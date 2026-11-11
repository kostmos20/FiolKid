using System.Net;
using ZapretPro.Core.Games;
using ZapretPro.Core.Infrastructure;

namespace ZapretPro.Core.Tests;

public sealed class GameTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OdnaKnopkaTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static ProcessPaths.Entry P(int pid, string path) => new(pid, path);

    private static GameEntry Game(string path, string name) => new() { Path = path, Name = name };

    [Fact]
    public void Folder_game_covers_everything_inside_and_exe_game_only_itself()
    {
        var folder = Game(@"D:\Games\VRChat", "VRChat");
        var exe = Game(@"C:\Roblox\RobloxPlayerBeta.exe", "Roblox");

        Assert.True(folder.Matches(@"D:\Games\VRChat\VRChat.exe"));
        Assert.True(folder.Matches(@"d:\games\vrchat\tools\launcher.exe"));
        Assert.False(folder.Matches(@"D:\Games\VRChat2\VRChat.exe"));
        Assert.True(exe.Matches(@"C:\Roblox\RobloxPlayerBeta.exe"));
        Assert.False(exe.Matches(@"C:\Roblox\RobloxPlayerBeta.exe\x.exe"));
    }

    [Fact]
    public void Any_game_counts_but_only_listed_ones_go_through_vpn()
    {
        var vrchat = Game(@"D:\Games\VRChat", "VRChat");
        ProcessPaths.Entry[] processes =
        [
            P(1, @"C:\Windows\explorer.exe"),
            P(2, @"D:\SteamLibrary\steamapps\common\dota 2 beta\game\bin\win64\dota2.exe"),
        ];

        // Дота: игровой режим включается, но через VPN она не идёт
        var dota = GameMonitor.Evaluate(processes, [vrchat], () => false);
        Assert.Equal("dota2", dota.Name);
        Assert.Equal("игра (dota2)", dota.Busy);
        Assert.Empty(dota.VpnGames);

        // VRChat из списка: два процесса
        var both = GameMonitor.Evaluate([.. processes, P(3, @"D:\Games\VRChat\VRChat.exe"), P(4, @"D:\Games\VRChat\helper.exe")],
            [vrchat], () => false);
        Assert.Equal("VRChat", both.Name);
        var running = Assert.Single(both.VpnGames);
        Assert.Equal([3, 4], running.Pids.Order());

        // Ничего, кроме полноэкранного окна
        var full = GameMonitor.Evaluate([P(1, @"C:\Windows\explorer.exe")], [vrchat], () => true);
        Assert.True(full.Playing);
        Assert.True(full.FullScreenOnly);
        Assert.Equal("полноэкранное приложение", full.Busy);

        Assert.False(GameMonitor.Evaluate([P(1, @"C:\Windows\explorer.exe")], [vrchat], () => false).Playing);
    }

    [Fact]
    public void Monitor_reports_start_and_stop_once()
    {
        var processes = new List<ProcessPaths.Entry>();
        var monitor = new GameMonitor(() => [], () => processes, () => false);
        var changes = new List<(GameScan Before, GameScan After)>();
        monitor.Changed += (b, a) => changes.Add((b, a));

        monitor.Scan();
        processes.Add(P(7, @"C:\Riot Games\VALORANT\live\VALORANT.exe"));
        monitor.Scan();
        monitor.Scan();
        processes.Clear();
        monitor.Scan();

        Assert.Equal(2, changes.Count);
        Assert.Equal("VALORANT", changes[0].After.Name);
        Assert.False(changes[1].After.Playing);
    }

    private sealed class FakeRoutes : IGameRoutes
    {
        public bool TunnelRunning { get; set; } = true;
        public HashSet<IPAddress> Routed { get; } = [];
        public List<string> Domains { get; private set; } = [];
        public Dictionary<IPAddress, string> Names { get; } = [];

        public void SetGameTargets(IReadOnlyCollection<IPAddress> addresses, IReadOnlyList<string> domains)
        {
            Routed.Clear();
            Routed.UnionWith(addresses);
            Domains = domains.ToList();
        }

        public bool AddGameAddress(IPAddress address) => Routed.Add(address);
        public string? NameOf(IPAddress address) => Names.GetValueOrDefault(address);
    }

    private sealed class FakeTraffic : IGameTraffic
    {
        public bool Running { get; private set; }
        public event Action<int, IPAddress, bool>? Seen;
        public string? Start()
        {
            Running = true;
            return null;
        }

        public void Stop() => Running = false;
        public void Dispose() => Stop();
        public void Send(int pid, string ip, bool tcp = true) => Seen?.Invoke(pid, IPAddress.Parse(ip), tcp);
    }

    private (GameRouter Router, FakeRoutes Routes, FakeTraffic Traffic, SettingsStore Settings, List<(IPAddress Ip, int Pids)> Closed)
        Router(GameEntry game, Dictionary<int, string>? paths = null, List<IPAddress>? open = null)
    {
        var settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        settings.Update(s => s.Games = [game]);
        var routes = new FakeRoutes();
        var traffic = new FakeTraffic();
        var closed = new List<(IPAddress, int)>();
        var router = new GameRouter(settings, routes, traffic, null,
            (pids, ip) =>
            {
                closed.Add((ip, pids.Count));
                return 1;
            },
            _ => open ?? [],
            pid => paths?.GetValueOrDefault(pid));
        return (router, routes, traffic, settings, closed);
    }

    private static GameScan Running(GameEntry game, params int[] pids) =>
        new(game.Name, false, [new RunningGame(game, pids.ToHashSet(), [game.Path + @"\game.exe"])]);

    [Fact]
    public void New_address_of_the_game_goes_to_vpn_and_old_direct_connection_is_closed()
    {
        var game = Game(@"D:\Games\VRChat", "VRChat");
        var (router, routes, traffic, settings, closed) = Router(game);

        router.Update(Running(game, 10));
        Assert.True(traffic.Running);

        routes.Names[IPAddress.Parse("3.3.3.3")] = "api.vrchat.cloud";
        traffic.Send(10, "3.3.3.3");
        traffic.Send(10, "3.3.3.3"); // второй раз — ничего нового
        traffic.Send(99, "4.4.4.4"); // чужой процесс

        Assert.Contains(IPAddress.Parse("3.3.3.3"), routes.Routed);
        Assert.DoesNotContain(IPAddress.Parse("4.4.4.4"), routes.Routed);
        Assert.Equal([(IPAddress.Parse("3.3.3.3"), 1)], closed);

        // Игра закрылась — снимаем маршруты, слежку и запоминаем узнанное
        router.Update(GameScan.None);
        Assert.False(traffic.Running);
        Assert.Empty(routes.Routed);
        var saved = Assert.Single(settings.Load().Games!);
        Assert.Equal(["3.3.3.3"], saved.LearnedAddresses);
        Assert.Equal(["api.vrchat.cloud"], saved.LearnedDomains);
    }

    [Fact]
    public void Learned_addresses_and_domains_go_to_vpn_from_the_first_packet()
    {
        var game = Game(@"D:\Games\VRChat", "VRChat");
        game.LearnedAddresses = ["5.5.5.5"];
        game.LearnedDomains = ["files.vrchat.cloud"];
        var (router, routes, _, _, closed) = Router(game);

        router.Update(Running(game, 10));

        Assert.Contains(IPAddress.Parse("5.5.5.5"), routes.Routed);
        Assert.Equal(["files.vrchat.cloud"], routes.Domains);
        Assert.Empty(closed);
    }

    [Fact]
    public void Helper_process_started_by_the_game_is_recognised_before_the_monitor_sees_it()
    {
        var game = Game(@"D:\Games\VRChat", "VRChat");
        var (router, routes, traffic, _, _) = Router(game, new Dictionary<int, string> { [11] = @"D:\Games\VRChat\helper.exe" });
        router.Update(Running(game, 10));

        traffic.Send(11, "6.6.6.6", tcp: false);

        Assert.Contains(IPAddress.Parse("6.6.6.6"), routes.Routed);
    }

    [Fact]
    public void Connections_opened_before_the_game_was_noticed_are_moved_to_vpn()
    {
        var game = Game(@"D:\Games\VRChat", "VRChat");
        var (router, routes, _, _, closed) = Router(game, open: [IPAddress.Parse("7.7.7.7")]);

        router.Update(Running(game, 10));

        Assert.Contains(IPAddress.Parse("7.7.7.7"), routes.Routed);
        Assert.Single(closed);
    }
}
