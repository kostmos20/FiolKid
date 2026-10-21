using System.Net;
using System.Net.Sockets;

namespace ZapretPro.Core.Vpn;

/// <summary>Что пускать в VPN. Нейросети — всегда, остальное — только в запасном режиме.</summary>
public enum RouteSet
{
    Ai,
    Telegram,
    YouTube,
    Discord,
}

/// <summary>Списки доменов и подсетей из папки vpn (рядом с exe). Редактируются без пересборки.</summary>
public sealed class RouteLists
{
    public required IReadOnlyDictionary<RouteSet, IReadOnlyList<string>> Domains { get; init; }
    public required IReadOnlyDictionary<RouteSet, IReadOnlyList<IPNetwork>> Networks { get; init; }

    public static RouteLists Load(string dir)
    {
        var files = new Dictionary<RouteSet, string[]>
        {
            [RouteSet.Ai] = ["ai-domains.txt"],
            [RouteSet.Telegram] = ["telegram-domains.txt", "telegram-cidr.txt"],
            [RouteSet.YouTube] = ["google-cidr.txt"],
            [RouteSet.Discord] = ["discord-domains.txt"],
        };

        var domains = new Dictionary<RouteSet, IReadOnlyList<string>>();
        var networks = new Dictionary<RouteSet, IReadOnlyList<IPNetwork>>();
        foreach (var (set, names) in files)
        {
            var lines = names
                .Select(n => Path.Combine(dir, n))
                .Where(File.Exists)
                .SelectMany(File.ReadLines)
                .Select(l => l.Split('#')[0].Trim())
                .Where(l => l.Length > 0)
                .ToList();

            // В файлах можно писать и домены, и подсети вида 1.2.3.0/24 (или одиночные IP)
            var nets = new List<IPNetwork>();
            var hosts = new List<string>();
            foreach (var line in lines)
            {
                if (TryParseNetwork(line, out var net))
                    nets.Add(net);
                else
                    hosts.Add(line.ToLowerInvariant());
            }
            domains[set] = hosts.Distinct().ToList();
            networks[set] = nets;
        }

        if (domains[RouteSet.Ai].Count == 0 && networks[RouteSet.Ai].Count == 0)
            throw new InvalidDataException($"Список нейросетей пуст или не найден: {Path.Combine(dir, "ai-domains.txt")}");

        return new RouteLists { Domains = domains, Networks = networks };
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

public sealed record RoutePlan(IReadOnlyList<string> AllowedIps, IReadOnlyList<string> Warnings);

/// <summary>
/// Раздельная маршрутизация для AmneziaWG: в туннель идут только адреса из списков.
/// Домены превращаются в IP через обычный DNS. Адреса, которые уже встречались, помним
/// неделю: CDN отдают разные IP, и без памяти маршруты менялись бы при каждой проверке.
/// Только IPv4: туннель Amnezia обычно без IPv6.
/// </summary>
public sealed class Router
{
    private static readonly TimeSpan Remember = TimeSpan.FromDays(7);

    // Локальные, служебные и CGNAT-адреса в туннель не пускаем никогда
    private static readonly IPNetwork[] NeverRoute =
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
        _resolve = resolve ?? Dns.GetHostAddressesAsync;
        _time = time ?? TimeProvider.System;
    }

    public async Task<RoutePlan> ComputeAsync(IReadOnlySet<RouteSet> sets, string endpointHost, CancellationToken ct)
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
        foreach (var set in sets)
        {
            var resolved = await Task.WhenAll(_lists.Domains[set].Select(d => ResolveAsync(d, ct)));
            var seen = _seen[set];
            lock (seen)
            {
                foreach (var ip in resolved.SelectMany(a => a))
                    seen[ip] = now;
                foreach (var stale in seen.Where(kv => now - kv.Value > Remember).Select(kv => kv.Key).ToList())
                    seen.Remove(stale);
                wanted.UnionWith(seen.Keys);
            }
        }

        var routes = new List<IPNetwork>();
        foreach (var net in networks)
        {
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
        return new RoutePlan(allowed, warnings);
    }

    private async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
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
