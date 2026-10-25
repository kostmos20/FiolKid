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
            await owner.WaitForExitAsync();
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
