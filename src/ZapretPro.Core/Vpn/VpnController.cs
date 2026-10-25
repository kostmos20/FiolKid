using System.Net;
using System.Net.Sockets;
using ZapretPro.Core.Dns;
using ZapretPro.Core.Infrastructure;

namespace ZapretPro.Core.Vpn;

public enum VpnState
{
    /// <summary>Ни ключа, ни подписки не вставлено.</summary>
    NoKey,
    /// <summary>Вставленное не подходит (ключ повреждён, подписка без подходящих серверов и т. п.).</summary>
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

    /// <summary>Через что сейчас идёт VPN: «ключ Amnezia», «подписка «Quattro VPN»».</summary>
    public string? Profile { get; init; }
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
/// Связывает способы подключения (ключ Amnezia, подписка), туннель, маршруты и наш DNS.
/// Туннель пускает в шифрование всё, что направит Windows; направляет программа: подсети из списков —
/// сразу, адреса сайтов из списков (с любыми поддоменами) — в момент, когда их спросит браузер (<see cref="SmartDns"/>).
/// Если вставлено несколько способов — работает тот, что подключается и быстрее; упал — переходим на другой.
/// </summary>
public sealed class VpnController : IVpn
{
    // Сколько ждать первого ответа сервера сразу после запуска туннеля, и сколько после запуска
    // считать молчание сервера нормальным «подключаюсь», а не «сервер не отвечает»
    private static readonly TimeSpan HandshakeWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ConnectingGrace = TimeSpan.FromMinutes(1);
    // Адреса, которые узнал наш DNS, держим в маршрутах неделю после последнего вопроса о них
    private static readonly TimeSpan LearnedLifetime = TimeSpan.FromDays(7);
    // Если наш DNS не запустился, пробуем снова не чаще раза в 10 минут
    private static readonly TimeSpan DnsRetry = TimeSpan.FromMinutes(10);
    // Способ подключения, который не заработал, откладываем, чтобы не дёргать его каждую проверку
    private static readonly TimeSpan FailedCooldown = TimeSpan.FromMinutes(15);
    // Как часто заново сравнивать скорость, если вставлено несколько способов, и насколько другой
    // должен быть быстрее, чтобы на него перейти
    private static readonly TimeSpan CompareInterval = TimeSpan.FromDays(1);
    private const double SwitchIfFasterBy = 0.3;

    private static readonly RouteSet[] MatchOrder = [RouteSet.Ai, RouteSet.Blocked, RouteSet.Telegram, RouteSet.Discord, RouteSet.YouTube];

    private const string SilentServerMessage =
        "VPN-сервер не отвечает. Если этот же ключ сейчас включён где-то ещё (Amnezia VPN, телефон, другой компьютер) — " +
        "выключи его там: один ключ работает только на одном устройстве одновременно. " +
        "Если нигде больше не включён — возможно, знакомый выключил сервер или сменил ключ.";

    private readonly IReadOnlyList<IVpnProfile> _profiles;
    private readonly Func<Router> _routerFactory;
    private readonly IRouteTable _routes;
    private readonly IDnsHijack? _dns;
    private readonly SettingsStore? _settings;
    private readonly TimeProvider _time;
    private readonly TimeSpan _handshakeWait;
    private readonly Func<IReadOnlyList<string>> _otherVpns;
    private readonly Func<CancellationToken, Task<TimeSpan?>> _measure;

    private readonly object _lock = new();
    private readonly Dictionary<IPAddress, (RouteSet Set, DateTimeOffset Seen)> _learned = new();
    private readonly Dictionary<IVpnProfile, DateTimeOffset> _cooldown = new();
    private HashSet<RouteSet> _activeSets = [];
    private IReadOnlyList<IPAddress> _endpoints = [];

    private Router? _router;
    private IVpnProfile? _active;
    private string? _runningConf;
    private DateTimeOffset _startedAt;
    private DateTimeOffset? _lastCompare;
    private HashSet<RouteSet>? _matcherFor;
    private DateTimeOffset _dnsRetryAt;

    public VpnController(IReadOnlyList<IVpnProfile> profiles, Func<Router> routerFactory, IRouteTable routes, IDnsHijack? dns,
        SettingsStore? settings = null, TimeProvider? time = null, TimeSpan? handshakeWait = null,
        Func<IReadOnlyList<string>>? otherVpns = null, Func<CancellationToken, Task<TimeSpan?>>? measure = null)
    {
        _profiles = profiles;
        _routerFactory = routerFactory;
        _routes = routes;
        _dns = dns;
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _handshakeWait = handshakeWait ?? HandshakeWait;
        _otherVpns = otherVpns ?? (() => OtherVpn.Detect(VpnManager.TunnelName));
        _measure = measure ?? TunnelProbe.MeasureAsync;
    }

    public bool HasKey => _profiles.Any(p => p.Configured);

    /// <summary>Последние сайты, которые наш DNS отправил в VPN, — для отчёта.</summary>
    public IReadOnlyList<string> RecentDns => _dns?.Recent ?? [];

    /// <summary>Запросы через туннель часто теряются — VPN работает с перебоями (обычно ключ включён ещё где-то).</summary>
    public bool Unstable => _dns?.Unstable ?? false;

    /// <summary>Маршруты, которые сейчас ведут в туннель — для разбора проблем.</summary>
    public IReadOnlyList<string> CurrentRoutes => _runningConf is null ? [] : _routes.Installed;

    public VpnStatus Status { get; private set; } = VpnStatus.Initial;

    public event Action<string>? Log;

    public async Task<VpnStatus> ApplyAsync(IReadOnlySet<RouteSet> fallback, CancellationToken ct)
    {
        var sets = new HashSet<RouteSet>(fallback) { RouteSet.Ai, RouteSet.Blocked };
        var configured = _profiles.Where(p => p.Configured).ToList();

        if (configured.Count == 0)
        {
            await StopAllAsync(ct);
            return Status = new VpnStatus(VpnState.NoKey, 0, fallback, null);
        }

        if (_active is not null && !configured.Contains(_active))
        {
            // Ключ или подписку удалили — снимаем этот туннель
            await StopTunnelAsync(ct);
            _active = null;
        }

        // Вставлено несколько способов — при первом запуске и раз в сутки выбираем быстрый
        if (configured.Count > 1 && (_lastCompare is null || _time.GetUtcNow() - _lastCompare >= CompareInterval))
            await CompareAsync(configured, sets, fallback, ct);

        VpnStatus? last = null;
        foreach (var profile in Candidates(configured))
        {
            var (status, ok) = await TryProfileAsync(profile, sets, fallback, ct);
            last = status;
            if (ok)
                return Status = status;

            if (configured.Count > 1)
            {
                _cooldown[profile] = _time.GetUtcNow() + FailedCooldown;
                Log?.Invoke($"VPN через {profile.Name} не работает: {status.Error}");
            }
        }
        return Status = last!;
    }

    /// <summary>Порядок попыток: текущий, затем запомненный как быстрый, затем остальные; отложенные — в конце.</summary>
    private IEnumerable<IVpnProfile> Candidates(List<IVpnProfile> configured)
    {
        var now = _time.GetUtcNow();
        var preferred = _settings?.Load().PreferredVpn;
        return configured
            .OrderBy(p => _cooldown.TryGetValue(p, out var until) && until > now ? 1 : 0)
            .ThenBy(p => p == _active ? 0 : 1)
            .ThenBy(p => p.Id == preferred ? 0 : 1)
            .ToList();
    }

    /// <summary>Поднять туннель через этот способ (если ещё не поднят), маршруты, DNS. Ok = работает или ещё подключается.</summary>
    private async Task<(VpnStatus Status, bool Ok)> TryProfileAsync(IVpnProfile profile, HashSet<RouteSet> sets,
        IReadOnlySet<RouteSet> fallback, CancellationToken ct)
    {
        var plan = await profile.PrepareAsync(ct);
        if (plan is null)
            return (new VpnStatus(VpnState.BadKey, 0, fallback, profile.Error) { Profile = profile.Name }, false);

        if (profile != _active)
        {
            await StopTunnelAsync(ct);
            _active = profile;
        }

        var health = plan.Conf == _runningConf ? await profile.Tunnel.GetHealthAsync(ct) : TunnelHealth.Stopped;
        var justStarted = false;
        if (health == TunnelHealth.Stopped)
        {
            Log?.Invoke($"Подключаю VPN: {profile.Name}");
            await StopTunnelAsync(ct);
            _active = profile;
            _routes.Reset();
            if (!await profile.Tunnel.StartAsync(plan.Conf, ct))
                return (new VpnStatus(VpnState.Failed, 0, fallback, Explain(profile.Tunnel.LastError)) { Profile = profile.Name }, false);
            _runningConf = plan.Conf;
            _startedAt = _time.GetUtcNow();
            justStarted = true;
        }

        RoutePlan routes;
        try
        {
            _router ??= _routerFactory();
            // Пока работает наш DNS, домены заранее не резолвим — их адреса придут на лету
            routes = await _router.ComputeAsync(sets, plan.EndpointHosts, ct, resolveDomains: _dns is not { Active: true });
        }
        catch (InvalidDataException e)
        {
            return (new VpnStatus(VpnState.Failed, 0, fallback, e.Message) { Profile = profile.Name }, false);
        }
        foreach (var warning in routes.Warnings)
            Log?.Invoke(warning);
        lock (_lock)
        {
            _activeSets = sets;
            _endpoints = routes.Endpoints;
        }

        SyncRoutes(routes);
        await EnsureDnsAsync(sets);

        if (justStarted)
            health = await WaitForHandshakeAsync(profile.Tunnel, ct);

        var count = _routes.Installed.Count;
        var smart = _dns is { Active: true };
        if (health == TunnelHealth.Connected)
        {
            _cooldown.Remove(profile);
            return (new VpnStatus(VpnState.Connected, count, fallback, null) { SmartDns = smart, Profile = profile.Name }, true);
        }
        if (_time.GetUtcNow() - _startedAt < ConnectingGrace)
            return (new VpnStatus(VpnState.Connecting, count, fallback, null) { SmartDns = smart, Profile = profile.Name }, true);

        Log?.Invoke($"VPN: {profile.Tunnel.LastError}");
        var others = _otherVpns();
        var message = others.Count > 0 ? Explain(profile.Tunnel.LastError)
            : profile is AmneziaProfile ? SilentServerMessage
            : $"Серверы {profile.Name} не отвечают.";
        return (new VpnStatus(VpnState.ServerNotResponding, count, fallback, message) { SmartDns = smart, Profile = profile.Name }, false);
    }

    /// <summary>
    /// Поднимает по очереди каждый способ, меряет задержку через туннель и оставляет самый быстрый.
    /// Уже работающий меняем, только если другой заметно быстрее — чтобы не прыгать туда-сюда.
    /// </summary>
    private async Task CompareAsync(List<IVpnProfile> configured, HashSet<RouteSet> sets, IReadOnlySet<RouteSet> fallback,
        CancellationToken ct)
    {
        _lastCompare = _time.GetUtcNow();
        Log?.Invoke($"Сравниваю скорость: {string.Join(" и ", configured.Select(p => p.Name))}");

        var results = new List<(IVpnProfile Profile, TimeSpan Latency)>();
        foreach (var profile in configured)
        {
            var (status, _) = await TryProfileAsync(profile, sets, fallback, ct);
            if (status.State is not (VpnState.Connected or VpnState.Connecting))
                continue;
            // Медиана из трёх замеров — один случайно медленный ответ не решает
            var samples = new List<TimeSpan>();
            for (var i = 0; i < 3; i++)
            {
                if (await _measure(ct) is { } latency)
                    samples.Add(latency);
            }
            if (samples.Count == 0)
                continue;
            var median = samples.Order().ElementAt(samples.Count / 2);
            Log?.Invoke($"{profile.Name}: {median.TotalMilliseconds:0} мс");
            results.Add((profile, median));
        }

        if (results.Count == 0)
            return;

        var best = results.MinBy(r => r.Latency);
        var preferredName = _settings?.Load().PreferredVpn;
        var current = results.FirstOrDefault(r => r.Profile.Id == preferredName);
        // Прежний выбор оставляем, если новый быстрее несильно
        var winner = current.Profile is not null && best.Latency.TotalMilliseconds > current.Latency.TotalMilliseconds * (1 - SwitchIfFasterBy)
            ? current
            : best;
        _settings?.Update(s => s.PreferredVpn = winner.Profile.Id);
        Log?.Invoke($"Выбран VPN: {winner.Profile.Name}");

        // Последним подняли не победителя — переподнимем победителя в общем цикле
        if (_active != winner.Profile)
        {
            await StopTunnelAsync(ct);
            _active = winner.Profile;
        }
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
            // Под той же блокировкой, что и сверка маршрутов: иначе сверка, начатая чуть раньше,
            // удалила бы только что добавленный маршрут как «лишний»
            if (_runningConf is not null)
            {
                foreach (var ip in add)
                    _routes.Add(new IPNetwork(ip, 32));
            }
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
            _routes.Sync(wanted);
        }
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

    /// <summary>Первый ответ сервера приходит через секунду-другую после запуска — ждём его, а не проверяем сразу.</summary>
    private async Task<TunnelHealth> WaitForHandshakeAsync(IVpnTunnel tunnel, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + _handshakeWait;
        while (true)
        {
            var health = await tunnel.GetHealthAsync(ct);
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

    /// <summary>Вставили или удалили ключ или подписку — заново выбрать способ подключения.</summary>
    public void ProfilesChanged()
    {
        _lastCompare = null;
        _cooldown.Clear();
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await StopAllAsync(ct);
        Status = Status with { State = HasKey ? VpnState.Stopped : VpnState.NoKey, RouteCount = 0, SmartDns = false };
    }

    /// <summary>Сначала вернуть DNS Windows, потом снять туннель — чтобы ни секунды не остаться без DNS.</summary>
    private async Task StopAllAsync(CancellationToken ct)
    {
        if (_dns is { Active: true })
            await _dns.StopAsync();
        _matcherFor = null;
        await StopTunnelAsync(ct);
        _active = null;
    }

    private async Task StopTunnelAsync(CancellationToken ct)
    {
        if (_runningConf is null || _active is null)
            return;
        Log?.Invoke($"Отключаю VPN: {_active.Name}");
        await _active.Tunnel.StopAsync(ct);
        _runningConf = null;
        _routes.Reset();
    }
}
