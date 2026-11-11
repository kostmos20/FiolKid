using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ZapretPro.Core.Infrastructure;
using ZapretPro.Core.Vpn;

namespace ZapretPro.Core.Dns;

/// <summary>Наш DNS вместо системного — пока работает VPN.</summary>
public interface IDnsHijack
{
    bool Active { get; }
    string? LastError { get; }

    Task<bool> StartAsync(Func<string, RouteSet?> classify, Func<IReadOnlyList<IPAddress>, RouteSet, Task> onResolved, Func<bool> tunnelUp);

    /// <summary>Сменить, какие домены идут через VPN (включился или выключился запасной режим).</summary>
    void UpdateClassifier(Func<string, RouteSet?> classify);

    /// <summary>Последние сайты, отправленные в VPN, — для отчёта.</summary>
    IReadOnlyList<string> Recent { get; }

    /// <summary>Запросы через туннель часто теряются — VPN работает с перебоями.</summary>
    bool Unstable { get; }

    Task StopAsync();

    /// <summary>VPN сейчас выключится (переподключение): снять блокировку DNS, пока адаптер не исчез.</summary>
    void TunnelStopping();

    /// <summary>Имя, по которому недавно спрашивали этот адрес, — чтобы запомнить домены игр.</summary>
    string? NameOf(IPAddress address);

    /// <summary>
    /// Свой DNS выключен, а DNS Windows вернуть до конца не вышло — попробовать ещё раз. Зовётся по таймеру
    /// и при смене сети. Пока свой DNS работает — подхватить DNS новой сети.
    /// </summary>
    Task RetryRestoreAsync();
}

/// <summary>
/// Запускает <see cref="SmartDns"/> и направляет на него Windows. Два способа:
/// <list type="bullet">
/// <item>Основной (с 1.0.0): наш DNS ставится только на адаптер VPN, а спрашивать DNS провайдера мимо нас
/// не дают фильтры WFP (<see cref="IDnsLeakGuard"/>). Физические адаптеры не трогаем вовсе, а фильтры
/// исчезают сами, если программа умерла, — DNS Windows застрять негде.</item>
/// <item>Запасной (как до 1.0.0): WFP не включился или Windows не спрашивает DNS адаптера VPN —
/// переключаем DNS всех адаптеров на наш, со всеми страховками возврата.</item>
/// </list>
/// Порядок важен: сначала занять порт, потом переключать — чтобы ни секунды не остаться без DNS.
/// Выключение — в обратном порядке.
/// </summary>
public sealed class DnsHijack : IDnsHijack
{
    private enum Mode { None, Tunnel, Adapters }

    private readonly SystemDns _system;
    private readonly IDnsLeakGuard _guard;
    private readonly Func<IReadOnlyList<string>> _allowedApps;
    private readonly Func<SmartDns, Task<string?>> _checkWindows;
    private readonly int _port;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SmartDns? _dns;
    private Mode _mode;
    private Timer? _watch;

    public DnsHijack(string ownAdapter) : this(new SystemDns(BackupPath, ownAdapter), new WfpDnsGuard(), AllowedApps,
        CheckWindowsAsync)
    {
    }

    public DnsHijack(SystemDns system, IDnsLeakGuard? guard = null, Func<IReadOnlyList<string>>? allowedApps = null,
        Func<SmartDns, Task<string?>>? checkWindows = null, int port = 53)
    {
        _port = port;
        _system = system;
        _guard = guard ?? new NoGuard();
        _allowedApps = allowedApps ?? (() => []);
        _checkWindows = checkWindows ?? (_ => Task.FromResult<string?>(null));
        _system.Log += m => Log?.Invoke(m);
    }

    public static string BackupPath => Path.Combine(AppPaths.DataDir, "dns-backup.json");

    /// <summary>
    /// Кому можно на порт 53 мимо нашего DNS: самой программе (её DNS пересылает вопросы провайдеру)
    /// и её VPN-программам (они узнают адреса серверов).
    /// </summary>
    private static IReadOnlyList<string> AllowedApps() =>
    [
        Environment.ProcessPath ?? "",
        Path.Combine(AppPaths.InstallDir, "sing-box", "sing-box.exe"),
        Path.Combine(AppPaths.InstallDir, "amneziawg", "amneziawg.exe"),
    ];

    public event Action<string>? Log;

    /// <summary>Работает основной способ (только адаптер VPN + WFP) — для отчёта.</summary>
    public bool TunnelMode => _dns is not null && _mode == Mode.Tunnel;

    /// <summary>
    /// Наш DNS работает и Windows направлена на него. Запасной способ: файла с прежними DNS нет, а DNS
    /// считается включённым — значит, сторож увидел, что наш DNS не отвечает, и вернул DNS Windows сам.
    /// Основной: адаптер VPN пропал или потерял наш DNS, или сняты фильтры WFP.
    /// </summary>
    public bool Active => _dns is not null && _mode switch
    {
        Mode.Tunnel => _guard.Enabled && _system.TunnelReady,
        Mode.Adapters => _system.HasBackup,
        _ => false,
    };

    public string? LastError { get; private set; }

    /// <summary>
    /// Программа упала, не вернув DNS, — вернуть при следующем запуске, первым делом. Заодно чинит DNS,
    /// застрявший на адресе программы без файла с прежними настройками (так бывало до 0.9.12).
    /// </summary>
    public static async Task RecoverAsync(string ownAdapter, Action<string>? log = null)
    {
        var system = new SystemDns(BackupPath, ownAdapter);
        if (log is not null)
            system.Log += log;
        await system.ClearTunnelAsync();
        if (system.HasBackup)
            await system.RestoreAsync();
        else
            await system.ResetStrayAsync(() => SystemDns.LoopbackAnswers());
    }

    public async Task<bool> StartAsync(Func<string, RouteSet?> classify, Func<IReadOnlyList<IPAddress>, RouteSet, Task> onResolved,
        Func<bool> tunnelUp)
    {
        await _gate.WaitAsync();
        try
        {
            return await StartCoreAsync(classify, onResolved, tunnelUp);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> StartCoreAsync(Func<string, RouteSet?> classify, Func<IReadOnlyList<IPAddress>, RouteSet, Task> onResolved,
        Func<bool> tunnelUp)
    {
        if (Active)
            return true;
        if (_dns is not null)
        {
            Log?.Invoke(_mode == Mode.Tunnel
                ? "Адаптер VPN пересоздан или потерял наш DNS — включаю свой DNS заново"
                // Сторож вернул DNS Windows: наш перестал отвечать
                : "Свой DNS перестал отвечать, и DNS Windows был возвращён как был — запускаю свой DNS заново");
            await ShutdownAsync();
        }
        LastError = null;

        // Если остались настройки от прошлого раза — вернуть, чтобы запомнить настоящие, а не наши
        await _system.ClearTunnelAsync();
        if (_system.HasBackup)
            await _system.RestoreAsync();
        var adapters = _system.Capture();

        var dns = new SmartDns(Upstreams(adapters), _port) { Classify = classify, OnResolved = onResolved, TunnelUp = tunnelUp };
        dns.Log += m => Log?.Invoke(m);
        try
        {
            dns.Start();
        }
        catch (SocketException e)
        {
            await dns.DisposeAsync();
            LastError = $"Порт DNS (53) занят другой программой ({e.SocketErrorCode}) — сайты из списков будут открываться не полностью";
            return false;
        }

        var tunnelProblem = await TryTunnelModeAsync(dns);
        if (tunnelProblem is null)
        {
            _dns = dns;
            _mode = Mode.Tunnel;
            _watch = new Timer(_ => CheckTunnel(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            Log?.Invoke("DNS: свой DNS только на адаптере VPN, DNS провайдера мимо него закрыт фильтром WFP. " +
                        "DNS Wi-Fi и сетевой карты не трогаю");
            return true;
        }
        Log?.Invoke($"DNS: основной способ не подошёл ({tunnelProblem}) — переключаю DNS адаптеров, как раньше");

        try
        {
            await _system.ActivateAsync(adapters);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await dns.DisposeAsync();
            await _system.RestoreAsync();
            LastError = $"Не удалось переключить DNS Windows: {e.Message}";
            return false;
        }

        _dns = dns;
        _mode = Mode.Adapters;
        return true;
    }

    private static List<IPEndPoint> Upstreams(IReadOnlyList<SystemDns.AdapterBackup> adapters)
    {
        var upstreams = adapters.SelectMany(a => a.EffectiveServers).Select(IPAddress.Parse)
            .Where(a => !IPAddress.IsLoopback(a)).Distinct().Select(a => new IPEndPoint(a, 53)).ToList();
        // DNS не было вообще (бывает в странных сетях) — Яндекс и Google, они работают из России
        if (upstreams.Count == 0)
            upstreams = [new(IPAddress.Parse("77.88.8.8"), 53), new(IPAddress.Parse("8.8.8.8"), 53)];
        return upstreams;
    }

    /// <summary>Основной способ. Null — включён; иначе причина, а всё, что успели поменять, убрано.</summary>
    private async Task<string?> TryTunnelModeAsync(SmartDns dns)
    {
        if (!_system.TunnelPresent)
            return "адаптера VPN нет";
        if (!await _system.ActivateTunnelAsync())
        {
            await _system.ClearTunnelAsync();
            return "Windows не поставила DNS адаптеру VPN";
        }
        var guardError = _guard.Enable(_allowedApps());
        if (guardError is not null)
        {
            await _system.ClearTunnelAsync();
            return guardError;
        }
        var checkError = await _checkWindows(dns);
        if (checkError is not null)
        {
            _guard.Disable();
            await _system.ClearTunnelAsync();
            return checkError;
        }
        return null;
    }

    /// <summary>
    /// Проверка основного способа вживую: спрашиваем уникальное имя через саму Windows (её службу DNS)
    /// и смотрим, что вопрос дошёл до нашего DNS и быстро. Если Windows сначала стучится к заблокированному
    /// DNS провайдера и ждёт — каждый сайт открывался бы с задержкой, и лучше запасной способ.
    /// </summary>
    internal static async Task<string?> CheckWindowsAsync(SmartDns dns)
    {
        var last = TimeSpan.Zero;
        var reached = false;
        // Windows подхватывает новый DNS адаптера не мгновенно — несколько попыток
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(700);
            var name = $"c{Guid.NewGuid():N}.{SmartDns.HealthName}";
            var watch = Stopwatch.StartNew();
            try
            {
                await System.Net.Dns.GetHostAddressesAsync(name).WaitAsync(TimeSpan.FromSeconds(4));
            }
            catch (Exception e) when (e is SocketException or TimeoutException)
            {
                // «Адресов нет» — так и задумано; таймаут — значит, не дошло
            }
            watch.Stop();
            reached = dns.SawHealthCheck(name);
            last = watch.Elapsed;
            if (reached && last < TimeSpan.FromMilliseconds(800))
                return null;
        }
        return reached
            ? $"Windows спрашивает наш DNS с задержкой {last.TotalMilliseconds:0} мс"
            : "Windows не спрашивает DNS адаптера VPN";
    }

    /// <summary>Адаптер VPN пропал (туннель упал) — снять блокировку сразу, иначе DNS не работал бы вовсе.</summary>
    internal void CheckTunnel()
    {
        if (_mode != Mode.Tunnel || !_guard.Enabled || _system.TunnelPresent)
            return;
        _guard.Disable();
        Log?.Invoke("Адаптер VPN пропал — снял блокировку DNS провайдера, пока VPN не вернётся");
    }

    public void TunnelStopping()
    {
        if (_mode == Mode.Tunnel)
            _guard.Disable();
    }

    public void UpdateClassifier(Func<string, RouteSet?> classify)
    {
        if (_dns is null)
            return;
        _dns.Classify = classify;
        // Windows помнит прежние ответы: без сброса, например, Discord после включения запасного режима
        // ещё минуты ходил бы на старые адреса мимо VPN
        SystemDns.FlushCache();
    }

    public IReadOnlyList<string> Recent => _dns?.Recent ?? [];

    public string? NameOf(IPAddress address) => _dns?.NameOf(address);

    public bool Unstable => _dns?.Unstable ?? false;

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await ShutdownAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Сначала вернуть DNS Windows (снять фильтры, убрать наш DNS с адаптеров), потом освободить порт.
    /// Не вернулся до конца — отметки остались, RetryRestoreAsync попробует ещё.
    /// </summary>
    private async Task ShutdownAsync()
    {
        var dns = _dns;
        _dns = null;
        _mode = Mode.None;
        _watch?.Dispose();
        _watch = null;
        try
        {
            _guard.Disable();
            await _system.ClearTunnelAsync();
            await _system.RestoreAsync();
        }
        finally
        {
            if (dns is not null)
                await dns.DisposeAsync();
        }
    }

    public async Task RetryRestoreAsync()
    {
        if (!await _gate.WaitAsync(0))
            return;
        try
        {
            if (_dns is null)
            {
                await _system.ClearTunnelAsync();
                if (_system.HasBackup)
                    await _system.RestoreAsync();
            }
            else if (_mode == Mode.Tunnel)
            {
                // Сменилась сеть: DNS адаптеров мы не трогали — обычные вопросы пересылаем DNS новой сети
                var upstreams = Upstreams(_system.Capture());
                if (!upstreams.SequenceEqual(_dns.NormalUpstreams))
                {
                    _dns.UpdateUpstreams(upstreams);
                    Log?.Invoke($"DNS: сеть сменилась, обычные вопросы теперь идут на {string.Join(", ", upstreams)}");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Без WFP: основной способ не включается.</summary>
    private sealed class NoGuard : IDnsLeakGuard
    {
        public bool Enabled => false;
        public string? Enable(IReadOnlyList<string> allowedApps) => "WFP не используется";

        public void Disable()
        {
        }
    }
}
