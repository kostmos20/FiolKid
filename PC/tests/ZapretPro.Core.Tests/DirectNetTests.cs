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
    public async Task Last_good_answer_is_used_when_dns_gives_nothing_this_time()
    {
        var host = $"flaky-{Guid.NewGuid():N}.example";
        IReadOnlyList<IPAddress> answer = [IPAddress.Parse("142.250.2.2")];
        Assert.Equal(answer, await DirectNet.ResolveAsync(host, [], default, (_, _) => Task.FromResult(answer)));

        // Провайдер в этот раз адреса не отдал — берём прошлый, а не «альт не работает»
        var again = await DirectNet.ResolveAsync(host, [], default, (_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([]));
        Assert.Equal(answer, again);
    }

    [Fact]
    public async Task Stub_addresses_count_as_not_found()
    {
        var addresses = await DirectNet.ResolveAsync($"stub-{Guid.NewGuid():N}.example", [], default,
            (_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Any, IPAddress.Loopback]));

        Assert.Empty(addresses);
    }
}
