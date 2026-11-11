using System.Diagnostics;
using System.IO;
using ZapretPro.Core.Dns;
using ZapretPro.Core.Infrastructure;
using ZapretPro.Core.Vpn;

namespace ZapretPro.App;

/// <summary>
/// Сторож DNS: второй FiolKid.exe с ключом --dns-guard. Ждёт, пока основная программа завершится, и, если
/// она упала, не вернув DNS Windows, возвращает его сам. Иначе после аварии у человека не открывался бы
/// ни один сайт до следующего запуска FiolKid. Запускается до Job Object, чтобы пережить основной процесс.
/// </summary>
internal static class DnsRecoveryGuard
{
    internal const string InstanceMutex = @"Local\FiolKid";
    private static string ReadyName(int pid) => $@"Local\FiolKid-DnsGuard-{pid}";

    public static void Start()
    {
        using var owner = Process.GetCurrentProcess();
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyName(owner.Id));
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "--dns-guard", owner.Id.ToString(), owner.StartTime.ToUniversalTime().Ticks.ToString() },
        }) ?? throw new InvalidOperationException("сторож не запустился");
        if (!ready.WaitOne(TimeSpan.FromSeconds(10)))
        {
            if (!child.HasExited)
                child.Kill();
            throw new InvalidOperationException("сторож не ответил за 10 секунд");
        }
    }

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private const int StrikesBeforeRestore = 3;

    /// <summary>
    /// Пока программа работает и DNS Windows переключён на неё, раз в 30 секунд спрашивает наш DNS
    /// (127.0.0.1:53). Три раза подряд не ответил — программа зависла или её DNS сломался, и у человека
    /// не открывается ни один сайт: возвращаем DNS Windows как был, не дожидаясь, пока программу закроют.
    /// </summary>
    private static async Task WatchUntilExitAsync(Process owner)
    {
        var exit = owner.WaitForExitAsync();
        var strikes = 0;
        while (true)
        {
            await Task.WhenAny(exit, Task.Delay(CheckInterval));
            if (exit.IsCompleted)
                return;
            if (!File.Exists(DnsHijack.BackupPath) || await Task.Run(() => SystemDns.LoopbackAnswers()))
            {
                strikes = 0;
                continue;
            }
            if (++strikes < StrikesBeforeRestore)
                continue;
            strikes = 0;
            try
            {
                var system = new SystemDns(DnsHijack.BackupPath, VpnManager.TunnelName);
                if (await system.RestoreAsync())
                    DnsRecoveryNote.Add("свой DNS программы перестал отвечать — вернул DNS Windows как был, чтобы не пропал интернет");
                else
                    DnsRecoveryNote.Add("свой DNS программы перестал отвечать, а вернуть DNS Windows до конца не вышло — попробую ещё");
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                DnsRecoveryNote.Add($"свой DNS программы перестал отвечать, а вернуть DNS Windows не вышло: {e.Message}");
            }
        }
    }

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[1], out var pid) || !long.TryParse(args[2], out var ticks))
            return;
        try
        {
            using var owner = Process.GetProcessById(pid);
            // Тот же процесс, а не новый с тем же номером
            if (owner.StartTime.ToUniversalTime().Ticks != ticks ||
                !string.Equals(owner.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                return;
            using (var ready = EventWaitHandle.OpenExisting(ReadyName(pid)))
                ready.Set();
            await WatchUntilExitAsync(owner);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
                                    or WaitHandleCannotBeOpenedException)
        {
            return;
        }

        // Если уже запустилась новая копия программы, DNS вернёт она сама. Блокировка принадлежит
        // потоку, поэтому после захвата — без await
        using var instance = new Mutex(false, InstanceMutex);
        var acquired = false;
        try
        {
            try { acquired = instance.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
                return;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Task.Run(() => DnsHijack.RecoverAsync(VpnManager.TunnelName)).GetAwaiter().GetResult();
                    return;
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    try
                    {
                        Directory.CreateDirectory(AppPaths.DataDir);
                        File.WriteAllText(Path.Combine(AppPaths.DataDir, "dns-recovery-error.txt"),
                            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}: {e.Message}");
                    }
                    catch (IOException)
                    {
                    }
                    Thread.Sleep(1000);
                }
            }
        }
        finally
        {
            if (acquired)
                instance.ReleaseMutex();
        }
    }
}
