using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ZapretPro.Core.Dns;
using ZapretPro.Core.Vpn;

namespace ZapretPro.Core.Tests;

public sealed class DnsTests
{
    /// <summary>DNS-запрос, как его шлёт Windows.</summary>
    private static byte[] Query(string name, ushort type, ushort id = 0x1234)
    {
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        bytes.AddRange([(byte)(type >> 8), (byte)type, 0, 1]);
        return bytes.ToArray();
    }

    /// <summary>Ответ с CNAME и A-записями, имена сжаты ссылками — как отвечают настоящие серверы.</summary>
    private static byte[] Answer(byte[] query, params string[] ips)
    {
        DnsMessage.TryReadQuestion(query, out _, out _, out var end);
        var bytes = new List<byte>(query[..end]);
        bytes[2] = 0x81;
        bytes[3] = 0x80;
        bytes[6] = 0;
        bytes[7] = (byte)(ips.Length + 1);
        // CNAME: имя — ссылка на вопрос (0xC00C), данные — «cdn» + ссылка на вопрос
        bytes.AddRange([0xC0, 0x0C, 0, 5, 0, 1, 0, 0, 0, 60, 0, 6, 3, (byte)'c', (byte)'d', (byte)'n', 0xC0, 0x0C]);
        foreach (var ip in ips)
        {
            bytes.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4]);
            bytes.AddRange(IPAddress.Parse(ip).GetAddressBytes());
        }
        return bytes.ToArray();
    }

    [Fact]
    public void Question_is_read()
    {
        Assert.True(DnsMessage.TryReadQuestion(Query("Abs.TWIMG.com", DnsMessage.TypeA), out var name, out var type, out var end));
        Assert.Equal("abs.twimg.com", name);
        Assert.Equal(DnsMessage.TypeA, type);
        Assert.Equal(Query("abs.twimg.com", 1).Length, end);

        Assert.False(DnsMessage.TryReadQuestion(new byte[5], out _, out _, out _));
        Assert.False(DnsMessage.TryReadQuestion(Query("x.com", 1)[..14], out _, out _, out _));
    }

    [Fact]
    public void A_records_are_read_through_cname_and_compression()
    {
        var answer = Answer(Query("api.x.com", 1), "162.159.140.229", "172.66.0.227");

        Assert.Equal([IPAddress.Parse("162.159.140.229"), IPAddress.Parse("172.66.0.227")], DnsMessage.ReadARecords(answer));
        Assert.Empty(DnsMessage.ReadARecords(answer[..20]));
    }

    [Fact]
    public void Empty_answer_keeps_id_and_question()
    {
        var query = Query("chatgpt.com", DnsMessage.TypeAaaa, 0xBEEF);

        var answer = DnsMessage.EmptyAnswer(query);

        Assert.Equal(0xBEEF, DnsMessage.Id(answer));
        Assert.Equal(0x81, answer[2]);      // ответ, RD
        Assert.Equal(0x80, answer[3]);      // RA, ошибок нет
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(4)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(6)));
        Assert.True(DnsMessage.TryReadQuestion(answer, out var name, out var type, out _));
        Assert.Equal(("chatgpt.com", DnsMessage.TypeAaaa), (name, type));
        Assert.Equal(2, DnsMessage.ServerFailure(query)[3] & 0x0F);
    }

    [Fact]
    public void Matcher_covers_subdomains_but_not_lookalikes()
    {
        var matcher = new DomainMatcher([("x.com", RouteSet.Blocked), ("twimg.com", RouteSet.Blocked), ("chatgpt.com", RouteSet.Ai),
            ("chatgpt.com", RouteSet.Blocked)]);

        Assert.Equal(RouteSet.Blocked, matcher.Match("x.com"));
        Assert.Equal(RouteSet.Blocked, matcher.Match("api.x.com."));
        Assert.Equal(RouteSet.Blocked, matcher.Match("pbs.twimg.com"));
        Assert.Equal(RouteSet.Ai, matcher.Match("ab.chatgpt.com"));   // первый набор важнее
        Assert.Null(matcher.Match("box.com"));                        // не «x.com», хоть и заканчивается так же
        Assert.Null(matcher.Match("ya.ru"));
    }

    /// <summary>Поддельный DNS-сервер на 127.0.0.1: отвечает A-записью и запоминает, о чём спрашивали.</summary>
    private sealed class FakeUpstream : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));

        public FakeUpstream(string ip, Func<byte[], byte[]>? response = null)
        {
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    UdpReceiveResult request;
                    try
                    {
                        request = await _udp.ReceiveAsync();
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    DnsMessage.TryReadQuestion(request.Buffer, out var name, out _, out _);
                    Asked.Add(name);
                    if (Silent)
                        continue;
                    await _udp.SendAsync(response?.Invoke(request.Buffer) ?? Answer(request.Buffer, ip), request.RemoteEndPoint);
                }
            });
        }

        public IPEndPoint EndPoint => (IPEndPoint)_udp.Client.LocalEndPoint!;
        public System.Collections.Concurrent.ConcurrentBag<string> Asked { get; } = [];
        /// <summary>Перестать отвечать — как туннель, который вдруг начал терять UDP.</summary>
        public bool Silent { get; set; }
        public void Dispose() => _udp.Dispose();
    }

    private static SmartDns Dns(FakeUpstream normal, FakeUpstream secure, List<(IPAddress, RouteSet)> resolved, bool tunnelUp = true) =>
        new([normal.EndPoint], port: 0, secureUpstreams: [secure.EndPoint])
        {
            Classify = new DomainMatcher([("x.com", RouteSet.Blocked), ("chatgpt.com", RouteSet.Ai)]).Match,
            OnResolved = (ips, set) =>
            {
                lock (resolved)
                    resolved.AddRange(ips.Select(ip => (ip, set)));
                return Task.CompletedTask;
            },
            TunnelUp = () => tunnelUp,
        };

    [Fact]
    public async Task Ordinary_sites_go_to_the_previous_dns_untouched()
    {
        using var normal = new FakeUpstream("87.250.250.242");
        using var secure = new FakeUpstream("1.2.3.4");
        var resolved = new List<(IPAddress, RouteSet)>();
        var dns = Dns(normal, secure, resolved);

        var answer = await dns.HandleAsync(Query("ya.ru", DnsMessage.TypeA), default);

        Assert.Equal([IPAddress.Parse("87.250.250.242")], DnsMessage.ReadARecords(answer));
        Assert.Contains("ya.ru", normal.Asked);
        Assert.Empty(secure.Asked);
        Assert.Empty(resolved);
    }

    [Fact]
    public async Task Vpn_sites_are_asked_through_vpn_and_routed_before_answer()
    {
        using var normal = new FakeUpstream("10.10.10.10"); // так провайдер подменил бы адрес
        using var secure = new FakeUpstream("162.159.140.229");
        var resolved = new List<(IPAddress, RouteSet)>();
        var dns = Dns(normal, secure, resolved);

        var answer = await dns.HandleAsync(Query("api.x.com", DnsMessage.TypeA), default);

        Assert.Equal([IPAddress.Parse("162.159.140.229")], DnsMessage.ReadARecords(answer));
        Assert.Equal([(IPAddress.Parse("162.159.140.229"), RouteSet.Blocked)], resolved);
        Assert.Empty(normal.Asked);
    }

    [Fact]
    public async Task Vpn_sites_get_no_ipv6_so_browser_does_not_bypass_tunnel()
    {
        using var normal = new FakeUpstream("10.10.10.10");
        using var secure = new FakeUpstream("1.2.3.4");
        var dns = Dns(normal, secure, []);

        foreach (var type in new[] { DnsMessage.TypeAaaa, DnsMessage.TypeHttps })
        {
            var answer = await dns.HandleAsync(Query("chatgpt.com", type), default);
            Assert.Empty(DnsMessage.ReadARecords(answer));
            Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(6)));
        }
        Assert.Empty(normal.Asked);
        Assert.Empty(secure.Asked);
    }

    [Fact]
    public async Task Without_tunnel_everything_goes_to_the_previous_dns()
    {
        using var normal = new FakeUpstream("31.13.72.174");
        using var secure = new FakeUpstream("1.2.3.4");
        var resolved = new List<(IPAddress, RouteSet)>();
        var dns = Dns(normal, secure, resolved, tunnelUp: false);

        var answer = await dns.HandleAsync(Query("x.com", DnsMessage.TypeA), default);

        Assert.Equal([IPAddress.Parse("31.13.72.174")], DnsMessage.ReadARecords(answer));
        Assert.Empty(resolved);
        Assert.Empty(secure.Asked);
    }

    [Fact]
    public async Task Dead_vpn_dns_falls_back_to_previous_dns()
    {
        using var normal = new FakeUpstream("31.13.72.174");
        var dead = new IPEndPoint(IPAddress.Loopback, 9); // никто не слушает
        var routed = new List<(IPAddress, RouteSet)>();
        var logs = new List<string>();
        var dns = new SmartDns([normal.EndPoint], port: 0, secureUpstreams: [dead])
        {
            Classify = _ => RouteSet.Ai,
            TunnelUp = () => true,
            OnResolved = (ips, set) =>
            {
                lock (routed)
                    routed.AddRange(ips.Select(ip => (ip, set)));
                return Task.CompletedTask;
            },
        };
        dns.Log += logs.Add;

        var answer = await dns.HandleAsync(Query("chatgpt.com", DnsMessage.TypeA), default);

        Assert.Equal([IPAddress.Parse("31.13.72.174")], DnsMessage.ReadARecords(answer));
        // Адрес от обычного DNS всё равно ведём в VPN — иначе ChatGPT увидел бы российский адрес
        Assert.Equal([(IPAddress.Parse("31.13.72.174"), RouteSet.Ai)], routed);
        Assert.False(dns.Unstable);

        // Много промахов подряд — «перебои», и в журнале не десятки одинаковых строк, а две
        for (var i = 0; i < 6; i++)
            await dns.HandleAsync(Query("chatgpt.com", DnsMessage.TypeA), default);
        Assert.True(dns.Unstable);
        Assert.Equal(2, logs.Count);
        Assert.Contains("перебоями", logs[1]);
    }

    [Fact]
    public async Task System_dns_is_switched_and_restored_exactly()
    {
        var backup = Path.Combine(Path.GetTempPath(), "OdnaKnopkaTests", Guid.NewGuid().ToString("N"), "dns.json");
        var commands = new List<string>();
        var system = new SystemDns(backup, "FiolKid", args =>
        {
            commands.Add(args);
            return Task.FromResult((0, ""));
        });
        SystemDns.AdapterBackup[] adapters =
        [
            new("{1}", "Ethernet", null, null, ["192.168.100.1", "fe80::1%14"]),
            new("{2}", "Wi-Fi", ["8.8.8.8", "8.8.4.4"], null, ["8.8.8.8", "8.8.4.4"]),
        ];

        var upstreams = await system.ActivateAsync(adapters);

        Assert.True(system.HasBackup);
        Assert.Contains("192.168.100.1:53", upstreams.Select(u => u.ToString()));
        Assert.Contains("interface ipv4 set dnsservers name=\"Ethernet\" source=static address=127.0.0.1 register=primary validate=no", commands);
        // Второго DNS провайдера нет: Windows не должна спрашивать его мимо нас
        Assert.DoesNotContain(commands, c => c.StartsWith("interface ipv4 add dnsservers"));
        Assert.Contains("interface ipv6 set dnsservers name=\"Ethernet\" source=static address=::1 register=primary validate=no", commands);

        commands.Clear();
        await system.RestoreAsync();

        Assert.False(system.HasBackup);
        Assert.Contains("interface ipv4 set dnsservers name=\"Ethernet\" source=dhcp", commands);       // было от роутера
        Assert.Contains("interface ipv6 set dnsservers name=\"Ethernet\" source=dhcp", commands);
        Assert.Contains("interface ipv4 set dnsservers name=\"Wi-Fi\" source=static address=8.8.8.8 register=primary validate=no", commands);
        Assert.Contains("interface ipv4 add dnsservers name=\"Wi-Fi\" address=8.8.4.4 index=2 validate=no", commands);
    }

    [Fact]
    public async Task Leftover_backup_is_restored_before_switching_again()
    {
        var backup = Path.Combine(Path.GetTempPath(), "OdnaKnopkaTests", Guid.NewGuid().ToString("N"), "dns.json");
        var commands = new List<string>();
        var system = new SystemDns(backup, "FiolKid", args =>
        {
            commands.Add(args);
            return Task.FromResult((0, ""));
        });
        await system.ActivateAsync([new("{1}", "Ethernet", null, null, ["192.168.100.1"])]);
        commands.Clear();

        // Программа «упала», не вернув DNS, — следующий запуск сначала возвращает
        await system.ActivateAsync([new("{1}", "Ethernet", null, null, ["192.168.100.1"])]);

        Assert.Equal("interface ipv4 set dnsservers name=\"Ethernet\" source=dhcp", commands[0]);
    }

    [Theory]
    [InlineData("192.168.100.1", true)]
    [InlineData("fe80::1%14", true)]
    [InlineData("fec0:0:0:ffff::1", false)]  // заглушка Windows, по ней никто не отвечает
    [InlineData("127.0.0.1", false)]
    public void Only_real_dns_servers_are_used(string address, bool real)
    {
        Assert.Equal(real, SystemDns.IsRealServer(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task Our_dns_answers_over_real_udp_socket()
    {
        using var normal = new FakeUpstream("87.250.250.242");
        using var secure = new FakeUpstream("1.2.3.4");
        // Свободный порт
        int port;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

        await using var dns = new SmartDns([normal.EndPoint], port, [secure.EndPoint]) { TunnelUp = () => true };
        dns.Start();

        using var client = new UdpClient();
        var query = Query("ya.ru", DnsMessage.TypeA, 0x4242);
        await client.SendAsync(query, new IPEndPoint(IPAddress.Loopback, port));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply = await client.ReceiveAsync(timeout.Token);

        Assert.Equal(0x4242, DnsMessage.Id(reply.Buffer));
        Assert.Equal([IPAddress.Parse("87.250.250.242")], DnsMessage.ReadARecords(reply.Buffer));
    }

    [Fact]
    public async Task A_silent_first_server_does_not_delay_the_second_server()
    {
        using var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var fast = new FakeUpstream("104.18.32.47");
        await using var dns = new SmartDns([], port: 0,
            secureUpstreams: [(IPEndPoint)silent.Client.LocalEndPoint!, fast.EndPoint])
        {
            Classify = _ => RouteSet.Ai,
            TunnelUp = () => true,
        };
        var answer = await dns.HandleAsync(Query("chatgpt.com", 1), default).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal([IPAddress.Parse("104.18.32.47")], DnsMessage.ReadARecords(answer));
    }

    [Fact]
    public async Task Tcp_is_used_when_udp_is_silent()
    {
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        try
        {
            var endpoint = (IPEndPoint)tcp.LocalEndpoint;
            using var silentUdp = new UdpClient(endpoint);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var server = Task.Run(async () =>
            {
                using var connection = await tcp.AcceptTcpClientAsync(timeout.Token);
                var stream = connection.GetStream();
                var size = new byte[2];
                await stream.ReadExactlyAsync(size, timeout.Token);
                var request = new byte[BinaryPrimitives.ReadUInt16BigEndian(size)];
                await stream.ReadExactlyAsync(request, timeout.Token);
                var answer = Answer(request, "104.18.32.47");
                BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)answer.Length);
                await stream.WriteAsync(size, timeout.Token);
                await stream.WriteAsync(answer, timeout.Token);
            }, timeout.Token);
            await using var dns = new SmartDns([], port: 0, secureUpstreams: [endpoint])
            {
                Classify = _ => RouteSet.Ai,
                TunnelUp = () => true,
            };
            var answer = await dns.HandleAsync(Query("chatgpt.com", 1), timeout.Token);
            Assert.Equal([IPAddress.Parse("104.18.32.47")], DnsMessage.ReadARecords(answer));
            await server;
        }
        finally
        {
            tcp.Stop();
        }
    }

    [Fact]
    public async Task Servfail_from_one_upstream_does_not_override_a_valid_answer()
    {
        using var failed = new FakeUpstream("1.2.3.4", q => DnsMessage.ServerFailure(q));
        using var working = new FakeUpstream("104.18.32.47");
        await using var dns = new SmartDns([], port: 0, secureUpstreams: [failed.EndPoint, working.EndPoint])
        {
            Classify = _ => RouteSet.Ai,
            TunnelUp = () => true,
        };
        var answer = await dns.HandleAsync(Query("chatgpt.com", 1), default);
        Assert.Equal([IPAddress.Parse("104.18.32.47")], DnsMessage.ReadARecords(answer));
        Assert.Equal(0, answer[3] & 15);
    }

    [Theory]
    [InlineData("other.com", 1)]
    [InlineData("chatgpt.com", 28)]
    public void Responses_for_another_question_are_rejected(string name, ushort type)
    {
        var query = Query("chatgpt.com", 1);
        Assert.False(DnsMessage.IsResponseTo(Answer(Query(name, type), "1.2.3.4"), query));
        Assert.False(DnsMessage.IsResponseTo(query, query));
    }

    [Fact]
    public async Task When_tunnel_dns_goes_silent_the_last_tunnel_answer_wins_over_the_provider()
    {
        // Случай Ивана: первый ответ через VPN — настоящий адрес ChatGPT, потом туннель теряет DNS,
        // а провайдер отдаёт «российский» адрес Cloudflare, через который ChatGPT видит RU
        using var provider = new FakeUpstream("8.47.69.5");
        using var secure = new FakeUpstream("104.18.32.47");
        var resolved = new List<IPAddress>();
        await using var dns = new SmartDns([provider.EndPoint], port: 0, secureUpstreams: [secure.EndPoint])
        {
            Classify = _ => RouteSet.Ai,
            TunnelUp = () => true,
            OnResolved = (a, _) => { resolved.AddRange(a); return Task.CompletedTask; },
        };

        await dns.HandleAsync(Query("chatgpt.com", 1), default);
        secure.Silent = true;
        var query = Query("chatgpt.com", 1);
        var answer = await dns.HandleAsync(query, default);

        Assert.Equal([IPAddress.Parse("104.18.32.47")], DnsMessage.ReadARecords(answer));
        Assert.Equal(DnsMessage.Id(query), DnsMessage.Id(answer));
        Assert.Empty(provider.Asked);
        Assert.Contains("прошлый ответ через VPN", dns.Recent[^1]);
        Assert.All(resolved, ip => Assert.Equal(IPAddress.Parse("104.18.32.47"), ip));
    }
}
