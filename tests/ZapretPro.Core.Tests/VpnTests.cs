using System.Net;
using System.Text.Json.Nodes;
using ZapretPro.Core.Health;
using ZapretPro.Core.Key;
using ZapretPro.Core.Vpn;

namespace ZapretPro.Core.Tests;

public sealed class VpnTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OdnaKnopkaTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // ---------- AwgConfig ----------

    [Fact]
    public void AwgConfig_from_key_keeps_amnezia_params_and_replaces_routing()
    {
        var key = KeyParser.Parse(SyntheticKey.Build(SyntheticKey.Config("amnezia-awg", "amnezia-awg")));

        var config = AwgConfig.FromKey(key);
        var conf = config.ToConf(["1.2.3.4/32", "5.6.0.0/16"]);

        Assert.Equal("203.0.113.10", config.EndpointHost);
        Assert.Contains("Jc = 4", conf);
        Assert.Contains("H4 = 1520812435", conf);
        Assert.Contains("I2 = <b 0x00>", conf);
        Assert.Contains($"PrivateKey = {SyntheticKey.FakeSecret}=", conf);
        Assert.Contains("PresharedKey = FAKE-PSK=", conf);
        Assert.Contains("Endpoint = 203.0.113.10:51820", conf);
        Assert.Contains("AllowedIPs = 1.2.3.4/32, 5.6.0.0/16", conf);
        Assert.Contains("MTU = 1376", conf);

        Assert.DoesNotContain("0.0.0.0/0", conf);
        Assert.DoesNotContain("DNS", conf);
        Assert.DoesNotContain("$", conf);
        Assert.DoesNotContain("I1", conf);
        Assert.DoesNotContain("PostUp", conf);
        Assert.DoesNotContain("calc.exe", conf);
    }

    [Fact]
    public void AwgConfig_ToString_has_no_secrets()
    {
        var config = AwgConfig.Parse(SyntheticKey.AwgText);

        Assert.DoesNotContain(SyntheticKey.FakeSecret, config.ToString());
        Assert.DoesNotContain("203.0.113.10", config.ToString());
    }

    [Fact]
    public void AwgConfig_keeps_mtu_from_text_over_default()
    {
        var conf = AwgConfig.Parse(SyntheticKey.AwgText.Replace("Jc = 4", "Jc = 4\nMTU = 1280"), "1376").ToConf(["1.1.1.1/32"]);

        Assert.Contains("MTU = 1280", conf);
        Assert.DoesNotContain("MTU = 1376", conf);
    }

    [Fact]
    public void AwgConfig_handles_hostname_and_ipv6_endpoints()
    {
        Assert.Equal("vpn.example.com", AwgConfig.Parse(SyntheticKey.AwgText.Replace("203.0.113.10:51820", "vpn.example.com:443")).EndpointHost);
        Assert.Equal("2001:db8::1", AwgConfig.Parse(SyntheticKey.AwgText.Replace("203.0.113.10:51820", "[2001:db8::1]:443")).EndpointHost);
    }

    [Theory]
    [InlineData("PrivateKey")]
    [InlineData("Address")]
    [InlineData("PublicKey")]
    [InlineData("Endpoint")]
    public void AwgConfig_rejects_config_without_required_field(string field)
    {
        var text = string.Join('\n', SyntheticKey.AwgText.Split('\n').Where(l => !l.TrimStart().StartsWith(field + " ")));

        Assert.Throws<KeyParseException>(() => AwgConfig.Parse(text));
    }

    [Fact]
    public void AwgConfig_rejects_non_awg_key_and_key_without_config()
    {
        var xray = KeyParser.Parse(SyntheticKey.Build(SyntheticKey.Config("amnezia-xray", "amnezia-xray")));
        Assert.Throws<KeyParseException>(() => AwgConfig.FromKey(xray));

        var config = SyntheticKey.Config("amnezia-awg", "amnezia-awg");
        ((JsonObject)config["containers"]![0]!["awg"]!).Remove("last_config");
        var empty = KeyParser.Parse(SyntheticKey.Build(config));
        Assert.Throws<KeyParseException>(() => AwgConfig.FromKey(empty));
    }

    // ---------- Router ----------

    private static RouteLists Lists() => new()
    {
        Domains = new Dictionary<RouteSet, IReadOnlyList<string>>
        {
            [RouteSet.Ai] = ["chatgpt.com", "claude.ai", "dead.example"],
            [RouteSet.Blocked] = [],
            [RouteSet.Telegram] = ["telegram.org"],
            [RouteSet.YouTube] = [],
            [RouteSet.Discord] = ["discord.com"],
        },
        Networks = new Dictionary<RouteSet, IReadOnlyList<IPNetwork>>
        {
            [RouteSet.Ai] = [IPNetwork.Parse("160.79.104.0/23")],
            [RouteSet.Blocked] = [],
            [RouteSet.Telegram] = [IPNetwork.Parse("149.154.160.0/20")],
            [RouteSet.YouTube] = [IPNetwork.Parse("142.250.0.0/15"), IPNetwork.Parse("203.0.113.0/24")],
            [RouteSet.Discord] = [],
        },
    };

    private sealed class FakeDns
    {
        public Dictionary<string, string[]> Answers { get; } = new()
        {
            ["chatgpt.com"] = ["104.18.32.47", "2606:4700::6812:202f"],
            ["claude.ai"] = ["160.79.104.10"],
            ["telegram.org"] = ["149.154.167.99"],
            ["discord.com"] = ["162.159.135.232"],
        };

        public Task<IPAddress[]> Resolve(string host, CancellationToken ct) =>
            Answers.TryGetValue(host, out var ips)
                ? Task.FromResult(ips.Select(IPAddress.Parse).ToArray())
                : Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException(11001));
    }

    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Router_routes_only_ai_by_default()
    {
        var router = new Router(Lists(), new FakeDns().Resolve);

        var plan = await router.ComputeAsync(new HashSet<RouteSet> { RouteSet.Ai }, "203.0.113.10", default);

        // claude.ai уже внутри подсети 160.79.104.0/23, IPv6 и несуществующий домен отброшены
        Assert.Equal(["104.18.32.47/32", "160.79.104.0/23"], plan.AllowedIps);
    }

    [Fact]
    public async Task Router_adds_fallback_sets()
    {
        var router = new Router(Lists(), new FakeDns().Resolve);

        var plan = await router.ComputeAsync(new HashSet<RouteSet> { RouteSet.Ai, RouteSet.Telegram, RouteSet.Discord }, "198.51.100.7", default);

        Assert.Contains("149.154.160.0/20", plan.AllowedIps);
        Assert.Contains("162.159.135.232/32", plan.AllowedIps);
        Assert.DoesNotContain("149.154.167.99/32", plan.AllowedIps); // уже покрыт подсетью Telegram
    }

    [Fact]
    public async Task Router_never_routes_vpn_server_into_tunnel()
    {
        var dns = new FakeDns();
        dns.Answers["vpn.example.com"] = ["203.0.113.10"];
        dns.Answers["chatgpt.com"] = ["203.0.113.10", "104.18.32.47"];
        var router = new Router(Lists(), dns.Resolve);

        var plan = await router.ComputeAsync(new HashSet<RouteSet> { RouteSet.Ai, RouteSet.YouTube }, "vpn.example.com", default);

        Assert.DoesNotContain("203.0.113.10/32", plan.AllowedIps);
        Assert.DoesNotContain("203.0.113.0/24", plan.AllowedIps);
        Assert.Contains("142.250.0.0/15", plan.AllowedIps);
        Assert.Contains(plan.Warnings, w => w.Contains("203.0.113.0/24"));
    }

    [Fact]
    public async Task Router_remembers_rotating_cdn_addresses_for_a_week()
    {
        var dns = new FakeDns();
        var time = new FakeTime();
        var router = new Router(Lists(), dns.Resolve, time);
        var ai = new HashSet<RouteSet> { RouteSet.Ai };

        await router.ComputeAsync(ai, "203.0.113.10", default);
        dns.Answers["chatgpt.com"] = ["104.18.33.47"];
        time.Now += TimeSpan.FromDays(6);
        var plan = await router.ComputeAsync(ai, "203.0.113.10", default);
        Assert.Contains("104.18.32.47/32", plan.AllowedIps);
        Assert.Contains("104.18.33.47/32", plan.AllowedIps);

        time.Now += TimeSpan.FromDays(2);
        plan = await router.ComputeAsync(ai, "203.0.113.10", default);
        Assert.DoesNotContain("104.18.32.47/32", plan.AllowedIps);
        Assert.Contains("104.18.33.47/32", plan.AllowedIps);
    }

    [Fact]
    public async Task Router_skips_private_addresses()
    {
        var dns = new FakeDns();
        dns.Answers["chatgpt.com"] = ["192.168.1.1", "127.0.0.1", "104.18.32.47"];
        var router = new Router(Lists(), dns.Resolve);

        var plan = await router.ComputeAsync(new HashSet<RouteSet> { RouteSet.Ai }, "203.0.113.10", default);

        Assert.Equal(["104.18.32.47/32", "160.79.104.0/23"], plan.AllowedIps);
    }

    [Fact]
    public void RouteLists_reads_bundled_files()
    {
        var lists = RouteLists.Load(Path.Combine(AppContext.BaseDirectory, "data", "vpn"));

        Assert.Contains("chatgpt.com", lists.Domains[RouteSet.Ai]);
        Assert.Contains("claude.ai", lists.Domains[RouteSet.Ai]);
        Assert.Contains(IPNetwork.Parse("149.154.160.0/20"), lists.Networks[RouteSet.Telegram]);
        Assert.True(lists.Networks[RouteSet.YouTube].Count > 20);
        Assert.Contains("discord.com", lists.Domains[RouteSet.Discord]);
        Assert.All(lists.Domains.Values.SelectMany(d => d), d => Assert.DoesNotContain(" ", d));
    }

    [Fact]
    public void Bundled_blocked_list_excludes_what_zapret_and_proxy_already_handle()
    {
        var zapretLists = Path.Combine(_dir, "zapret");
        Directory.CreateDirectory(zapretLists);
        File.WriteAllText(Path.Combine(zapretLists, "list-google.txt"), "youtube.com\ngooglevideo.com\nytimg.com\nggpht.com\nyoutu.be\n");
        File.WriteAllText(Path.Combine(zapretLists, "list-general.txt"), "^dns.google\ndiscord.com\n");

        var lists = RouteLists.Load(Path.Combine(AppContext.BaseDirectory, "data", "vpn"), null,
            [Path.Combine(zapretLists, "list-google.txt"), Path.Combine(zapretLists, "list-general.txt")]);
        var blocked = lists.Domains[RouteSet.Blocked];

        Assert.Contains("instagram.com", blocked);
        Assert.Contains("www.instagram.com", blocked);
        Assert.Contains("facebook.com", blocked);
        Assert.True(blocked.Count > 1000);
        Assert.DoesNotContain("youtube.com", blocked);
        Assert.DoesNotContain("googlevideo.com", blocked);
        Assert.DoesNotContain(blocked, d => d.EndsWith(".youtube.com") || d.EndsWith(".googlevideo.com"));
        Assert.DoesNotContain(blocked, d => d.StartsWith('.'));
        Assert.True(lists.Networks[RouteSet.Blocked].Count > 10);
    }

    [Fact]
    public void Blocked_list_prefers_downloaded_copy_and_drops_telegram_domains()
    {
        var builtIn = Path.Combine(_dir, "builtin");
        var updated = Path.Combine(_dir, "updated");
        Directory.CreateDirectory(builtIn);
        Directory.CreateDirectory(updated);
        File.WriteAllText(Path.Combine(builtIn, "ai-domains.txt"), "chatgpt.com");
        File.WriteAllText(Path.Combine(builtIn, "telegram-domains.txt"), "telegram.org\nt.me");
        File.WriteAllText(Path.Combine(builtIn, "blocked-domains.txt"), "old.example");
        File.WriteAllText(Path.Combine(updated, "blocked-domains.txt"), "# шапка\nnew.example\nweb.telegram.org\nt.me\n.ua\nwww.site.example");

        var blocked = RouteLists.Load(builtIn, updated).Domains[RouteSet.Blocked];

        Assert.Equal(["new.example", "www.new.example", "www.site.example"], blocked);
    }

    [Fact]
    public async Task ListUpdater_downloads_once_a_day_and_rejects_garbage()
    {
        var time = new FakeTime();
        var calls = 0;
        var answer = string.Join('\n', Enumerable.Range(0, 600).Select(i => $"site{i}.example"));
        var updater = new ListUpdater(_dir, (url, _) =>
        {
            calls++;
            return Task.FromResult(url.EndsWith("inside-raw.lst") ? answer : "157.240.0.0/16\n31.13.24.0/21\n" +
                string.Join('\n', Enumerable.Range(1, 10).Select(i => $"10.{i}.0.0/16")));
        }, time);

        Assert.True(await updater.RefreshIfStaleAsync(default));
        Assert.Equal(3, calls);
        Assert.Contains("site599.example", File.ReadAllText(Path.Combine(_dir, "blocked-domains.txt")));

        // Через час — не качаем снова
        time.Now += TimeSpan.FromHours(1);
        Assert.False(await updater.RefreshIfStaleAsync(default));
        Assert.Equal(3, calls);

        // Через сутки пришёл обрезок — старый список остаётся
        time.Now += TimeSpan.FromDays(1);
        answer = "only.example";
        await updater.RefreshIfStaleAsync(default);
        Assert.Contains("site599.example", File.ReadAllText(Path.Combine(_dir, "blocked-domains.txt")));
    }

    [Fact]
    public async Task ListUpdater_keeps_old_list_when_offline()
    {
        var updater = new ListUpdater(_dir, (_, _) => Task.FromException<string>(new HttpRequestException("нет сети")));

        Assert.False(await updater.RefreshIfStaleAsync(default));
        Assert.False(File.Exists(Path.Combine(_dir, "blocked-domains.txt")));
    }

    [Fact]
    public async Task Router_drops_subnets_inside_wider_ones()
    {
        var lists = Lists();
        var nets = (Dictionary<RouteSet, IReadOnlyList<IPNetwork>>)lists.Networks;
        nets[RouteSet.Telegram] = [IPNetwork.Parse("149.154.167.0/24"), IPNetwork.Parse("149.154.160.0/20"), IPNetwork.Parse("149.154.167.99/32")];
        var router = new Router(lists, new FakeDns().Resolve);

        var plan = await router.ComputeAsync(new HashSet<RouteSet> { RouteSet.Telegram }, "203.0.113.10", default);

        Assert.Equal(["149.154.160.0/20"], plan.AllowedIps);
    }

    [Fact]
    public void RouteLists_accepts_cidr_and_ip_lines_in_domain_files()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "ai-domains.txt"), "# комментарий\nchatgpt.com  # хвост\n1.2.3.0/24\n5.6.7.8\nCLAUDE.AI\n");

        var lists = RouteLists.Load(_dir);

        Assert.Equal(["chatgpt.com", "claude.ai"], lists.Domains[RouteSet.Ai]);
        Assert.Equal([IPNetwork.Parse("1.2.3.0/24"), IPNetwork.Parse("5.6.7.8/32")], lists.Networks[RouteSet.Ai]);
        Assert.Empty(lists.Domains[RouteSet.Telegram]);
    }

    // ---------- VpnController ----------

    private sealed class FakeTunnel : IVpnTunnel
    {
        public List<string> Started { get; } = [];
        public int Stops { get; private set; }
        public TunnelHealth Health { get; set; } = TunnelHealth.Connected;
        public bool FailStart { get; set; }
        public string? LastError { get; private set; }
        private bool _running;

        public Task<bool> StartAsync(string conf, CancellationToken ct)
        {
            Started.Add(conf);
            _running = !FailStart;
            LastError = FailStart ? "служба не встала" : null;
            return Task.FromResult(_running);
        }

        public Task StopAsync(CancellationToken ct)
        {
            Stops++;
            _running = false;
            return Task.CompletedTask;
        }

        public Task<TunnelHealth> GetHealthAsync(CancellationToken ct) =>
            Task.FromResult(_running ? Health : TunnelHealth.Stopped);

        public void Crash() => _running = false;
    }

    private readonly List<string> _otherVpns = [];

    private (VpnController Vpn, FakeTunnel Tunnel, KeyStore Keys, FakeTime Time) Controller()
    {
        var keys = new KeyStore(Path.Combine(_dir, "key.bin"));
        var tunnel = new FakeTunnel();
        var time = new FakeTime();
        var dns = new FakeDns();
        var vpn = new VpnController(keys, () => new Router(Lists(), dns.Resolve, time), tunnel, time,
            handshakeWait: TimeSpan.Zero, otherVpns: () => _otherVpns);
        return (vpn, tunnel, keys, time);
    }

    [Fact]
    public async Task Controller_blames_other_running_vpn()
    {
        var (vpn, tunnel, keys, _) = Controller();
        keys.Save(SyntheticKey.Build(SyntheticKey.Config("amnezia-awg", "amnezia-awg")));
        tunnel.FailStart = true;
        _otherVpns.Add("AmneziaVPN (AmneziaVPN TUN)");

        var status = await vpn.ApplyAsync(NoFallback, default);

        Assert.Equal(VpnState.Failed, status.State);
        Assert.StartsWith("Включён другой VPN (AmneziaVPN (AmneziaVPN TUN))", status.Error);
        Assert.Contains("служба не встала", status.Error);
    }

    [Fact]
    public void Failure_is_taken_from_the_error_line_not_from_shutdown_noise()
    {
        const string dump = """
            2026-10-21 23:09:38.100000: [TUN] [FiolKid] Starting AmneziaWG/3.1.0
            2026-10-21 23:09:38.200000: [TUN] [FiolKid] Unable to set ips: The object already exists.
            2026-10-21 23:09:38.300000: [TUN] [FiolKid] Routine: encryption worker 1 - stopped
            2026-10-21 23:10:42.000000: [TUN] [Other] Unable to do something else
            2026-10-21 23:10:42.100000: [TUN] [FiolKid] Starting AmneziaWG/3.1.0
            2026-10-21 23:10:42.110000: [TUN] [FiolKid] Creating network adapter
            2026-10-21 23:10:42.120000: [TUN] [FiolKid] Unable to set ips: The object already exists.
            2026-10-21 23:10:42.126965: [TUN] [FiolKid] Routine: encryption worker 3 - stopped
            2026-10-21 23:10:42.126965: [TUN] [FiolKid] Routine: encryption worker 2 - stopped
            2026-10-21 23:10:42.130000: [TUN] [FiolKid] Shutting down
            """;

        Assert.Equal("[FiolKid] Unable to set ips: The object already exists.", VpnManager.FailureFromLog(dump, "FiolKid"));
        Assert.Equal("журнал AmneziaWG пуст", VpnManager.FailureFromLog("", "FiolKid"));
    }

    [Fact]
    public void Other_vpn_adapters_are_recognised_but_ours_and_regular_ones_are_not()
    {
        var found = OtherVpn.Filter(
        [
            ("Ethernet", "Realtek PCIe GbE Family Controller"),
            ("FiolKid", "AmneziaWG Tunnel"),
            ("AmneziaVPN", "AmneziaVPN TUN"),
            ("Wi-Fi", "Intel(R) Wi-Fi 6 AX201"),
            ("wg0", "WireGuard Tunnel"),
        ], "FiolKid");

        Assert.Equal(["AmneziaVPN (AmneziaVPN TUN)", "wg0 (WireGuard Tunnel)"], found);
    }

    private static readonly IReadOnlySet<RouteSet> NoFallback = new HashSet<RouteSet>();

    [Fact]
    public async Task Controller_without_key_does_nothing()
    {
        var (vpn, tunnel, _, _) = Controller();

        var status = await vpn.ApplyAsync(NoFallback, default);

        Assert.Equal(VpnState.NoKey, status.State);
        Assert.Empty(tunnel.Started);
        Assert.False(vpn.HasKey);
    }

    [Fact]
    public async Task Controller_with_unsupported_key_explains_why()
    {
        var (vpn, tunnel, keys, _) = Controller();
        keys.Save(SyntheticKey.Build(SyntheticKey.Config("amnezia-xray", "amnezia-xray")));

        var status = await vpn.ApplyAsync(NoFallback, default);

        Assert.Equal(VpnState.BadKey, status.State);
        Assert.Contains("AmneziaWG", status.Error);
        Assert.Empty(tunnel.Started);
    }

    [Fact]
    public async Task Controller_starts_tunnel_once_and_keeps_it_while_routes_are_same()
    {
        var (vpn, tunnel, keys, time) = Controller();
        keys.Save(SyntheticKey.Build(SyntheticKey.Config("amnezia-awg", "amnezia-awg")));

        var status = await vpn.ApplyAsync(NoFallback, default);
        Assert.Equal(VpnState.Connected, status.State);
        Assert.Equal(2, status.RouteCount);
        Assert.Single(tunnel.Started);
        Assert.Contains("AllowedIPs = 104.18.32.47/32, 160.79.104.0/23", tunnel.Started[0]);

        time.Now += TimeSpan.FromMinutes(30);
        await vpn.ApplyAsync(NoFallback, default);
        Assert.Single(tunnel.Started);
    }

    [Fact]
    public async Task Controller_restarts_tunnel_when_fallback_changes()
    {
        var (vpn, tunnel, keys, _) = Controller();
        keys.Save(SyntheticKey.Build(SyntheticKey.Config("amnezia-awg", "amnezia-awg")));
        await vpn.ApplyAsync(NoFallback, default);

        await vpn.ApplyAsync(new HashSet<RouteSet> { RouteSet.Telegram }, default);
        Assert.Equal(2, tunnel.Started.Count);
        Assert.Contains("149.154.160.0/20", tunnel.Started[1]);

        await vpn.ApplyAsync(NoFallback, default);
        Assert.Equal(3, tunnel.Started.Count);
        Assert.DoesNotContain("149.154.160.0/20", tunnel.Started[2]);
    }

    [Fact]
    public async Task Controller_restarts_crashed_tunnel_and_picks_up_new_key()
    {
        var (vpn, tunnel, keys, _) = Controller();
        keys.Save(SyntheticKey.Build(SyntheticKey.Config("amnezia-awg", "amnezia-awg")));
        await vpn.ApplyAsync(NoFallback, default);

        tunnel.Crash();
        await vpn.ApplyAsync(NoFallback, default);
        Assert.Equal(2, tunnel.Started.Count);

        var config = SyntheticKey.Config("amnezia-awg", "amnezia-awg");
        var awg = (JsonObject)config["containers"]![0]!["awg"]!;
        awg["last_config"] = JsonNode.Parse(awg["last_config"]!.GetValue<string>())!.AsObject()
            .Also(o => o["config"] = SyntheticKey.AwgText.Replace("51820", "443")).ToJsonString();
        keys.Save(SyntheticKey.Build(config));
        await vpn.ApplyAsync(NoFallback, default);

        Assert.Equal(3, tunnel.Started.Count);
        Assert.Contains("Endpoint = 203.0.113.10:443", tunnel.Started[2]);
    }

    [Fact]
    public async Task Controller_reports_silent_server_and_failed_start()
    {
        var (vpn, tunnel, keys, time) = Controller();
        keys.Save(SyntheticKey.Build(SyntheticKey.Config("amnezia-awg", "amnezia-awg")));

        // Сразу после запуска молчание сервера — это ещё «подключаюсь», а не поломка
        tunnel.Health = TunnelHealth.NoHandshake;
        var connecting = await vpn.ApplyAsync(NoFallback, default);
        Assert.Equal(VpnState.Connecting, connecting.State);
        Assert.Null(connecting.Error);

        time.Now += TimeSpan.FromMinutes(2);
        var silent = await vpn.ApplyAsync(NoFallback, default);
        Assert.Equal(VpnState.ServerNotResponding, silent.State);
        Assert.Contains("одном устройстве", silent.Error);

        tunnel.Health = TunnelHealth.Connected;
        Assert.Equal(VpnState.Connected, (await vpn.ApplyAsync(NoFallback, default)).State);

        tunnel.Crash();
        tunnel.FailStart = true;
        var status = await vpn.ApplyAsync(NoFallback, default);
        Assert.Equal(VpnState.Failed, status.State);
        Assert.Equal("служба не встала", status.Error);
    }

    [Fact]
    public async Task Controller_stops_tunnel_when_key_removed()
    {
        var (vpn, tunnel, keys, _) = Controller();
        keys.Save(SyntheticKey.Build(SyntheticKey.Config("amnezia-awg", "amnezia-awg")));
        await vpn.ApplyAsync(NoFallback, default);

        keys.Delete();
        var status = await vpn.ApplyAsync(NoFallback, default);

        Assert.Equal(VpnState.NoKey, status.State);
        Assert.Equal(1, tunnel.Stops);
    }

    // ---------- Проверка нейросетей ----------

    [Theory]
    [InlineData("fl=1\nh=chatgpt.com\nip=1.2.3.4\nloc=NL\ntls=TLSv1.3\n", "NL")]
    [InlineData("loc=ru\n", "RU")]
    [InlineData("ip=1.2.3.4\n", null)]
    public void Trace_country_is_parsed(string trace, string? expected)
    {
        Assert.Equal(expected, HealthChecker.ParseTraceCountry(trace));
    }

    [Theory]
    [InlineData("NL", true)]
    [InlineData("MD", true)]
    [InlineData("RU", false)]
    [InlineData("BY", false)]
    [InlineData(null, false)]
    public void Ai_works_only_from_supported_countries(string? country, bool ok)
    {
        Assert.Equal(ok, HealthChecker.IsAiCountryOk(country));
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
