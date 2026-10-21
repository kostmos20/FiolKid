using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using ZapretPro.Core.Infrastructure;

namespace ZapretPro.Core.Vpn;

public enum TunnelHealth
{
    Stopped,
    /// <summary>Служба работает, но сервер не отвечает (нет свежего рукопожатия).</summary>
    NoHandshake,
    Connected,
}

public interface IVpnTunnel
{
    string? LastError { get; }
    Task<bool> StartAsync(string conf, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    Task<TunnelHealth> GetHealthAsync(CancellationToken ct);
}

/// <summary>
/// Туннель AmneziaWG через официальный amneziawg.exe (amnezia-vpn/amneziawg-windows-client):
/// /installtunnelservice ставит службу AmneziaWGTunnel$FiolKid, своего окна и иконки у неё нет.
/// Конфиг с приватным ключом лежит в папке, доступной только SYSTEM и администраторам,
/// и удаляется при остановке.
/// </summary>
public sealed class VpnManager : IVpnTunnel
{
    public const string TunnelName = "FiolKid";

    // Прежние имена программы. Туннель со старым именем мог остаться службой Windows с тем же ключом:
    // он занимает тот же адрес, и новый туннель не поднимается («The object already exists»).
    private static readonly string[] LegacyTunnelNames = ["OdnaKnopka"];

    // Без трафика клиент шлёт keepalive раз в 25 с, рукопожатие обновляется раз в 2 минуты
    private static readonly TimeSpan HandshakeFresh = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);

    private readonly string _awgDir;
    private readonly string _confDir;

    public VpnManager() : this(Path.Combine(AppPaths.InstallDir, "amneziawg"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FiolKid", "vpn"))
    {
    }

    public VpnManager(string awgDir, string confDir)
    {
        _awgDir = awgDir;
        _confDir = confDir;
    }

    public string? LastError { get; private set; }

    private string ConfPath => Path.Combine(_confDir, TunnelName + ".conf");
    private string AmneziaWg => Path.Combine(_awgDir, "amneziawg.exe");
    private string Awg => Path.Combine(_awgDir, "awg.exe");

    public async Task<bool> StartAsync(string conf, CancellationToken ct)
    {
        LastError = null;
        if (!File.Exists(AmneziaWg))
        {
            LastError = $"Не найден {AmneziaWg}. Возможно, его удалил антивирус.";
            return false;
        }

        await RemoveLegacyTunnelsAsync(ct);
        await UninstallAsync(TunnelName, ct);

        WriteProtected(conf);
        var (code, output) = await RunAsync(AmneziaWg, $"/installtunnelservice \"{ConfPath}\"", ct);
        if (code != 0)
        {
            LastError = $"Не удалось поставить службу VPN (код {code}): {output}";
            return false;
        }

        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            switch (QueryService())
            {
                case ServiceControllerStatus.Running:
                    return true;
                case ServiceControllerStatus.Stopped:
                    LastError = $"Служба VPN остановилась сразу после запуска: {await TunnelLogAsync(ct)}";
                    return false;
            }
            await Task.Delay(500, ct);
        }

        LastError = $"Служба VPN не запустилась за {StartupTimeout.TotalSeconds:0} с: {await TunnelLogAsync(ct)}";
        return false;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await UninstallAsync(TunnelName, ct);
        try
        {
            File.Delete(ConfPath);
        }
        catch (IOException)
        {
            // Удалим при следующем запуске
        }
    }

    public async Task<TunnelHealth> GetHealthAsync(CancellationToken ct)
    {
        if (QueryService() != ServiceControllerStatus.Running)
            return TunnelHealth.Stopped;

        // Вывод: «<публичный ключ сервера>\t<unix-время последнего рукопожатия>»
        var (code, output) = await RunAsync(Awg, $"show {TunnelName} latest-handshakes", ct);
        if (code != 0)
        {
            LastError = $"awg show не сработал (код {code}): {output}";
            return TunnelHealth.NoHandshake;
        }

        var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t', ' ').LastOrDefault())
            .Select(t => long.TryParse(t, out var s) ? s : 0)
            .DefaultIfEmpty(0)
            .Max();
        if (last > 0 && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(last) < HandshakeFresh)
            return TunnelHealth.Connected;

        LastError = last == 0
            ? "рукопожатия с сервером ещё не было"
            : $"последнее рукопожатие {(DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(last)).TotalMinutes:0} мин назад";
        return TunnelHealth.NoHandshake;
    }

    private async Task UninstallAsync(string tunnel, CancellationToken ct)
    {
        if (QueryService(tunnel) is null || !File.Exists(AmneziaWg))
            return;
        await RunAsync(AmneziaWg, $"/uninstalltunnelservice {tunnel}", ct);

        // Служба удаляется асинхронно; новая с тем же именем не встанет, пока старая не исчезнет
        for (var i = 0; i < 20 && QueryService(tunnel) is not null; i++)
            await Task.Delay(250, ct);
    }

    /// <summary>Убирает туннели прежних версий программы и их конфиги (в них приватный ключ).</summary>
    private async Task RemoveLegacyTunnelsAsync(CancellationToken ct)
    {
        foreach (var name in LegacyTunnelNames)
        {
            await UninstallAsync(name, ct);
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), name, "vpn");
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Попробуем при следующем запуске
            }
        }
    }

    private static ServiceControllerStatus? QueryService(string tunnel = TunnelName)
    {
        try
        {
            using var service = new ServiceController("AmneziaWGTunnel$" + tunnel);
            return service.Status;
        }
        catch (InvalidOperationException)
        {
            return null; // службы нет
        }
    }

    /// <summary>Папка с конфигом: доступ только у SYSTEM и администраторов, наследование прав отключено.</summary>
    private void WriteProtected(string conf)
    {
        var dir = Directory.CreateDirectory(_confDir);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
        dir.SetAccessControl(security);
        File.WriteAllText(ConfPath, conf);
    }

    /// <summary>Последние строки журнала AmneziaWG про наш туннель — для отчёта.</summary>
    private async Task<string> TunnelLogAsync(CancellationToken ct)
    {
        var (_, output) = await RunAsync(AmneziaWg, "/dumplog", ct);
        return FailureFromLog(output, TunnelName);
    }

    /// <summary>
    /// Из журнала AmneziaWG — причина последней остановки нашего туннеля. Последние строки журнала —
    /// это «worker … stopped» при выключении, они ничего не объясняют: берём строки с ошибкой.
    /// </summary>
    internal static string FailureFromLog(string dump, string tunnel)
    {
        var lines = dump.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Contains($"[{tunnel}]", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Только последний запуск: с последней строки «Starting …»
        var start = lines.FindLastIndex(l => l.Contains("Starting", StringComparison.OrdinalIgnoreCase));
        if (start >= 0)
            lines = lines[start..];

        var meaningful = lines
            .Where(l => !l.Contains("Routine:", StringComparison.OrdinalIgnoreCase))
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^\S+ \S+: (\[TUN\] )?", ""))
            .ToList();
        var errors = meaningful
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, "unable|fail|error|denied|exists|invalid|cannot",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .ToList();

        var picked = (errors.Count > 0 ? errors : meaningful).TakeLast(errors.Count > 0 ? 3 : 5).ToList();
        return picked.Count > 0 ? string.Join(" | ", picked) : "журнал AmneziaWG пуст";
    }

    private static async Task<(int Code, string Output)> RunAsync(string exe, string args, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return (-1, e.Message);
        }

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ChildProcesses.KillTree(process);
            return (-1, "нет ответа за 30 с");
        }
        return (process.ExitCode, ((await stdout) + (await stderr)).Trim());
    }
}
