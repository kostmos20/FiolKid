using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ZapretPro.Core.Dns;
using ZapretPro.Core.Infrastructure;
using ZapretPro.Core.Vpn;

namespace ZapretPro.Core.Tests;

public sealed class SubscriptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OdnaKnopkaTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // Выдуманные серверы — в форматах, которые отдают настоящие сервисы подписок
    private const string Uuid = "a3482e88-686a-4a58-8126-99c9df64b7bf";
    private static readonly string[] Links =
    [
        $"vless://{Uuid}@203.0.113.10:443?type=tcp&security=reality&flow=xtls-rprx-vision&fp=edge&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sid=0123abcd&sni=www.microsoft.com&encryption=none&headerType=none#%F0%9F%87%B3%F0%9F%87%B1%20Netherlands",
        $"vless://{Uuid}@grpc.example.com:443?type=grpc&security=tls&serviceName=svc&mode=gun&fp=qq&sni=grpc.example.com&encryption=none#Germany",
        "hysteria2://hy-secret@203.0.113.11:8443?sni=hy.example.com&obfs=salamander&obfs-password=obfs-secret#Finland",
        "trojan://trojan-secret@203.0.113.12:443?type=ws&path=%2Fws&host=cdn.example.com&sni=cdn.example.com#Poland",
        "ss://YWVzLTI1Ni1nY206c3Mtc2VjcmV0@203.0.113.13:8388#Sweden",
        $"vless://{Uuid}@203.0.113.14:443?type=xhttp&security=reality&pbk=abc#Unsupported-xhttp",
        "vmess://eyJ2IjoiMiJ9#Unsupported-vmess",
    ];

    private static string Base64Body() => Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("\n", Links)));

    [Fact]
    public void Base64_subscription_is_parsed_with_service_headers()
    {
        var headers = new Dictionary<string, string>
        {
            ["profile-title"] = "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("🔥 Quattro VPN")),
            ["subscription-userinfo"] = "upload=1; download=2; total=3; expire=1793712905",
            ["profile-update-interval"] = "3",
        };

        var sub = SubscriptionParser.Parse(Base64Body(), headers);

        Assert.Equal("🔥 Quattro VPN", sub.Title);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793712905), sub.Expires);
        Assert.Equal(TimeSpan.FromHours(3), sub.UpdateInterval);
        Assert.Equal(["vless", "vless", "hysteria2", "trojan", "shadowsocks"], sub.Servers.Select(s => s.Protocol));
        Assert.Equal(2, sub.Skipped); // xhttp и vmess sing-box-ом в нашей сборке не берём
    }

    [Fact]
    public void Plain_text_subscription_and_line_wrapped_base64_work()
    {
        Assert.Equal(5, SubscriptionParser.Parse(string.Join("\r\n", Links)).Servers.Count);

        var wrapped = string.Join("\n", Base64Body().Chunk(76).Select(c => new string(c)));
        Assert.Equal(5, SubscriptionParser.Parse(wrapped).Servers.Count);
    }

    [Fact]
    public void Vless_reality_becomes_singbox_outbound()
    {
        var server = SubscriptionParser.ParseLink(Links[0])!;
        var o = server.Outbound;

        Assert.Equal("🇳🇱 Netherlands", server.Name);
        Assert.Equal("vless", (string?)o["type"]);
        Assert.Equal("203.0.113.10", (string?)o["server"]);
        Assert.Equal(443, (int?)o["server_port"]);
        Assert.Equal(Uuid, (string?)o["uuid"]);
        Assert.Equal("xtls-rprx-vision", (string?)o["flow"]);
        Assert.Equal("www.microsoft.com", (string?)o["tls"]?["server_name"]);
        Assert.Equal("edge", (string?)o["tls"]?["utls"]?["fingerprint"]);
        Assert.Equal("jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", (string?)o["tls"]?["reality"]?["public_key"]);
        Assert.Equal("0123abcd", (string?)o["tls"]?["reality"]?["short_id"]);
        Assert.Null(o["transport"]);
        Assert.Equal(0, server.Preference);
    }

    [Fact]
    public void Grpc_hysteria2_trojan_and_shadowsocks_are_mapped()
    {
        var grpc = SubscriptionParser.ParseLink(Links[1])!.Outbound;
        Assert.Equal("grpc", (string?)grpc["transport"]?["type"]);
        Assert.Equal("svc", (string?)grpc["transport"]?["service_name"]);
        Assert.Null(grpc["flow"]);

        var hy = SubscriptionParser.ParseLink(Links[2])!.Outbound;
        Assert.Equal("hy-secret", (string?)hy["password"]);
        Assert.Equal("salamander", (string?)hy["obfs"]?["type"]);
        Assert.Equal("obfs-secret", (string?)hy["obfs"]?["password"]);
        Assert.Equal("hy.example.com", (string?)hy["tls"]?["server_name"]);

        var trojan = SubscriptionParser.ParseLink(Links[3])!.Outbound;
        Assert.Equal("ws", (string?)trojan["transport"]?["type"]);
        Assert.Equal("/ws", (string?)trojan["transport"]?["path"]);
        Assert.Equal("cdn.example.com", (string?)trojan["transport"]?["headers"]?["Host"]);
        Assert.True((bool?)trojan["tls"]?["enabled"]);

        var ss = SubscriptionParser.ParseLink(Links[4])!.Outbound;
        Assert.Equal("aes-256-gcm", (string?)ss["method"]);
        Assert.Equal("ss-secret", (string?)ss["password"]);
        // Старая форма ss: всё целиком в base64
        var legacy = SubscriptionParser.ParseLink("ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:pw@203.0.113.15:8388")) + "#old")!;
        Assert.Equal("203.0.113.15", legacy.Host);
        Assert.Equal(8388, legacy.Port);
    }

    [Theory]
    [InlineData("vless://not-a-uuid@host:443?security=none")]
    [InlineData("vless://a3482e88-686a-4a58-8126-99c9df64b7bf@host:443?security=reality")] // без ключа Reality
    [InlineData("hysteria2://pw@host:443?mport=1000-2000")]
    [InlineData("ss://bWV0aG9k@host:8388?plugin=obfs-local")]
    [InlineData("garbage")]
    public void Broken_or_unsupported_links_are_skipped(string link)
    {
        Assert.Null(SubscriptionParser.ParseLink(link));
    }

    [Fact]
    public void Server_ToString_hides_address_and_secrets()
    {
        var text = SubscriptionParser.ParseLink(Links[0])!.ToString();

        Assert.DoesNotContain("203.0.113.10", text);
        Assert.DoesNotContain(Uuid, text);
    }

    [Fact]
    public void Selection_prefers_reality_and_is_stable_for_same_seed()
    {
        var servers = SubscriptionParser.Parse(Base64Body()).Servers;

        var first = SingBoxConfig.Select(servers, max: 3, random: new Random(42));
        var again = SingBoxConfig.Select(servers, max: 3, random: new Random(42));

        Assert.Equal("vless", first[0].Protocol);
        Assert.NotNull(first[0].Outbound["tls"]?["reality"]);
        Assert.Equal("hysteria2", first[1].Protocol);
        Assert.Equal(first.Select(s => s.Name), again.Select(s => s.Name));
    }

    [Fact]
    public void Config_has_tun_without_own_routes_and_urltest_over_all_servers()
    {
        var servers = SubscriptionParser.Parse(Base64Body()).Servers;

        var config = JsonNode.Parse(SingBoxConfig.Build(servers, "FiolKid"))!;

        var tun = config["inbounds"]![0]!;
        Assert.Equal("tun", (string?)tun["type"]);
        Assert.Equal("FiolKid", (string?)tun["interface_name"]);
        Assert.False((bool?)tun["auto_route"]);
        var outbounds = config["outbounds"]!.AsArray();
        Assert.Equal("urltest", (string?)outbounds[0]!["type"]);
        Assert.Equal(servers.Count, outbounds[0]!["outbounds"]!.AsArray().Count);
        Assert.Equal("auto", (string?)config["route"]!["final"]);
        Assert.True((bool?)config["route"]!["auto_detect_interface"]);
    }

    /// <summary>Главная проверка формата: конфиг принимает сам sing-box той версии, что идёт с программой.</summary>
    [Fact]
    public async Task Real_singbox_accepts_generated_config()
    {
        var singBox = FindUp("third_party", "sing-box", "sing-box.exe");
        if (singBox is null)
            return; // tools\fetch-deps.ps1 ещё не запускали

        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.json");
        await File.WriteAllTextAsync(path, SingBoxConfig.Build(SubscriptionParser.Parse(Base64Body()).Servers, "FiolKid"));

        using var process = Process.Start(new ProcessStartInfo(singBox, $"check -c \"{path}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = await process.StandardError.ReadToEndAsync() + await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"sing-box check: {output}");
    }

    /// <summary>
    /// Замер серверов настоящим sing-box: «серверы» — прямой выход и неработающий SOCKS, сайты для замера —
    /// локальный HTTP-сервер. Проверяем, что рабочий сервер получил задержку, скорость и страну, а мёртвый — нет.
    /// </summary>
    [Fact]
    public async Task Real_singbox_benchmark_measures_each_server_separately()
    {
        var singBox = FindUp("third_party", "sing-box", "sing-box.exe");
        if (singBox is null)
            return;
        Directory.CreateDirectory(_dir);

        var site = new TcpListener(IPAddress.Loopback, 0);
        site.Start();
        var port = ((IPEndPoint)site.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        _ = ServeAsync(site, stop.Token);
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var deadPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();

        ProxyServer[] servers =
        [
            new("direct", "127.0.0.1", 1, "Рабочий", new JsonObject { ["type"] = "direct" }),
            new("socks", "127.0.0.1", deadPort, "Мёртвый",
                new JsonObject { ["type"] = "socks", ["server"] = "127.0.0.1", ["server_port"] = deadPort }),
        ];
        var benchmark = new ServerBenchmark(Path.GetDirectoryName(singBox)!, _dir, protect: false)
        {
            PingUrl = $"http://127.0.0.1:{port}/ping",
            TraceUrl = $"http://127.0.0.1:{port}/trace",
            SpeedUrl = $"http://127.0.0.1:{port}/down",
        };
        try
        {
            var scores = await benchmark.RunAsync(servers, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);

            var good = scores.Single(s => s.Name == "Рабочий");
            var dead = scores.Single(s => s.Name == "Мёртвый");
            Assert.True(good.Works);
            Assert.Equal("NL", good.Country);
            Assert.True(good.Mbps > 0);
            Assert.False(dead.Works);
            Assert.Equal(good.Key, scores[0].Key);
            Assert.Equal([good.Key], ServerBenchmark.Best(scores));
            Assert.False(File.Exists(Path.Combine(_dir, "sing-box-benchmark.json"))); // конфиг с паролями не остаётся
        }
        finally
        {
            stop.Cancel();
            site.Stop();
        }
    }

    [Fact]
    public void Best_servers_skip_russian_exits_unless_nothing_else_works()
    {
        ServerScore[] scores =
        [
            new("a", "Москва", "vless", 20, 90, "RU", null),
            new("b", "Швеция", "vless", 60, 40, "SE", null),
            new("c", "Германия", "vless", 50, 10, "DE", null),
            new("d", "Мёртвый", "vless", null, null, null, "не ответил"),
        ];
        Assert.Equal(["b", "c"], ServerBenchmark.Best(scores));
        Assert.Equal(["a"], ServerBenchmark.Best([scores[0], scores[3]]));
    }

    /// <summary>Мини-сайт для замера: /trace — страна, /down — 2 МБ, остальное — пустой ответ.</summary>
    private static async Task ServeAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var reader = new StreamReader(stream, Encoding.ASCII);
                        var first = await reader.ReadLineAsync(ct) ?? "";
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct)))
                        {
                        }
                        var body = first.Contains("/trace") ? Encoding.ASCII.GetBytes("ip=203.0.113.5\nloc=NL\n")
                            : first.Contains("/down") ? new byte[2_000_000] : [];
                        var head = Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(head, ct);
                        await stream.WriteAsync(body, ct);
                    }
                    catch (Exception e) when (e is IOException or OperationCanceledException or SocketException)
                    {
                    }
                }
            }, ct);
        }
    }

    [Fact]
    public void Subscription_store_encrypts_link_and_drops_cache_on_new_link()
    {
        var store = new SubscriptionStore(_dir);
        const string link = "https://sub.example.com/secret-token";

        store.SaveSource(link);
        store.SaveCache(new CachedSubscription("body", new Dictionary<string, string> { ["profile-title"] = "x" }, DateTimeOffset.UnixEpoch));

        Assert.Equal(link, store.LoadSource());
        Assert.Equal("body", store.LoadCache()!.Body);
        Assert.DoesNotContain("secret-token", Encoding.Latin1.GetString(File.ReadAllBytes(Path.Combine(_dir, "subscription.bin"))));

        store.SaveSource("https://sub.example.com/other");
        Assert.Null(store.LoadCache());

        store.Delete();
        Assert.False(store.HasSubscription);
    }

    [Theory]
    [InlineData("https://auth.example.com/AbC123", true)]
    [InlineData("vless://x@y:1#n", true)]
    [InlineData("vpn://AAAA", false)]
    [InlineData("привет", false)]
    public void Pasted_text_is_recognised(string text, bool subscription)
    {
        Assert.Equal(subscription, SubscriptionClient.LooksLikeSubscription(text));
    }

    [Fact]
    public async Task Profile_uses_cached_list_when_site_is_down_and_refreshes_when_stale()
    {
        var store = new SubscriptionStore(_dir);
        store.SaveSource("https://sub.example.com/token");
        var time = new FakeTime();
        var online = true;
        var fetches = 0;
        var client = new SubscriptionClient((_, _) =>
        {
            fetches++;
            return online
                ? Task.FromResult((Base64Body(), new Dictionary<string, string> { ["profile-update-interval"] = "3" }))
                : Task.FromException<(string, Dictionary<string, string>)>(new HttpRequestException("нет сети"));
        });
        var profile = new SubscriptionProfile(store, client, new NullTunnel(), time);

        var plan = await profile.PrepareAsync(default);
        Assert.NotNull(plan);
        Assert.Equal(1, fetches);
        Assert.Contains("203.0.113.10", plan.EndpointHosts);

        // Через час список свежий — не качаем; через 4 часа сайт лежит — работаем на старом
        time.Now += TimeSpan.FromHours(1);
        Assert.Equal(plan.Conf, (await profile.PrepareAsync(default))!.Conf);
        Assert.Equal(1, fetches);

        online = false;
        time.Now += TimeSpan.FromHours(3);
        Assert.Equal(plan.Conf, (await profile.PrepareAsync(default))!.Conf); // тот же выбор серверов — без переподключения
        Assert.Equal(2, fetches);
    }

    [Fact]
    public async Task Expired_subscription_is_reported()
    {
        var store = new SubscriptionStore(_dir);
        store.SaveSource(string.Join("\n", Links));
        store.SaveCache(new CachedSubscription(Base64Body(),
            new Dictionary<string, string> { ["subscription-userinfo"] = "expire=1000" }, DateTimeOffset.UtcNow));
        var profile = new SubscriptionProfile(store, new SubscriptionClient(), new NullTunnel(), new FakeTime { Now = DateTimeOffset.UtcNow });

        Assert.Null(await profile.PrepareAsync(default));
        Assert.Contains("Срок подписки закончился", profile.Error);
    }

    // ---------- Автовыбор ----------

    private sealed class FakeProfile(string id, string name) : IVpnProfile
    {
        public string Id => id;
        public string Name => name;
        public bool Configured { get; set; } = true;
        public FakeTunnel Tunnel { get; } = new();
        IVpnTunnel IVpnProfile.Tunnel => Tunnel;
        public string? Error => null;
        public TimeSpan Latency { get; set; } = TimeSpan.FromMilliseconds(100);

        public Task<TunnelPlan?> PrepareAsync(CancellationToken ct) =>
            Task.FromResult<TunnelPlan?>(new TunnelPlan($"conf-{id}", ["198.51.100.1"]));
    }

    private sealed class FakeTunnel : IVpnTunnel
    {
        public bool Running { get; private set; }
        public bool Dead { get; set; }
        public int Starts { get; private set; }
        public string? LastError => Dead ? "молчит" : null;

        public Task<bool> StartAsync(string conf, CancellationToken ct)
        {
            Starts++;
            Running = true;
            return Task.FromResult(true);
        }

        public Task StopAsync(CancellationToken ct)
        {
            Running = false;
            return Task.CompletedTask;
        }

        public Task<TunnelHealth> GetHealthAsync(CancellationToken ct) =>
            Task.FromResult(!Running ? TunnelHealth.Stopped : Dead ? TunnelHealth.NoHandshake : TunnelHealth.Connected);
    }

    private sealed class NullTunnel : IVpnTunnel
    {
        public string? LastError => null;
        public Task<bool> StartAsync(string conf, CancellationToken ct) => Task.FromResult(true);
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<TunnelHealth> GetHealthAsync(CancellationToken ct) => Task.FromResult(TunnelHealth.Connected);
    }

    private sealed class FakeRoutes : IRouteTable
    {
        public IReadOnlyList<string> Installed => [];
        public void Reset() { }
        public bool Add(System.Net.IPNetwork network) => true;
        public void Sync(IReadOnlyCollection<System.Net.IPNetwork> wanted) { }
    }

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private (VpnController Vpn, FakeProfile Amnezia, FakeProfile Sub, FakeTime Time, SettingsStore Settings) TwoProfiles()
    {
        var amnezia = new FakeProfile("amnezia", "ключ Amnezia");
        var sub = new FakeProfile("subscription", "подписка «Quattro VPN»");
        var time = new FakeTime();
        var settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var lists = new RouteLists
        {
            Domains = Enum.GetValues<RouteSet>().ToDictionary(s => s, _ => (IReadOnlyList<string>)[]),
            Networks = Enum.GetValues<RouteSet>().ToDictionary(s => s, _ => (IReadOnlyList<System.Net.IPNetwork>)[]),
        };
        IVpnProfile? measuring = null;
        var vpn = new VpnController([amnezia, sub], () => new Router(lists, (_, _) => Task.FromResult(Array.Empty<System.Net.IPAddress>()), time),
            new FakeRoutes(), dns: null, settings, time, handshakeWait: TimeSpan.Zero, otherVpns: () => [],
            // Задержку «меряем» у того, чей туннель сейчас поднят
            measure: _ => Task.FromResult<TimeSpan?>((amnezia.Tunnel.Running ? amnezia : sub).Latency));
        _ = measuring;
        return (vpn, amnezia, sub, time, settings);
    }

    private static readonly IReadOnlySet<RouteSet> NoFallback = new HashSet<RouteSet>();

    [Fact]
    public async Task Only_one_configured_is_used_without_comparing()
    {
        var (vpn, amnezia, sub, _, _) = TwoProfiles();
        amnezia.Configured = false;

        var status = await vpn.ApplyAsync(NoFallback, default);

        Assert.Equal(VpnState.Connected, status.State);
        Assert.Equal("подписка «Quattro VPN»", status.Profile);
        Assert.Equal(0, amnezia.Tunnel.Starts);
    }

    [Fact]
    public async Task With_both_the_faster_one_wins_and_is_remembered()
    {
        var (vpn, amnezia, sub, _, settings) = TwoProfiles();
        amnezia.Latency = TimeSpan.FromMilliseconds(180);
        sub.Latency = TimeSpan.FromMilliseconds(60);

        var status = await vpn.ApplyAsync(NoFallback, default);

        Assert.Equal("подписка «Quattro VPN»", status.Profile);
        Assert.True(sub.Tunnel.Running);
        Assert.False(amnezia.Tunnel.Running);
        Assert.Equal("subscription", settings.Load().PreferredVpn);
    }

    [Fact]
    public async Task Dead_active_vpn_fails_over_to_the_other()
    {
        var (vpn, amnezia, sub, time, _) = TwoProfiles();
        amnezia.Latency = TimeSpan.FromMilliseconds(50);
        sub.Latency = TimeSpan.FromMilliseconds(150);
        await vpn.ApplyAsync(NoFallback, default);
        Assert.True(amnezia.Tunnel.Running);

        // Сервер знакомого замолчал, прошла минута «подключаюсь» — переходим на подписку
        amnezia.Tunnel.Dead = true;
        time.Now += TimeSpan.FromMinutes(2);
        var status = await vpn.ApplyAsync(NoFallback, default);

        Assert.Equal(VpnState.Connected, status.State);
        Assert.Equal("подписка «Quattro VPN»", status.Profile);
        Assert.True(sub.Tunnel.Running);
        Assert.False(amnezia.Tunnel.Running);
    }

    [Fact]
    public async Task Small_speed_difference_does_not_cause_switching_on_daily_compare()
    {
        var (vpn, amnezia, sub, time, settings) = TwoProfiles();
        amnezia.Latency = TimeSpan.FromMilliseconds(100);
        sub.Latency = TimeSpan.FromMilliseconds(120);
        await vpn.ApplyAsync(NoFallback, default);
        Assert.Equal("amnezia", settings.Load().PreferredVpn);

        // Через сутки подписка стала чуть быстрее — не настолько, чтобы переходить
        sub.Latency = TimeSpan.FromMilliseconds(90);
        time.Now += TimeSpan.FromDays(1);
        var status = await vpn.ApplyAsync(NoFallback, default);
        Assert.Equal("ключ Amnezia", status.Profile);

        // А теперь заметно быстрее — переходим
        sub.Latency = TimeSpan.FromMilliseconds(40);
        time.Now += TimeSpan.FromDays(1);
        status = await vpn.ApplyAsync(NoFallback, default);
        Assert.Equal("подписка «Quattro VPN»", status.Profile);
    }

    [Fact]
    public async Task Removing_active_profile_switches_to_remaining_one()
    {
        var (vpn, amnezia, sub, _, _) = TwoProfiles();
        sub.Latency = TimeSpan.FromMilliseconds(10);
        await vpn.ApplyAsync(NoFallback, default);
        Assert.True(sub.Tunnel.Running);

        sub.Configured = false;
        vpn.ProfilesChanged();
        var status = await vpn.ApplyAsync(NoFallback, default);

        Assert.Equal("ключ Amnezia", status.Profile);
        Assert.False(sub.Tunnel.Running);
        Assert.True(amnezia.Tunnel.Running);
    }

    private static string? FindUp(params string[] relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine([dir.FullName, .. relative]);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
