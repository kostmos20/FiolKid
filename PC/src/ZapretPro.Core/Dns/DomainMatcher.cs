using ZapretPro.Core.Vpn;

namespace ZapretPro.Core.Dns;

/// <summary>Домен из списка покрывает и все свои поддомены: x.com → api.x.com, abs.twimg.com → twimg.com.</summary>
public sealed class DomainMatcher
{
    private readonly Dictionary<string, RouteSet> _domains = new(StringComparer.OrdinalIgnoreCase);

    public DomainMatcher(IEnumerable<(string Domain, RouteSet Set)> domains)
    {
        foreach (var (domain, set) in domains)
            _domains.TryAdd(domain.TrimStart('^', '.').TrimEnd('.'), set); // первый набор важнее
    }

    public int Count => _domains.Count;

    public RouteSet? Match(string name)
    {
        for (var d = name.TrimEnd('.'); d.Length > 0;)
        {
            if (_domains.TryGetValue(d, out var set))
                return set;
            var dot = d.IndexOf('.');
            if (dot < 0)
                return null;
            d = d[(dot + 1)..];
        }
        return null;
    }
}
