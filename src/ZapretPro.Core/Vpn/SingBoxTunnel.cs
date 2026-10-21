using System.Diagnostics;
using System.Net.NetworkInformation;
using ZapretPro.Core.Infrastructure;

namespace ZapretPro.Core.Vpn;

/// <summary>
/// Туннель по подписке через sing-box (SagerNet/sing-box): процесс программы, а не служба Windows,
/// поэтому завершается вместе с программой. Адаптер называется так же, как у AmneziaWG («FiolKid»):
/// одновременно работает только один туннель, и маршрутами управляет тот же <see cref="TunnelRoutes"/>.
/// </summary>
public sealed class SingBoxTunnel : IVpnTunnel
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);

    private readonly string _dir;
    private readonly string _confPath;
    private readonly Func<CancellationToken, Task<TimeSpan?>> _probe;
    private readonly OutputTail _output = new();
    private Process? _process;

    public SingBoxTunnel() : this(Path.Combine(AppPaths.InstallDir, "sing-box"), ProtectedFolder.Root)
    {
    }

    public SingBoxTunnel(string dir, string confDir, Func<CancellationToken, Task<TimeSpan?>>? probe = null)
    {
        _dir = dir;
        _confPath = Path.Combine(confDir, "sing-box.json");
        _probe = probe ?? TunnelProbe.MeasureAsync;
    }

    public string? LastError { get; private set; }

    private string Exe => Path.Combine(_dir, "sing-box.exe");

    public async Task<bool> StartAsync(string conf, CancellationToken ct)
    {
        await StopAsync(ct);
        LastError = null;
        if (!File.Exists(Exe))
        {
            LastError = $"Не найден {Exe}. Возможно, его удалил антивирус.";
            return false;
        }

        KillOurStrayCopies();
        ProtectedFolder.Write(Path.GetDirectoryName(_confPath)!, _confPath, conf);
        _output.Clear();

        var process = new Process
        {
            StartInfo = new ProcessStartInfo(Exe, $"run -c \"{_confPath}\" --disable-color")
            {
                WorkingDirectory = _dir,
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
            LastError = $"Не удалось запустить sing-box: {e.Message}";
            process.Dispose();
            return false;
        }
        _output.Attach(process);
        _process = process;

        // Готов, когда появился адаптер; упал — значит, конфиг или система ему не понравились
        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                LastError = $"sing-box завершился сразу после запуска: {string.Join(" | ", _output.Lines.TakeLast(3))}";
                _process = null;
                process.Dispose();
                return false;
            }
            if (AdapterUp())
                return true;
            await Task.Delay(300, ct);
        }

        LastError = $"sing-box не создал адаптер за {StartupTimeout.TotalSeconds:0} с: {string.Join(" | ", _output.Lines.TakeLast(3))}";
        await StopAsync(ct);
        return false;
    }

    public Task StopAsync(CancellationToken ct)
    {
        var process = _process;
        _process = null;
        ChildProcesses.KillTree(process);
        process?.Dispose();
        try
        {
            if (File.Exists(_confPath))
                File.Delete(_confPath);
        }
        catch (IOException)
        {
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// «Рукопожатия», как у WireGuard, у sing-box нет. Проверяем делом: запрос к Cloudflare 1.1.1.1,
    /// который программа всегда направляет в туннель, — ответил, значит, сервер подписки работает.
    /// </summary>
    public async Task<TunnelHealth> GetHealthAsync(CancellationToken ct)
    {
        if (_process is not { HasExited: false })
            return TunnelHealth.Stopped;
        if (await _probe(ct) is not null)
            return TunnelHealth.Connected;
        LastError = "серверы подписки не отвечают";
        return TunnelHealth.NoHandshake;
    }

    public IReadOnlyList<string> RecentOutput => _output.Lines;

    private static bool AdapterUp()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Any(n => n.Name.Equals(VpnManager.TunnelName, StringComparison.OrdinalIgnoreCase));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private void KillOurStrayCopies()
    {
        foreach (var process in Process.GetProcessesByName("sing-box"))
        {
            using (process)
            {
                if (string.Equals(ChildProcesses.PathOf(process), Exe, StringComparison.OrdinalIgnoreCase))
                    ChildProcesses.KillTree(process);
            }
        }
    }
}

/// <summary>Работает ли туннель и насколько быстро: запрос к https://1.1.1.1 (этот адрес всегда идёт в туннель).</summary>
public static class TunnelProbe
{
    public static async Task<TimeSpan?> MeasureAsync(CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.Zero };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await http.GetAsync("https://1.1.1.1/cdn-cgi/trace", HttpCompletionOption.ResponseHeadersRead, ct);
            return response.IsSuccessStatusCode ? sw.Elapsed : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
