using System.Net;
using ZapretPro.Core.Health;

namespace ZapretPro.Core.Tests;

public sealed class DirectNetTests
{
    [Fact]
    public async Task When_provider_dns_gives_nothing_windows_is_asked()
    {
        // DNS провайдера не ответил (или ответил пустотой, как бывает с YouTube) — адрес берём у Windows
        var asked = new List<string>();
        var addresses = await DirectNet.ResolveAsync("www.youtube.com", [], default, (host, _) =>
        {
            asked.Add(host);
            return Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("142.250.1.1")]);
        });

        Assert.Equal(["www.youtube.com"], asked);
        Assert.Equal([IPAddress.Parse("142.250.1.1")], addresses);
    }

    [Fact]
    public async Task Stub_addresses_count_as_not_found()
    {
        var addresses = await DirectNet.ResolveAsync("www.youtube.com", [], default,
            (_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Any, IPAddress.Loopback]));

        Assert.Empty(addresses);
    }
}
