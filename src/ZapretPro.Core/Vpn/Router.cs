using System.Net;
using System.Net.Sockets;

namespace ZapretPro.Core.Vpn;

/// <summary>
/// Что пускать в VPN. Нейросети и заблокированные сайты — всегда, остальное — только в запасном режиме
/// (основной путь для YouTube и Discord — zapret, для Telegram — локальный прокси).
/// </summary>
public enum RouteSet
{
    Ai,
    Blocked,
    Telegram,
    YouTube,
    Discord,
}

/// <summary>Списки доменов и подсетей из папки vpn (рядом с exe). Редактируются без пересборки.</summary>
public sealed class RouteLists
{
    /// <summary>Списки, которые программа сама обновляет из интернета (см. <see cref="ListUpdater"/>).</summary>
    public static readonly string[] UpdatableFiles = ["blocked-domains.txt", "blocked-cidr.txt"];

    public required IReadOnlyDictionary<RouteSet, IReadOnlyList<string>> Domains { get; init; }
    public required IReadOnlyDictionary<RouteSet, IReadOnlyList<IPNetwork>> Networks { get; init; }

    /// <param name="dir">Встроенные списки (папка vpn рядом с exe).</param>
    /// <param name="updatedDir">Скачанные свежие версии; если файл там есть, берётся он.</param>
    /// <param name="excludeFiles">
    /// Списки доменов, которые идут мимо VPN своим путём (хостлисты zapret). Из заблокированных сайтов они
    /// исключаются, чтобы видео YouTube не шло через сервер знакомого.
    /// </param>
    public static RouteLists Load(string dir, string? updatedDir = null, IEnumerable<string>? excludeFiles = null)
    {
        var files = new Dictionary<RouteSet, string[]>
        {
            [RouteSet.Ai] = ["ai-domains.txt"],
            [RouteSet.Blocked] = ["blocked-domains.txt", "blocked-cidr.txt"],
            [RouteSet.Telegram] = ["telegram-domains.txt", "telegram-cidr.txt"],
            [RouteSet.YouTube] = ["google-cidr.txt"],
            [RouteSet.Discord] = ["discord-domains.txt"],
        };

        var domains = new Dictionary<RouteSet, IReadOnlyList<string>>();
        var networks = new Dictionary<RouteSet, IReadOnlyList<IPNetwork>>();
        foreach (var (set, names) in files)
        {
            var (hosts, nets) = Parse(names.Select(n => Pick(dir, updatedDir, n)).SelectMany(ReadLines));
            domains[set] = hosts;
            networks[set] = nets;
        }

        if (domains[RouteSet.Ai].Count == 0 && networks[RouteSet.Ai].Count == 0)
            throw new InvalidDataException($"Список нейросетей пуст или не найден: {Path.Combine(dir, "ai-domains.txt")}");

        // Домены с собственным путём: всё из хостлистов zapret (в их синтаксисе ^ — «точно этот домен») и Telegram
        var own = (excludeFiles ?? [])
            .SelectMany(ReadLines)
            .Concat(domains[RouteSet.Telegram])
            .Select(d => d.TrimStart('^').ToLowerInvariant())
            .ToHashSet();

        domains[RouteSet.Blocked] = domains[RouteSet.Blocked]
            // Строки вида «.ua» — целые доменные зоны, по IP их не выразить
            .Where(d => !d.StartsWith('.') && d.Contains('.') && !IsUnder(d, own))
            // Сайты часто живут на www.: без него открылась бы только переадресация
            .SelectMany(d => d.StartsWith("www.") ? [d] : new[] { d, "www." + d })
            .Distinct()
            .ToList();

        return new RouteLists { Domains = domains, Networks = networks };
    }

    private static string Pick(string dir, string? updatedDir, string name)
    {
        var updated = updatedDir is null ? null : Path.Combine(updatedDir, name);
        return updated is not null && File.Exists(updated) ? updated : Path.Combine(dir, name);
    }

    private static IEnumerable<string> ReadLines(string path) => File.Exists(path)
        ? File.ReadLines(path).Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0)
        : [];

    /// <summary>В файлах можно писать и домены, и подсети вида 1.2.3.0/24 (или одиночные IP).</summary>
    private static (IReadOnlyList<string> Hosts, IReadOnlyList<IPNetwork> Networks) Parse(IEnumerable<string> lines)
    {
        var nets = new List<IPNetwork>();
        var hosts = new List<string>();
        foreach (var line in lines)
        {
            if (TryParseNetwork(line, out var net))
                nets.Add(net);
            else
                hosts.Add(line.ToLowerInvariant());
        }
        return (hosts.Distinct().ToList(), nets.Distinct().ToList());
    }

    /// <summary>Домен совпадает с одним из списка или является его поддоменом.</summary>
    private static bool IsUnder(string domain, HashSet<string> parents)
    {
        for (var d = domain; ; )
        {
            if (parents.Contains(d))
                return true;
            var dot = d.IndexOf('.');
            if (dot < 0)
                return false;
            d = d[(dot + 1)..];
        }
    }

    internal static bool TryParseNetwork(string text, out IPNetwork network)
    {
        if (IPNetwork.TryParse(text, out network) && network.BaseAddress.AddressFamily == AddressFamily.InterNetwork)
            return true;
        if (IPAddress.TryParse(text, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        {
            network = new IPNetwork(ip, 32);
            return true;
        }
        return false;
    }
}

public sealed record RoutePlan(IReadOnlyList<string> AllowedIps, IReadOnlyList<string> Warnings)
{
    /// <summary>Адреса VPN-сервера: их нельзя пускать в сам туннель.</summary>
    public IReadOnlyList<IPAddress> Endpoints { get; init; } = [];
}

/// <summary>
/// Раздельная маршрутизация для AmneziaWG: в туннель идут только адреса из списков.
/// Домены превращаются в IP через обычный DNS. Адреса, которые уже встречались, помним
/// неделю: CDN отдают разные IP, и без памяти маршруты менялись бы при каждой проверке.
/// Только IPv4: туннель Amnezia обычно без IPv6.
/// </summary>
public sealed class Router
{
    private static readonly TimeSpan Remember = TimeSpan.FromDays(7);

    // Адреса нейросетей узнаём часто (у них короткие DNS-записи), тысячу заблокированных сайтов — раз в час
    private static readonly TimeSpan FastRefresh = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SlowRefresh = TimeSpan.FromHours(1);

    // Чтобы тысяча DNS-запросов не ушла разом
    private readonly SemaphoreSlim _dnsSlots = new(32);
    private readonly Dictionary<RouteSet, DateTimeOffset> _resolvedAt = new();

    // Локальные, служебные и CGNAT-адреса в туннель не пускаем никогда
    internal static readonly IPNetwork[] NeverRoute =
    [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("224.0.0.0/3"),
    ];

    private readonly RouteLists _lists;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly TimeProvider _time;
    private readonly Dictionary<RouteSet, Dictionary<IPAddress, DateTimeOffset>> _seen =
        Enum.GetValues<RouteSet>().ToDictionary(s => s, _ => new Dictionary<IPAddress, DateTimeOffset>());

    public Router(RouteLists lists, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null, TimeProvider? time = null)
    {
        _lists = lists;
        _resolve = resolve ?? System.Net.Dns.GetHostAddressesAsync;
        _time = time ?? TimeProvider.System;
    }

    public RouteLists Lists => _lists;

    /// <param name="resolveDomains">
    /// False — домены не резолвить заранее: их адреса добавит наш DNS в момент, когда браузер их спросит
    /// (<see cref="Dns.SmartDns"/>). True — запасной путь, если наш DNS не запустился.
    /// </param>
    public async Task<RoutePlan> ComputeAsync(IReadOnlySet<RouteSet> sets, string endpointHost, CancellationToken ct,
        bool resolveDomains = true)
    {
        var warnings = new List<string>();
        var now = _time.GetUtcNow();

        var endpoints = IPAddress.TryParse(endpointHost, out var endpointIp)
            ? [endpointIp]
            : await ResolveAsync(endpointHost, ct);
        if (endpoints.Length == 0)
            warnings.Add("Не удалось узнать IP VPN-сервера");

        var networks = sets.SelectMany(s => _lists.Networks[s]).Distinct().ToList();

        var wanted = new HashSet<IPAddress>();
        foreach (var set in resolveDomains ? sets : new HashSet<RouteSet>())
        {
            var seen = _seen[set];
            var refresh = set == RouteSet.Ai ? FastRefresh : SlowRefresh;
            if (!_resolvedAt.TryGetValue(set, out var last) || now - last >= refresh)
            {
                var resolved = await Task.WhenAll(_lists.Domains[set].Select(d => ResolveAsync(d, ct)));
                _resolvedAt[set] = now;
                lock (seen)
                {
                    foreach (var ip in resolved.SelectMany(a => a))
                        seen[ip] = now;
                }
            }
            lock (seen)
            {
                foreach (var stale in seen.Where(kv => now - kv.Value > Remember).Select(kv => kv.Key).ToList())
                    seen.Remove(stale);
                wanted.UnionWith(seen.Keys);
            }
        }

        var routes = new List<IPNetwork>();
        // Сначала широкие подсети: узкие внутри них уже не нужны
        foreach (var net in networks.OrderBy(n => n.PrefixLength))
        {
            if (routes.Any(r => r.Contains(net.BaseAddress) && r.PrefixLength <= net.PrefixLength))
                continue;
            if (NeverRoute.Any(n => Overlaps(n, net)))
            {
                warnings.Add($"Подсеть {net} пропущена: это локальные адреса");
                continue;
            }
            // Сервер VPN внутри маршрутов туннеля замкнул бы туннель сам на себя
            if (endpoints.Any(net.Contains))
            {
                warnings.Add($"Подсеть {net} пропущена: в ней VPN-сервер");
                continue;
            }
            routes.Add(net);
        }

        foreach (var ip in wanted)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork || endpoints.Contains(ip) || NeverRoute.Any(n => n.Contains(ip)))
                continue;
            if (routes.Any(n => n.Contains(ip)))
                continue;
            routes.Add(new IPNetwork(ip, 32));
        }

        var allowed = routes
            .Distinct()
            .OrderBy(n => n.BaseAddress.GetAddressBytes(), ByteArrayComparer.Instance)
            .ThenBy(n => n.PrefixLength)
            .Select(n => n.ToString())
            .ToList();
        return new RoutePlan(allowed, warnings) { Endpoints = endpoints };
    }

    private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        await _dnsSlots.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var all = await _resolve(host, timeout.Token);
            return all.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return [];
        }
        finally
        {
            _dnsSlots.Release();
        }
    }

    private static bool Overlaps(IPNetwork a, IPNetwork b) => a.Contains(b.BaseAddress) || b.Contains(a.BaseAddress);

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y)
        {
            for (var i = 0; i < Math.Min(x!.Length, y!.Length); i++)
            {
                var c = x[i].CompareTo(y[i]);
                if (c != 0)
                    return c;
            }
            return x.Length.CompareTo(y.Length);
        }
    }
}
