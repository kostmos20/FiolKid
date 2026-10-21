using ZapretPro.Core.Key;

namespace ZapretPro.Core.Vpn;

public enum VpnState
{
    /// <summary>Ключ ещё не вставлен.</summary>
    NoKey,
    /// <summary>Ключ есть, но не подходит (не AmneziaWG или повреждён).</summary>
    BadKey,
    Stopped,
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

    public VpnController(KeyStore keys, Func<Router> routerFactory, IVpnTunnel tunnel, TimeProvider? time = null)
    {
        _keys = keys;
        _routerFactory = routerFactory;
        _tunnel = tunnel;
        _time = time ?? TimeProvider.System;
    }

    public bool HasKey => _keys.HasKey;

    public VpnStatus Status { get; private set; } = VpnStatus.Initial;

    public event Action<string>? Log;

    public async Task<VpnStatus> ApplyAsync(IReadOnlySet<RouteSet> fallback, CancellationToken ct)
    {
        var sets = new HashSet<RouteSet>(fallback) { RouteSet.Ai };

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
            health = await _tunnel.GetHealthAsync(ct);
        }

        var state = health == TunnelHealth.Connected ? VpnState.Connected : VpnState.ServerNotResponding;
        return Status = new VpnStatus(state, _plan.AllowedIps.Count, fallback,
            state == VpnState.Connected ? null : "VPN-сервер не отвечает: возможно, знакомый выключил сервер или сменил ключ.");
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
