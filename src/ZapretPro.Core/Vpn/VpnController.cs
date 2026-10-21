using System.Net;
using System.Net.Sockets;
using ZapretPro.Core.Dns;
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

    /// <summary>Работает ли наш DNS (адреса сайтов из списков и их поддоменов добавляются на лету).</summary>
    public bool SmartDns { get; init; }
}

public interface IVpn
{
    bool HasKey { get; }
    VpnStatus Status { get; }

    /// <summary>
    /// Поднимает туннель (если ещё не поднят) и приводит маршруты к нужным: нейросети, заблокированные
    /// сайты и указанные запасные наборы. Смена наборов VPN не переподключает — меняются только маршруты.
    /// </summary>
    Task<VpnStatus> ApplyAsync(IReadOnlySet<RouteSet> fallback, CancellationToken ct);

    Task StopAsync(CancellationToken ct);
}

/// <summary>
/// Связывает ключ, туннель, маршруты и наш DNS.
/// Туннель поднимается с «Table = off» и пускает в шифрование всё, что направит Windows; направляет
/// программа: подсети из списков — сразу, адреса сайтов из списков (с любыми поддоменами) — в момент,
/// когда их спросит браузер (<see cref="SmartDns"/>).
/// </summary>
public sealed class VpnController : IVpn
{
    // Сколько ждать первого рукопожатия сразу после запуска туннеля, и сколько после запуска
    // считать молчание сервера нормальным «подключаюсь», а не «сервер не отвечает»
    private static readonly TimeSpan HandshakeWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectingGrace = TimeSpan.FromMinutes(1);
    // Адреса, которые узнал наш DNS, держим в маршрутах неделю после последнего вопроса о них
    private static readonly TimeSpan LearnedLifetime = TimeSpan.FromDays(7);
    // Если наш DNS не запустился, пробуем снова не чаще раза в 10 минут
    private static readonly TimeSpan DnsRetry = TimeSpan.FromMinutes(10);

    private static readonly RouteSet[] MatchOrder = [RouteSet.Ai, RouteSet.Blocked, RouteSet.Telegram, RouteSet.Discord, RouteSet.YouTube];

    private const string SilentServerMessage =
        "VPN-сервер не отвечает. Если этот же ключ сейчас включён где-то ещё (Amnezia VPN, телефон, другой компьютер) — " +
        "выключи его там: один ключ работает только на одном устройстве одновременно. " +
        "Если нигде больше не включён — возможно, знакомый выключил сервер или сменил ключ.";

    private readonly KeyStore _keys;
    private readonly Func<Router> _routerFactory;
    private readonly IVpnTunnel _tunnel;
    private readonly IRouteTable _routes;
    private readonly IDnsHijack? _dns;
    private readonly TimeProvider _time;
    private readonly TimeSpan _handshakeWait;
    private readonly Func<IReadOnlyList<string>> _otherVpns;

    private readonly object _lock = new();
    private readonly Dictionary<IPAddress, (RouteSet Set, DateTimeOffset Seen)> _learned = new();
    private HashSet<RouteSet> _activeSets = [];
    private IReadOnlyList<IPAddress> _endpoints = [];

    private Router? _router;
    private string? _keyLoaded;
    private AwgConfig? _config;
    private string? _keyError;
    private string? _runningConf;
    private DateTimeOffset _startedAt;
    private HashSet<RouteSet>? _matcherFor;
    private DateTimeOffset _dnsRetryAt;

    public VpnController(KeyStore keys, Func<Router> routerFactory, IVpnTunnel tunnel, IRouteTable routes, IDnsHijack? dns,
        TimeProvider? time = null, TimeSpan? handshakeWait = null, Func<IReadOnlyList<string>>? otherVpns = null)
    {
        _keys = keys;
        _routerFactory = routerFactory;
        _tunnel = tunnel;
        _routes = routes;
        _dns = dns;
        _time = time ?? TimeProvider.System;
        _handshakeWait = handshakeWait ?? HandshakeWait;
        _otherVpns = otherVpns ?? (() => OtherVpn.Detect(VpnManager.TunnelName));
    }

    public bool HasKey => _keys.HasKey;

    /// <summary>Маршруты, которые сейчас ведут в туннель — для разбора проблем.</summary>
    public IReadOnlyList<string> CurrentRoutes => _runningConf is null ? [] : _routes.Installed;

    public VpnStatus Status { get; private set; } = VpnStatus.Initial;

    public event Action<string>? Log;

    public async Task<VpnStatus> ApplyAsync(IReadOnlySet<RouteSet> fallback, CancellationToken ct)
    {
        var sets = new HashSet<RouteSet>(fallback) { RouteSet.Ai, RouteSet.Blocked };

        if (!LoadKey())
        {
            await StopAllAsync(ct);
            return Status = new VpnStatus(_keyError is null ? VpnState.NoKey : VpnState.BadKey, 0, fallback, _keyError);
        }

        RoutePlan plan;
        try
        {
            _router ??= _routerFactory();
            // Пока работает наш DNS, домены заранее не резолвим — их адреса придут на лету
            plan = await _router.ComputeAsync(sets, _config!.EndpointHost, ct, resolveDomains: _dns is not { Active: true });
        }
        catch (InvalidDataException e)
        {
            return Status = new VpnStatus(VpnState.Failed, 0, fallback, e.Message);
        }
        foreach (var warning in plan.Warnings)
            Log?.Invoke(warning);

        lock (_lock)
        {
            _activeSets = sets;
            _endpoints = plan.Endpoints;
        }

        // Конфиг от маршрутов больше не зависит: туннель перезапускается только при смене ключа или если упал
        var conf = _config!.ToConf(["0.0.0.0/0"], tableOff: true);
        var health = conf == _runningConf ? await _tunnel.GetHealthAsync(ct) : TunnelHealth.Stopped;

        if (health == TunnelHealth.Stopped)
        {
            Log?.Invoke("Подключаю VPN");
            _routes.Reset();
            if (!await _tunnel.StartAsync(conf, ct))
            {
                _runningConf = null;
                return Status = new VpnStatus(VpnState.Failed, 0, fallback, Explain(_tunnel.LastError));
            }
            _runningConf = conf;
            _startedAt = _time.GetUtcNow();
        }

        SyncRoutes(plan);
        await EnsureDnsAsync(sets);

        if (health == TunnelHealth.Stopped)
            health = await WaitForHandshakeAsync(ct);

        var count = _routes.Installed.Count;
        var smart = _dns is { Active: true };
        if (health == TunnelHealth.Connected)
            return Status = new VpnStatus(VpnState.Connected, count, fallback, null) { SmartDns = smart };

        if (_time.GetUtcNow() - _startedAt < ConnectingGrace)
            return Status = new VpnStatus(VpnState.Connecting, count, fallback, null) { SmartDns = smart };

        Log?.Invoke($"VPN: {_tunnel.LastError}");
        var others = _otherVpns();
        return Status = new VpnStatus(VpnState.ServerNotResponding, count, fallback,
            others.Count > 0 ? Explain(_tunnel.LastError) : SilentServerMessage) { SmartDns = smart };
    }

    /// <summary>
    /// Наш DNS узнал адреса сайта, который идёт через VPN: сразу ведём их в туннель — до того,
    /// как браузер получит ответ и начнёт подключаться.
    /// </summary>
    internal Task OnResolvedAsync(IReadOnlyList<IPAddress> addresses, RouteSet set)
    {
        var now = _time.GetUtcNow();
        var add = new List<IPAddress>();
        lock (_lock)
        {
            foreach (var ip in addresses)
            {
                if (ip.AddressFamily != AddressFamily.InterNetwork || _endpoints.Contains(ip) || Router.NeverRoute.Any(n => n.Contains(ip)))
                    continue;
                _learned[ip] = (set, now);
                if (_activeSets.Contains(set))
                    add.Add(ip);
            }
        }
        if (_runningConf is not null)
        {
            foreach (var ip in add)
                _routes.Add(new IPNetwork(ip, 32));
        }
        return Task.CompletedTask;
    }

    /// <summary>Нужные маршруты: подсети из списков, DNS-серверы для вопросов через VPN и узнанные адреса активных наборов.</summary>
    private void SyncRoutes(RoutePlan plan)
    {
        var wanted = new HashSet<IPNetwork>(plan.AllowedIps.Select(IPNetwork.Parse));
        foreach (var upstream in SmartDns.SecureUpstreams)
            wanted.Add(new IPNetwork(upstream.Address, 32));

        var now = _time.GetUtcNow();
        lock (_lock)
        {
            foreach (var stale in _learned.Where(kv => now - kv.Value.Seen > LearnedLifetime).Select(kv => kv.Key).ToList())
                _learned.Remove(stale);
            foreach (var (ip, (set, _)) in _learned)
            {
                if (_activeSets.Contains(set) && !_endpoints.Contains(ip) && !wanted.Any(n => n.Contains(ip)))
                    wanted.Add(new IPNetwork(ip, 32));
            }
        }
        _routes.Sync(wanted);
    }

    private async Task EnsureDnsAsync(HashSet<RouteSet> sets)
    {
        if (_dns is null || _router is null)
            return;

        if (_matcherFor is null || !_matcherFor.SetEquals(sets) || !_dns.Active)
        {
            var lists = _router.Lists;
            var matcher = new DomainMatcher(MatchOrder.Where(sets.Contains).SelectMany(s => lists.Domains[s].Select(d => (d, s))));
            Func<string, RouteSet?> classify = matcher.Match;
            _matcherFor = sets;

            if (_dns.Active)
            {
                _dns.UpdateClassifier(classify);
                return;
            }
            if (_time.GetUtcNow() < _dnsRetryAt)
                return;

            if (await _dns.StartAsync(classify, OnResolvedAsync, () => _runningConf is not null))
                Log?.Invoke($"Свой DNS включён: сайты из списков ({matcher.Count} доменов) и все их поддомены идут через VPN");
            else
            {
                _dnsRetryAt = _time.GetUtcNow() + DnsRetry;
                Log?.Invoke($"Свой DNS не включился: {_dns.LastError}");
            }
        }
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

    /// <summary>
    /// Если включён другой VPN, почти наверняка дело в нём — говорим об этом прямо, а техническую причину
    /// оставляем следом для журнала.
    /// </summary>
    private string Explain(string? reason)
    {
        var others = _otherVpns();
        if (others.Count == 0)
            return reason ?? "неизвестная ошибка";
        Log?.Invoke($"Включены другие VPN: {string.Join(", ", others)}");
        return $"Включён другой VPN ({string.Join(", ", others)}). Выключи его: если там тот же ключ, " +
               $"два VPN мешают друг другу и наш не может подключиться. Подробности: {reason}";
    }

    /// <summary>Списки сайтов обновились — при следующем <see cref="ApplyAsync"/> перечитать их.</summary>
    public void ReloadLists()
    {
        _router = null;
        _matcherFor = null;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await StopAllAsync(ct);
        Status = Status with { State = HasKey ? VpnState.Stopped : VpnState.NoKey, RouteCount = 0, SmartDns = false };
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

    /// <summary>Сначала вернуть DNS Windows, потом снять туннель — чтобы ни секунды не остаться без DNS.</summary>
    private async Task StopAllAsync(CancellationToken ct)
    {
        if (_dns is { Active: true })
            await _dns.StopAsync();
        _matcherFor = null;

        if (_runningConf is null)
            return;
        Log?.Invoke("Отключаю VPN");
        await _tunnel.StopAsync(ct);
        _runningConf = null;
        _routes.Reset();
    }
}
