using System.Collections.Concurrent;
using System.Net;
using ZapretPro.Core.Infrastructure;

namespace ZapretPro.Core.Games;

/// <summary>Куда вести адреса игр: в туннель VPN.</summary>
public interface IGameRoutes
{
    /// <summary>Туннель поднят.</summary>
    bool TunnelRunning { get; }

    /// <summary>Что игр сейчас вести в VPN: запомненные адреса и домены. Пусто — игр нет.</summary>
    void SetGameTargets(IReadOnlyCollection<IPAddress> addresses, IReadOnlyList<string> domains);

    /// <summary>Новый адрес игры — сразу маршрут. True — маршрут поставлен только что.</summary>
    bool AddGameAddress(IPAddress address);

    /// <summary>Имя, по которому этот адрес недавно спрашивали у нашего DNS.</summary>
    string? NameOf(IPAddress address);
}

/// <summary>
/// Ведёт игры из списка через VPN. Пока игра запущена: следит, куда подключаются её процессы (ETW), и сразу ставит
/// маршрут на каждый новый адрес; уже открытые прямые TCP-соединения рвёт, чтобы игра переподключилась через VPN;
/// IPv6 игре закрыт (туннель только IPv4). Запоминает адреса и домены — со следующего запуска они идут через VPN
/// с первого пакета. Игра закрылась — всё снимается.
/// </summary>
public sealed class GameRouter : IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(30);

    private readonly SettingsStore _settings;
    private readonly IGameRoutes _routes;
    private readonly IGameTraffic _traffic;
    private readonly GameIpv6Block? _ipv6;
    private readonly Func<IReadOnlySet<int>, IPAddress, int> _closeTcp;
    private readonly Func<IReadOnlySet<int>, IReadOnlyList<IPAddress>> _openTcp;
    private readonly Func<int, string?> _pathOf;
    private readonly object _lock = new();

    // pid → игра; pid не игры — чтобы не спрашивать путь на каждый пакет
    private readonly ConcurrentDictionary<int, GameEntry?> _pids = new();
    private readonly ConcurrentDictionary<IPAddress, byte> _seen = new();
    private volatile IReadOnlyList<GameEntry> _running = [];
    private readonly Dictionary<string, (HashSet<string> Ips, HashSet<string> Domains)> _learned = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;
    private DateTime _saveAt;

    public GameRouter(SettingsStore settings, IGameRoutes routes, IGameTraffic? traffic = null, GameIpv6Block? ipv6 = null,
        Func<IReadOnlySet<int>, IPAddress, int>? closeTcp = null, Func<IReadOnlySet<int>, IReadOnlyList<IPAddress>>? openTcp = null,
        Func<int, string?>? pathOf = null)
    {
        _settings = settings;
        _routes = routes;
        _traffic = traffic ?? new EtwGameTraffic();
        _ipv6 = ipv6;
        _closeTcp = closeTcp ?? TcpConnections.Close;
        _openTcp = openTcp ?? (pids => TcpConnections.Of(pids).Select(c => c.Remote).Distinct().ToList());
        _pathOf = pathOf ?? ProcessPaths.Of;
        _traffic.Seen += OnSeen;
    }

    public event Action<string>? Log;

    /// <summary>Какие игры сейчас идут через VPN — для экрана «Игры».</summary>
    public IReadOnlyList<GameEntry> Running => _running;

    /// <summary>Узнанное записано в настройки.</summary>
    public event Action? Saved;

    /// <summary>Сколько адресов игры пущено в VPN за этот запуск — для карточки.</summary>
    public int SeenCount => _seen.Count;

    /// <summary>Монитор увидел перемену: игры из списка запустились, закрылись или у них новые процессы.</summary>
    public void Update(GameScan scan)
    {
        lock (_lock)
        {
            var wasRunning = _running.Count > 0;
            _running = scan.VpnGames.Select(g => g.Game).ToList();
            foreach (var game in scan.VpnGames)
            {
                foreach (var pid in game.Pids)
                    _pids[pid] = game.Game;
            }

            if (scan.VpnGames.Count == 0)
            {
                if (!wasRunning)
                    return;
                StopCore();
            }
            else
                StartCore(scan, wasRunning);
        }
        SaveIfDue(force: scan.VpnGames.Count == 0);
    }

    private void StartCore(GameScan scan, bool wasRunning)
    {
        if (!wasRunning)
        {
            Log?.Invoke($"Игра через VPN: {string.Join(", ", _running.Select(g => g.Name))}");
            if (!_routes.TunnelRunning)
                Log?.Invoke("VPN сейчас не поднят — адреса игры пойдут в туннель, как только он поднимется");
            if (_traffic.Start() is { } error)
                Log?.Invoke($"Слежка за соединениями игры не запустилась: {error}. Через VPN пойдут только запомненные адреса");
        }

        // Запомненное — сразу в VPN, с первого пакета
        var ips = new HashSet<IPAddress>(_seen.Keys);
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in _running)
        {
            foreach (var ip in game.LearnedAddresses)
            {
                if (IPAddress.TryParse(ip, out var parsed))
                    ips.Add(parsed);
            }
            domains.UnionWith(game.LearnedDomains);
        }
        _routes.SetGameTargets(ips, domains.ToList());

        if (_ipv6?.Update(scan.VpnGames.SelectMany(g => g.Exes).ToList()) is { } ipv6Error)
            Log?.Invoke($"Не удалось закрыть игре IPv6: {ipv6Error}");

        // Соединения, открытые до того, как мы заметили игру, — тоже в VPN (и порвать прямые)
        foreach (var game in scan.VpnGames)
        {
            foreach (var ip in _openTcp(game.Pids))
                Route(game.Game, game.Pids, ip, tcp: true);
        }
    }

    private void StopCore()
    {
        _traffic.Stop();
        _ipv6?.Update([]);
        _routes.SetGameTargets([], []);
        Log?.Invoke($"Игры закрыты — их адреса больше не идут через VPN (было {_seen.Count})");
        _seen.Clear();
        _pids.Clear();
    }

    private void OnSeen(int pid, IPAddress ip, bool tcp)
    {
        if (_seen.ContainsKey(ip))
            return;
        var game = _pids.GetOrAdd(pid, p =>
        {
            // Новый процесс (игра запустила вспомогательный) — монитор заметит его только через 2 секунды
            var path = _pathOf(p);
            return path is null ? null : Running.FirstOrDefault(g => g.Matches(path));
        });
        if (game is null)
            return;
        var pids = _pids.Where(kv => kv.Value == game).Select(kv => kv.Key).ToHashSet();
        Route(game, pids, ip, tcp);
        SaveIfDue(force: false);
    }

    private void Route(GameEntry game, IReadOnlySet<int> pids, IPAddress ip, bool tcp)
    {
        if (!_seen.TryAdd(ip, 0))
            return;
        var fresh = _routes.AddGameAddress(ip);
        // Маршрут поставлен только что — значит, уже открытое соединение шло напрямую: рвём, игра переподключится
        var closed = fresh && tcp ? _closeTcp(pids, ip) : 0;
        // Первые адреса за запуск — в журнал, чтобы по отчёту было видно, что слежка работает
        if (_seen.Count <= 10)
            Log?.Invoke($"{game.Name}: {ip}{(_routes.NameOf(ip) is { } name ? $" ({name})" : "")} {(tcp ? "TCP" : "UDP")} → через VPN" +
                        (closed > 0 ? $", прямых соединений порвано: {closed}" : "") +
                        (_seen.Count == 10 ? " (дальше не пишу, итог — когда игра закроется)" : ""));
        Learn(game, ip);
    }

    private void Learn(GameEntry game, IPAddress ip)
    {
        lock (_learned)
        {
            if (!_learned.TryGetValue(game.Path, out var known))
                _learned[game.Path] = known = (new HashSet<string>(game.LearnedAddresses), new HashSet<string>(game.LearnedDomains,
                    StringComparer.OrdinalIgnoreCase));
            var changed = known.Ips.Count < GameEntry.MaxLearned && known.Ips.Add(ip.ToString());
            if (_routes.NameOf(ip) is { } name && known.Domains.Count < GameEntry.MaxLearned)
                changed |= known.Domains.Add(name.TrimEnd('.').ToLowerInvariant());
            if (changed && !_dirty)
            {
                _dirty = true;
                _saveAt = DateTime.UtcNow + SaveDelay;
            }
        }
    }

    /// <summary>Записать узнанное в настройки — не чаще раза в 30 секунд и сразу, когда игра закрылась.</summary>
    private void SaveIfDue(bool force)
    {
        Dictionary<string, (HashSet<string> Ips, HashSet<string> Domains)> copy;
        lock (_learned)
        {
            if (!_dirty || !force && DateTime.UtcNow < _saveAt)
                return;
            _dirty = false;
            copy = _learned.ToDictionary(kv => kv.Key, kv => (new HashSet<string>(kv.Value.Ips), new HashSet<string>(kv.Value.Domains)),
                StringComparer.OrdinalIgnoreCase);
        }
        try
        {
            _settings.Update(s =>
            {
                foreach (var game in s.Games ?? [])
                {
                    if (!copy.TryGetValue(game.Path, out var known))
                        continue;
                    game.LearnedAddresses = known.Ips.ToList();
                    game.LearnedDomains = known.Domains.ToList();
                }
            });
        }
        catch (IOException e)
        {
            Log?.Invoke($"Не удалось запомнить адреса игры: {e.Message}");
            return;
        }
        Saved?.Invoke();
    }

    /// <summary>Кнопка «Сбросить» на карточке игры: забыть узнанное.</summary>
    public void Forget(string path)
    {
        lock (_learned)
            _learned.Remove(path);
    }

    public void Dispose()
    {
        _traffic.Seen -= OnSeen;
        _traffic.Dispose();
        _ipv6?.Dispose();
        SaveIfDue(force: true);
    }
}
