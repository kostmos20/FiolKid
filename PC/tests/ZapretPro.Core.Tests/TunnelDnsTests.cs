using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using ZapretPro.Core.Dns;

namespace ZapretPro.Core.Tests;

/// <summary>1.0.0: наш DNS только на адаптере VPN, DNS провайдера мимо него закрыт WFP.</summary>
public sealed class TunnelDnsTests
{
    private sealed class FakeWindows : IAdapterDns, IDnsRestoreTask
    {
        public readonly Dictionary<(string Family, string Id), string[]?> Registry = [];
        public readonly Dictionary<string, string> Names = [];
        public readonly List<string> Commands = [];
        public int TaskInstalls { get; private set; }

        public void Add(string id, string name, string[]? v4 = null)
        {
            Names[id] = name;
            Registry[("ipv4", id)] = v4;
            Registry[("ipv6", id)] = null;
        }

        public Task<(int, string)> Netsh(string args)
        {
            Commands.Add(args);
            var name = args.Split('"')[1];
            var id = Names.FirstOrDefault(n => n.Value == name).Key;
            if (id is null)
                return Task.FromResult((1, "Элемент не найден."));
            if (!args.Contains(" dnsservers "))
                return Task.FromResult((0, ""));
            var family = args.Split(' ')[1];
            var address = args.Split(' ').FirstOrDefault(a => a.StartsWith("address="))?["address=".Length..];
            if (args.Contains("source=dhcp"))
                Registry[(family, id)] = null;
            else if (args.Contains(" set dnsservers"))
                Registry[(family, id)] = [address!];
            else
                Registry[(family, id)] = [.. Registry[(family, id)] ?? [], address!];
            return Task.FromResult((0, ""));
        }

        public bool Exists(string family, string id) => Registry.ContainsKey((family, id));
        public string[]? Read(string family, string id) => Registry.GetValueOrDefault((family, id));
        public void Write(string family, string id, string[]? servers) => Registry[(family, id)] = servers is { Length: > 0 } ? servers : null;
        public string? Name(string id) => Names.GetValueOrDefault(id);
        public IReadOnlyList<(string Id, string Name)> All() => Names.Select(n => (n.Key, n.Value)).ToList();
        public string? IdOf(string name) => Names.FirstOrDefault(n => n.Value == name).Key;

        public Task InstallAsync(string backupPath)
        {
            TaskInstalls++;
            return Task.CompletedTask;
        }

        public Task RemoveAsync() => Task.CompletedTask;
    }

    private sealed class FakeGuard : IDnsLeakGuard
    {
        public string? Error { get; set; }
        public bool Enabled { get; private set; }
        public IReadOnlyList<string> Apps { get; private set; } = [];

        public string? Enable(IReadOnlyList<string> allowedApps)
        {
            Apps = allowedApps;
            if (Error is not null)
                return Error;
            Enabled = true;
            return null;
        }

        public void Disable() => Enabled = false;
    }

    private static string NewBackupPath() =>
        Path.Combine(Path.GetTempPath(), "OdnaKnopkaTests", Guid.NewGuid().ToString("N"), "dns.json");

    private static int FreePort()
    {
        for (var attempt = 0; ; attempt++)
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            try
            {
                // Тот же порт нужен и для TCP
                var tcp = new TcpListener(IPAddress.Loopback, port);
                tcp.Start();
                tcp.Stop();
                return port;
            }
            catch (SocketException) when (attempt < 20)
            {
            }
        }
    }

    private const string App = @"C:\FiolKid\FiolKid.exe";

    private static (DnsHijack Hijack, SystemDns System, FakeWindows Windows, FakeGuard Guard) Hijack(string? checkError = null)
    {
        var windows = new FakeWindows();
        windows.Add("{2}", "Wi-Fi", ["8.8.8.8"]);
        windows.Add("{4}", "FiolKid");
        var system = new SystemDns(NewBackupPath(), "FiolKid", windows.Netsh, windows, windows,
            () => [new("{2}", "Wi-Fi", ["8.8.8.8"], null, ["8.8.8.8"])]);
        var guard = new FakeGuard();
        var hijack = new DnsHijack(system, guard, () => [App], _ => Task.FromResult(checkError), FreePort());
        return (hijack, system, windows, guard);
    }

    private static Task<bool> Start(DnsHijack hijack) => hijack.StartAsync(_ => null, (_, _) => Task.CompletedTask, () => true);

    [Fact]
    public async Task Dns_is_set_only_on_vpn_adapter()
    {
        var (hijack, system, windows, guard) = Hijack();

        Assert.True(await Start(hijack));

        Assert.True(hijack.Active);
        Assert.True(hijack.TunnelMode);
        Assert.True(guard.Enabled);
        Assert.Equal([App], guard.Apps);
        // Wi-Fi не тронут, файла с прежними DNS и задачи Планировщика нет — возвращать нечего
        Assert.Equal(["8.8.8.8"], windows.Read("ipv4", "{2}"));
        Assert.False(system.HasBackup);
        Assert.Equal(0, windows.TaskInstalls);
        Assert.DoesNotContain(windows.Commands, c => c.Contains("\"Wi-Fi\""));
        Assert.Equal(["127.0.0.1"], windows.Read("ipv4", "{4}"));
        Assert.Equal(["::1"], windows.Read("ipv6", "{4}"));
        Assert.Contains("interface ipv4 set interface interface=\"FiolKid\" metric=1", windows.Commands);
        Assert.True(system.HasTunnelMark);

        await hijack.StopAsync();

        Assert.False(guard.Enabled);
        Assert.Null(windows.Read("ipv4", "{4}"));
        Assert.Null(windows.Read("ipv6", "{4}"));
        Assert.False(system.HasTunnelMark);
        Assert.Equal(["8.8.8.8"], windows.Read("ipv4", "{2}"));
    }

    [Fact]
    public async Task Without_wfp_adapters_are_switched_as_before()
    {
        var (hijack, system, windows, guard) = Hijack();
        guard.Error = "служба BFE не запущена";
        var logs = new List<string>();
        hijack.Log += logs.Add;

        Assert.True(await Start(hijack));

        Assert.False(hijack.TunnelMode);
        Assert.True(hijack.Active);
        Assert.Contains(logs, l => l.Contains("BFE"));
        // Старый способ со всеми страховками
        Assert.Equal(["127.0.0.1"], windows.Read("ipv4", "{2}"));
        Assert.True(system.HasBackup);
        Assert.Equal(1, windows.TaskInstalls);
        // С адаптера VPN наш DNS убран, отметки нет
        Assert.Null(windows.Read("ipv4", "{4}"));
        Assert.False(system.HasTunnelMark);

        await hijack.StopAsync();
        Assert.Equal(["8.8.8.8"], windows.Read("ipv4", "{2}"));
        Assert.False(system.HasBackup);
    }

    [Fact]
    public async Task Slow_windows_dns_falls_back_and_removes_filters()
    {
        var (hijack, _, windows, guard) = Hijack("Windows спрашивает наш DNS с задержкой 1500 мс");

        Assert.True(await Start(hijack));

        Assert.False(hijack.TunnelMode);
        Assert.False(guard.Enabled);
        Assert.Null(windows.Read("ipv4", "{4}"));
        Assert.Equal(["127.0.0.1"], windows.Read("ipv4", "{2}"));
        await hijack.StopAsync();
        Assert.Equal(["8.8.8.8"], windows.Read("ipv4", "{2}"));
    }

    [Fact]
    public async Task Without_vpn_adapter_adapters_are_switched_as_before()
    {
        var (hijack, _, windows, guard) = Hijack();
        windows.Names.Remove("{4}");

        Assert.True(await Start(hijack));

        Assert.False(hijack.TunnelMode);
        Assert.False(guard.Enabled);
        Assert.Equal(["127.0.0.1"], windows.Read("ipv4", "{2}"));
        await hijack.StopAsync();
    }

    [Fact]
    public async Task Vanished_vpn_adapter_lifts_the_block_at_once()
    {
        var (hijack, _, windows, guard) = Hijack();
        Assert.True(await Start(hijack));

        // Туннель упал: адаптера больше нет — без снятия блокировки DNS не работал бы совсем
        windows.Names.Remove("{4}");
        hijack.CheckTunnel();

        Assert.False(guard.Enabled);
        Assert.False(hijack.Active);

        // VPN вернулся — включаемся заново, тем же основным способом
        windows.Names["{4}"] = "FiolKid";
        Assert.True(await Start(hijack));
        Assert.True(guard.Enabled);
        Assert.True(hijack.TunnelMode);
        Assert.True(hijack.Active);
        await hijack.StopAsync();
    }

    [Fact]
    public async Task Reconnect_lifts_the_block_before_tunnel_stops()
    {
        var (hijack, _, _, guard) = Hijack();
        Assert.True(await Start(hijack));

        hijack.TunnelStopping();

        Assert.False(guard.Enabled);
        Assert.False(hijack.Active);
        await hijack.StopAsync();
    }

    [Fact]
    public async Task Leftover_vpn_adapter_dns_is_cleared_even_without_the_adapter()
    {
        var (hijack, system, windows, _) = Hijack();
        Assert.True(await Start(hijack));
        // Программа «умерла»: фильтры WFP исчезли сами, адаптер VPN тоже, а Windows помнит его DNS
        windows.Names.Remove("{4}");
        var fresh = new SystemDns(Path.Combine(Path.GetDirectoryName(system.TunnelMarkPath)!, "dns.json"), "FiolKid",
            windows.Netsh, windows, windows);

        Assert.True(await fresh.ClearTunnelAsync());

        Assert.Null(windows.Read("ipv4", "{4}"));
        Assert.False(fresh.HasTunnelMark);
        await hijack.StopAsync();
    }

    [Fact]
    public async Task Network_change_keeps_working()
    {
        var windows = new FakeWindows();
        windows.Add("{4}", "FiolKid");
        var current = "192.168.0.1";
        var system = new SystemDns(NewBackupPath(), "FiolKid", windows.Netsh, windows, windows,
            () => [new("{2}", "Wi-Fi", null, null, [current])]);
        var hijack = new DnsHijack(system, new FakeGuard(), () => [App], _ => Task.FromResult<string?>(null), FreePort());
        var logs = new List<string>();
        hijack.Log += logs.Add;
        Assert.True(await Start(hijack));

        current = "10.0.0.1";
        await hijack.RetryRestoreAsync();

        Assert.True(hijack.Active);
        Assert.Contains(logs, l => l.Contains("10.0.0.1:53"));
        await hijack.StopAsync();
    }

    private static byte[] Query(string name, ushort id = 0x1234)
    {
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        bytes.AddRange([0, 1, 0, 1]); // тип A, класс IN
        return bytes.ToArray();
    }

    [Fact]
    public async Task Health_subdomains_are_answered_locally_and_remembered()
    {
        var nowhere = new IPEndPoint(IPAddress.Parse("192.0.2.1"), 53);
        await using var dns = new SmartDns([nowhere], 0, [nowhere]);
        var name = "c123." + SmartDns.HealthName;

        var answer = await dns.HandleAsync(Query(name), default).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0x1234, DnsMessage.Id(answer));
        Assert.True(dns.SawHealthCheck(name));
        Assert.False(dns.SawHealthCheck("c999." + SmartDns.HealthName));
    }

    [Fact]
    public void Wfp_structures_match_windows_headers()
    {
        // fwpmtypes.h, x64: ошибка в раскладке — и Windows прочитала бы мусор вместо фильтра
        Assert.Equal(72, Marshal.SizeOf<WfpDnsGuard.FWPM_SESSION0>());
        Assert.Equal(72, Marshal.SizeOf<WfpDnsGuard.FWPM_SUBLAYER0>());
        Assert.Equal(64, (int)Marshal.OffsetOf<WfpDnsGuard.FWPM_SUBLAYER0>("weight"));
        Assert.Equal(40, Marshal.SizeOf<WfpDnsGuard.FWPM_FILTER_CONDITION0>());
        Assert.Equal(24, (int)Marshal.OffsetOf<WfpDnsGuard.FWPM_FILTER_CONDITION0>("conditionValue"));
        Assert.Equal(200, Marshal.SizeOf<WfpDnsGuard.FWPM_FILTER0>());
        Assert.Equal(64, (int)Marshal.OffsetOf<WfpDnsGuard.FWPM_FILTER0>("layerKey"));
        Assert.Equal(96, (int)Marshal.OffsetOf<WfpDnsGuard.FWPM_FILTER0>("weight"));
        Assert.Equal(120, (int)Marshal.OffsetOf<WfpDnsGuard.FWPM_FILTER0>("filterCondition"));
        Assert.Equal(128, (int)Marshal.OffsetOf<WfpDnsGuard.FWPM_FILTER0>("action"));
        Assert.Equal(152, (int)Marshal.OffsetOf<WfpDnsGuard.FWPM_FILTER0>("rawContext"));
        Assert.Equal(184, (int)Marshal.OffsetOf<WfpDnsGuard.FWPM_FILTER0>("effectiveWeight"));
        Assert.Equal(6, WfpDnsGuard.FilterKeys.Distinct().Count());
    }
}
