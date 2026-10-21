using ZapretPro.Core.Key;

namespace ZapretPro.Core.Vpn;

public enum VpnState
{
    /// <summary>Ключ ещё не вставлен.</summary>
    NoKey,
    /// <summary>Ключ есть, но не подходит (не AmneziaWG или повреждён).</summary>
    BadKey,
    Stopped,
    /// <summary>Туннель только что поднят, ждём первого ответа сервера.</summary>
    Connecting,
    Connected,
    /// <summary>Туннель поднят, но сервер не отвечает.</summary>
    ServerNotResponding,
    Failed,
}

public sealed record VpnStatus(VpnState State, int RouteCount, IReadOnlySet<RouteSet> Fallback, string? Error)
{
    public static readonly VpnStatus Initial = new(VpnState.Stopped, 0, new HashSet<RouteSet>(), null);
}

public interface IVpn
{
    bool HasKey { get; }
    VpnStatus Status { get; }

    /// <summary>
    /// Поднимает туннель с нейросетями и указанными запасными наборами. Если маршруты не изменились
    /// и туннель работает — ничего не перезапускает.
    /// </summary>
    Task<VpnStatus> ApplyAsync(IReadOnlySet<RouteSet> fallback, CancellationToken ct);

    Task StopAsync(CancellationToken ct);
}

/// <summary>Связывает ключ, маршруты и туннель.</summary>
public sealed class VpnController : IVpn
{
    // Как часто заново узнавать IP доменов через DNS
    private static readonly TimeSpan ResolveInterval = TimeSpan.FromMinutes(10);

    // Сколько ждать первого рукопожатия сразу после запуска туннеля, и сколько после запуска
    // считать молчание сервера нормальным «подключаюсь», а не «сервер не отвечает»
    private static readonly TimeSpan HandshakeWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectingGrace = TimeSpan.FromMinutes(1);

    private const string SilentServerMessage =
        "VPN-сервер не отвечает. Если этот же ключ сейчас включён где-то ещё (Amnezia VPN, телефон, другой компьютер) — " +
        "выключи его там: один ключ работает только на одном устройстве одновременно. " +
        "Если нигде больше не включён — возможно, знакомый выключил сервер или сменил ключ.";

    private readonly KeyStore _keys;
    private readonly Func<Router> _routerFactory;
    private readonly IVpnTunnel _tunnel;
    private readonly TimeProvider _time;

    private Router? _router;
    private string? _keyLoaded;
    private AwgConfig? _config;
    private string? _keyError;
    private HashSet<RouteSet> _resolvedFor = [];
    private DateTimeOffset _resolvedAt;
    private RoutePlan? _plan;
    private string? _runningConf;
    private DateTimeOffset _startedAt;
    private readonly TimeSpan _handshakeWait;

    public VpnController(KeyStore keys, Func<Router> routerFactory, IVpnTunnel tunnel, TimeProvider? time = null,
        TimeSpan? handshakeWait = null)
    {
        _keys = keys;
        _routerFactory = routerFactory;
        _tunnel = tunnel;
        _time = time ?? TimeProvider.System;
        _handshakeWait = handshakeWait ?? HandshakeWait;
    }

    public bool HasKey => _keys.HasKey;

    public VpnStatus Status { get; private set; } = VpnStatus.Initial;

    public event Action<string>? Log;

    public async Task<VpnStatus> ApplyAsync(IReadOnlySet<RouteSet> fallback, CancellationToken ct)
    {
        var sets = new HashSet<RouteSet>(fallback) { RouteSet.Ai, RouteSet.Blocked };

        if (!LoadKey())
        {
            await StopTunnelAsync(ct);
            return Status = new VpnStatus(_keyError is null ? VpnState.NoKey : VpnState.BadKey, 0, fallback, _keyError);
        }

        if (_plan is null || !_resolvedFor.SetEquals(sets) || _time.GetUtcNow() - _resolvedAt >= ResolveInterval)
        {
            try
            {
                _router ??= _routerFactory();
                _plan = await _router.ComputeAsync(sets, _config!.EndpointHost, ct);
            }
            catch (InvalidDataException e)
            {
                return Status = new VpnStatus(VpnState.Failed, 0, fallback, e.Message);
            }
            _resolvedFor = sets;
            _resolvedAt = _time.GetUtcNow();
            foreach (var warning in _plan.Warnings)
                Log?.Invoke(warning);
        }

        if (_plan.AllowedIps.Count == 0)
            return Status = new VpnStatus(VpnState.Failed, 0, fallback, "Не удалось узнать адреса нейросетей — нет интернета или DNS не отвечает.");

        var conf = _config!.ToConf(_plan.AllowedIps);
        var health = conf == _runningConf ? await _tunnel.GetHealthAsync(ct) : TunnelHealth.Stopped;

        if (health == TunnelHealth.Stopped)
        {
            Log?.Invoke(_runningConf is null
                ? $"Подключаю VPN ({_plan.AllowedIps.Count} маршрутов)"
                : $"Обновляю маршруты VPN ({_plan.AllowedIps.Count})");
            if (!await _tunnel.StartAsync(conf, ct))
            {
                _runningConf = null;
                return Status = new VpnStatus(VpnState.Failed, 0, fallback, _tunnel.LastError);
            }
            _runningConf = conf;
            _startedAt = _time.GetUtcNow();
            health = await WaitForHandshakeAsync(ct);
        }

        if (health == TunnelHealth.Connected)
            return Status = new VpnStatus(VpnState.Connected, _plan.AllowedIps.Count, fallback, null);

        if (_time.GetUtcNow() - _startedAt < ConnectingGrace)
            return Status = new VpnStatus(VpnState.Connecting, _plan.AllowedIps.Count, fallback, null);

        Log?.Invoke($"VPN: {_tunnel.LastError}");
        return Status = new VpnStatus(VpnState.ServerNotResponding, _plan.AllowedIps.Count, fallback, SilentServerMessage);
    }

    /// <summary>Первое рукопожатие происходит через секунду-другую после запуска службы — ждём его, а не проверяем сразу.</summary>
    private async Task<TunnelHealth> WaitForHandshakeAsync(CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + _handshakeWait;
        while (true)
        {
            var health = await _tunnel.GetHealthAsync(ct);
            if (health != TunnelHealth.NoHandshake || _time.GetUtcNow() >= deadline)
                return health;
            await Task.Delay(TimeSpan.FromSeconds(1), _time, ct);
        }
    }

    /// <summary>Списки сайтов обновились — при следующем <see cref="ApplyAsync"/> перечитать их и пересчитать маршруты.</summary>
    public void ReloadLists()
    {
        _router = null;
        _plan = null;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await StopTunnelAsync(ct);
        Status = Status with { State = HasKey ? VpnState.Stopped : VpnState.NoKey, RouteCount = 0 };
    }

    /// <summary>Читает ключ заново, если его заменили. Возвращает false, если ключа нет или он негодный.</summary>
    private bool LoadKey()
    {
        var key = _keys.Load();
        if (key is null)
        {
            _keyLoaded = null;
            _config = null;
            _keyError = _keys.HasKey ? "Сохранённый ключ не читается. Вставь ключ заново." : null;
            return false;
        }

        if (key == _keyLoaded)
            return _config is not null;

        _keyLoaded = key;
        _plan = null;
        try
        {
            _config = AwgConfig.FromKey(KeyParser.Parse(key));
            _keyError = null;
            return true;
        }
        catch (KeyParseException e)
        {
            _config = null;
            _keyError = e.Message;
            return false;
        }
    }

    private async Task StopTunnelAsync(CancellationToken ct)
    {
        if (_runningConf is null)
            return;
        Log?.Invoke("Отключаю VPN");
        await _tunnel.StopAsync(ct);
        _runningConf = null;
    }
}
