using ZapretPro.Core.Health;
using ZapretPro.Core.Infrastructure;
using ZapretPro.Core.Vpn;
using ZapretPro.Core.Zapret;

namespace ZapretPro.Core.Tests;

public sealed class WatchdogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OdnaKnopkaTests", Guid.NewGuid().ToString("N"));
    private readonly FakeZapret _zapret = new("general", "ALT", "ALT2", "ALT3");
    private readonly FakeTelegram _telegram = new();
    private readonly FakeVpn _vpn = new();
    private readonly FakeTime _time = new();
    private readonly FakeProbe _probe;
    private readonly SettingsStore _settings;
    private readonly Watchdog _watchdog;

    public WatchdogTests()
    {
        _probe = new FakeProbe(_zapret, _telegram, _vpn);
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        _watchdog = new Watchdog(_zapret, _telegram, _vpn, _probe, _settings,
            new WatchdogOptions { WarmUp = TimeSpan.Zero }, _time);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private ZapretState State => _watchdog.Status.State;

    [Fact]
    public async Task Startup_without_saved_strategy_picks_first_working_and_remembers_it()
    {
        _zapret.Works("ALT2");

        await _watchdog.StartAsync(default);

        Assert.Equal(ZapretState.Working, State);
        Assert.Equal("ALT2", _zapret.Current);
        Assert.Equal(["general", "ALT", "ALT2"], _zapret.Started);
        Assert.Equal("ALT2", _settings.Load().LastStrategyId);
    }

    [Fact]
    public async Task Startup_uses_saved_strategy_first()
    {
        _zapret.Works("ALT", "ALT3");
        _settings.Update(s => s.LastStrategyId = "ALT3");

        await _watchdog.StartAsync(default);

        Assert.Equal(["ALT3"], _zapret.Started);
        Assert.Equal(ZapretState.Working, State);
    }

    [Fact]
    public async Task Saved_strategy_that_stopped_working_is_replaced_searching_around_the_circle()
    {
        _zapret.Works("ALT");
        _settings.Update(s => s.LastStrategyId = "ALT2");

        await _watchdog.StartAsync(default);

        Assert.Equal(["ALT2", "ALT3", "general", "ALT"], _zapret.Started);
        Assert.Equal("ALT", _settings.Load().LastStrategyId);
    }

    [Fact]
    public async Task Startup_starts_telegram_proxy()
    {
        _zapret.Works("general");

        await _watchdog.StartAsync(default);

        Assert.True(_telegram.IsRunning);
        Assert.True(_watchdog.Status.TelegramRunning);
    }

    [Fact]
    public async Task Single_failed_check_does_not_switch_but_two_in_a_row_do()
    {
        _zapret.Works("general", "ALT2");
        await _watchdog.StartAsync(default);
        _zapret.Started.Clear();

        _zapret.Breaks("general");
        await _watchdog.CheckAsync(default);
        Assert.Empty(_zapret.Started);
        Assert.Equal("general", _zapret.Current);

        await _watchdog.CheckAsync(default);
        Assert.Equal(["ALT", "ALT2"], _zapret.Started);
        Assert.Equal(ZapretState.Working, State);
        Assert.Equal("ALT2", _settings.Load().LastStrategyId);
    }

    [Fact]
    public async Task Successful_check_resets_failure_counter()
    {
        _zapret.Works("general", "ALT");
        await _watchdog.StartAsync(default);
        _zapret.Started.Clear();

        _probe.FailNext = 1;
        await _watchdog.CheckAsync(default);
        await _watchdog.CheckAsync(default);
        _probe.FailNext = 1;
        await _watchdog.CheckAsync(default);

        Assert.Empty(_zapret.Started);
        Assert.Equal(ZapretState.Working, State);
    }

    [Fact]
    public async Task When_nothing_helps_zapret_stops_and_retries_only_after_an_hour()
    {
        await _watchdog.StartAsync(default);

        Assert.Equal(ZapretState.Failed, State);
        Assert.False(_zapret.IsRunning);
        Assert.Equal(4, _zapret.Started.Count);
        Assert.Null(_settings.Load().LastStrategyId);

        _zapret.Started.Clear();
        _zapret.Works("ALT3");
        _time.Advance(TimeSpan.FromMinutes(59));
        await _watchdog.CheckAsync(default);
        Assert.Empty(_zapret.Started);
        Assert.Equal(ZapretState.Failed, State);

        _time.Advance(TimeSpan.FromMinutes(1));
        await _watchdog.CheckAsync(default);
        Assert.Equal(ZapretState.Working, State);
        Assert.Equal("ALT3", _zapret.Current);
    }

    [Fact]
    public async Task Partial_success_keeps_best_strategy_without_flapping()
    {
        _zapret.Works("ALT2", discord: false);
        _zapret.Works("ALT3", youtube: false, discord: false);

        await _watchdog.StartAsync(default);

        Assert.Equal(ZapretState.Partial, State);
        Assert.Equal("ALT2", _zapret.Current);

        _zapret.Started.Clear();
        for (var i = 0; i < 5; i++)
            await _watchdog.CheckAsync(default);
        Assert.Empty(_zapret.Started);
        Assert.Equal(ZapretState.Partial, State);
    }

    [Fact]
    public async Task Partial_mode_searches_again_when_even_the_working_part_breaks()
    {
        _zapret.Works("ALT2", discord: false);
        _zapret.Works("general", youtube: false, discord: false);
        await _watchdog.StartAsync(default);
        _zapret.Works("ALT3");
        _zapret.Breaks("ALT2");

        await _watchdog.CheckAsync(default);
        await _watchdog.CheckAsync(default);

        Assert.Equal(ZapretState.Working, State);
        Assert.Equal("ALT3", _zapret.Current);
    }

    [Fact]
    public async Task Partial_mode_retries_full_search_after_an_hour()
    {
        _zapret.Works("ALT2", discord: false);
        await _watchdog.StartAsync(default);
        _zapret.Works("general");

        _time.Advance(TimeSpan.FromHours(1));
        await _watchdog.CheckAsync(default);

        Assert.Equal(ZapretState.Working, State);
        Assert.Equal("general", _zapret.Current);
    }

    [Fact]
    public async Task No_internet_stops_search_and_keeps_strategy()
    {
        _probe.Internet = false;

        await _watchdog.StartAsync(default);

        Assert.Equal(ZapretState.NoInternet, State);
        Assert.Equal(["general"], _zapret.Started);

        _zapret.Started.Clear();
        await _watchdog.CheckAsync(default);
        await _watchdog.CheckAsync(default);
        Assert.Empty(_zapret.Started);
    }

    [Fact]
    public async Task After_internet_returns_working_strategy_is_kept()
    {
        _probe.Internet = false;
        _zapret.Works("general");
        await _watchdog.StartAsync(default);

        _probe.Internet = true;
        await _watchdog.CheckAsync(default);

        Assert.Equal(ZapretState.Working, State);
        Assert.Equal("general", _settings.Load().LastStrategyId);
    }

    [Fact]
    public async Task After_internet_returns_broken_strategy_is_replaced()
    {
        _probe.Internet = false;
        _zapret.Works("ALT");
        await _watchdog.StartAsync(default);

        _probe.Internet = true;
        await _watchdog.CheckAsync(default);
        await _watchdog.CheckAsync(default);

        Assert.Equal(ZapretState.Working, State);
        Assert.Equal("ALT", _zapret.Current);
    }

    [Fact]
    public async Task Crashed_winws_is_restarted_with_same_strategy()
    {
        _zapret.Works("ALT");
        await _watchdog.StartAsync(default);
        _zapret.Started.Clear();

        _zapret.Crash();
        await _watchdog.CheckAsync(default);

        Assert.Equal(["ALT"], _zapret.Started);
        Assert.True(_zapret.IsRunning);
        Assert.Equal(ZapretState.Working, State);
    }

    [Fact]
    public async Task Crashed_telegram_proxy_is_restarted_on_check()
    {
        _zapret.Works("general");
        await _watchdog.StartAsync(default);

        _telegram.Crash();
        await _watchdog.CheckAsync(default);

        Assert.True(_telegram.IsRunning);
        Assert.Equal(2, _telegram.Starts);
    }

    [Fact]
    public async Task Strategies_that_fail_to_start_are_skipped()
    {
        _zapret.FailToStart("general", "ALT");
        _zapret.Works("ALT2");

        await _watchdog.StartAsync(default);

        Assert.Equal("ALT2", _zapret.Current);
        Assert.Null(_watchdog.Status.LastError);
    }

    [Fact]
    public async Task Zapret_that_never_starts_is_reported()
    {
        _zapret.FailToStart("general", "ALT", "ALT2", "ALT3");

        await _watchdog.StartAsync(default);

        Assert.Equal(ZapretState.Failed, State);
        Assert.Contains("не запускается", _watchdog.Status.LastError);
    }

    [Fact]
    public async Task Broken_driver_stops_search_at_once_and_says_reboot()
    {
        _zapret.BreakDriver = true;

        await _watchdog.StartAsync(default);

        Assert.Single(_zapret.Started);                 // а не все стратегии подряд
        Assert.Equal(ZapretState.Failed, State);
        Assert.Contains("Перезагрузи компьютер", _watchdog.Status.LastError);
        Assert.Equal(new HashSet<RouteSet> { RouteSet.YouTube, RouteSet.Discord }, _vpn.Routes); // запасной канал всё равно включён
    }

    [Fact]
    public async Task Stop_stops_everything()
    {
        _zapret.Works("general");
        await _watchdog.StartAsync(default);

        await _watchdog.StopAsync(default);

        Assert.False(_zapret.IsRunning);
        Assert.False(_telegram.IsRunning);
        Assert.True(_vpn.Stopped);
        Assert.Equal(ZapretState.Stopped, State);
        Assert.Null(_watchdog.Status.StrategyId);
    }

    [Fact]
    public async Task Working_zapret_keeps_only_ai_in_vpn()
    {
        _zapret.Works("general");

        await _watchdog.StartAsync(default);

        Assert.Empty(_vpn.Routes);
        Assert.Equal([Service.Ai], _watchdog.Status.ViaVpn);
        Assert.True(_watchdog.Status.LastReport![Service.Ai]);
    }

    [Fact]
    public async Task When_zapret_fails_youtube_and_discord_go_through_vpn()
    {
        await _watchdog.StartAsync(default);

        Assert.Equal(ZapretState.Failed, State);
        Assert.Equal(new HashSet<RouteSet> { RouteSet.YouTube, RouteSet.Discord }, _vpn.Routes);
        Assert.Contains(Service.YouTube, _watchdog.Status.ViaVpn);
        Assert.True(_watchdog.Status.LastReport![Service.YouTube]);
        Assert.True(_watchdog.Status.LastReport[Service.Discord]);
    }

    [Fact]
    public async Task Partial_mode_sends_only_the_missing_service_through_vpn()
    {
        _zapret.Works("ALT", discord: false);

        await _watchdog.StartAsync(default);

        Assert.Equal(ZapretState.Partial, State);
        Assert.Equal(new HashSet<RouteSet> { RouteSet.Discord }, _vpn.Routes);
    }

    [Fact]
    public async Task Hourly_retry_does_not_mistake_vpn_for_a_working_strategy()
    {
        await _watchdog.StartAsync(default);
        _vpn.Applied.Clear();

        _time.Advance(TimeSpan.FromHours(1));
        await _watchdog.CheckAsync(default);

        // Перед перебором запасной канал для YouTube и Discord сняли, поэтому ни одна стратегия не «сработала»
        Assert.Empty(_vpn.Applied[0]);
        Assert.Equal(ZapretState.Failed, State);
        Assert.Null(_settings.Load().LastStrategyId);
        Assert.Equal(new HashSet<RouteSet> { RouteSet.YouTube, RouteSet.Discord }, _vpn.Routes);
    }

    [Fact]
    public async Task Telegram_goes_through_vpn_after_two_failures_and_retries_after_an_hour()
    {
        _zapret.Works("general");
        await _watchdog.StartAsync(default);

        _probe.TelegramUpstream = false;
        await _watchdog.CheckAsync(default);
        Assert.DoesNotContain(RouteSet.Telegram, _vpn.Routes);

        await _watchdog.CheckAsync(default);
        Assert.Contains(RouteSet.Telegram, _vpn.Routes);
        Assert.Contains(Service.Telegram, _watchdog.Status.ViaVpn);

        // Через VPN Telegram работает — пока не прошёл час, запасной канал не снимаем
        await _watchdog.CheckAsync(default);
        Assert.Contains(RouteSet.Telegram, _vpn.Routes);

        _probe.TelegramUpstream = true;
        _time.Advance(TimeSpan.FromHours(1));
        await _watchdog.CheckAsync(default);
        Assert.DoesNotContain(RouteSet.Telegram, _vpn.Routes);

        await _watchdog.CheckAsync(default);
        Assert.DoesNotContain(RouteSet.Telegram, _vpn.Routes);
    }

    [Fact]
    public async Task Without_key_ai_is_not_checked()
    {
        _vpn.HasKey = false;
        _zapret.Works("general");

        await _watchdog.StartAsync(default);

        Assert.All(_probe.Requested, r => Assert.DoesNotContain(Service.Ai, r));
        Assert.Equal(VpnState.NoKey, _watchdog.Status.Vpn.State);
    }

    private sealed class FakeZapret : IZapretRunner
    {
        private readonly Dictionary<string, (bool YouTube, bool Discord)> _works = new();
        private readonly HashSet<string> _failToStart = [];

        public FakeZapret(params string[] ids)
        {
            Strategies = ids.Select(id => new Strategy(id, "--fake")).ToList();
        }

        public IReadOnlyList<Strategy> Strategies { get; }
        public List<string> Started { get; } = [];
        public string? Current { get; private set; }
        public bool IsRunning { get; private set; }
        public string? LastError { get; private set; }

        public void Works(string id, bool youtube = true, bool discord = true) => _works[id] = (youtube, discord);
        public void Works(params string[] ids) => Array.ForEach(ids, id => Works(id));
        public void Breaks(string id) => _works.Remove(id);
        public void FailToStart(params string[] ids) => _failToStart.UnionWith(ids);
        public void Crash() => IsRunning = false;

        public (bool YouTube, bool Discord) Effect =>
            IsRunning && Current is not null && _works.TryGetValue(Current, out var e) ? e : (false, false);

        public Task<bool> StartAsync(Strategy strategy, CancellationToken ct)
        {
            Started.Add(strategy.Id);
            DriverBroken = BreakDriver;
            if (BreakDriver)
            {
                IsRunning = false;
                LastError = "Не открывается драйвер zapret (WinDivert). Перезагрузи компьютер";
                return Task.FromResult(false);
            }
            if (_failToStart.Contains(strategy.Id))
            {
                IsRunning = false;
                LastError = $"winws упал на {strategy.Id}";
                return Task.FromResult(false);
            }
            Current = strategy.Id;
            IsRunning = true;
            LastError = null;
            return Task.FromResult(true);
        }

        public void Stop()
        {
            IsRunning = false;
            Current = null;
        }

        public bool DriverBroken { get; private set; }
        public bool BreakDriver { get; set; }
    }

    private sealed class FakeTelegram : ITelegramProxy
    {
        public bool IsRunning { get; private set; }
        public string? LastError => null;
        public int Starts { get; private set; }

        public Task<bool> StartAsync(CancellationToken ct)
        {
            Starts++;
            IsRunning = true;
            return Task.FromResult(true);
        }

        public void Stop() => IsRunning = false;
        public void Crash() => IsRunning = false;
    }

    private sealed class FakeVpn : IVpn
    {
        public bool HasKey { get; set; } = true;
        public VpnStatus Status { get; private set; } = VpnStatus.Initial;
        public IReadOnlySet<RouteSet> Routes { get; private set; } = new HashSet<RouteSet>();
        public List<IReadOnlySet<RouteSet>> Applied { get; } = [];
        public bool Stopped { get; private set; }

        public bool Carries(RouteSet set) => HasKey && !Stopped && Routes.Contains(set);

        public Task<VpnStatus> ApplyAsync(IReadOnlySet<RouteSet> fallback, CancellationToken ct)
        {
            Stopped = false;
            Routes = new HashSet<RouteSet>(fallback);
            Applied.Add(Routes);
            Status = new VpnStatus(HasKey ? VpnState.Connected : VpnState.NoKey, 1, fallback, null);
            return Task.FromResult(Status);
        }

        public Task StopAsync(CancellationToken ct)
        {
            Stopped = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Ведёт себя как настоящая проверка: сервис открывается, если его пробивает текущая стратегия
    /// или если он идёт через VPN.
    /// </summary>
    private sealed class FakeProbe(FakeZapret zapret, FakeTelegram telegram, FakeVpn vpn) : IHealthProbe
    {
        public bool Internet { get; set; } = true;

        /// <summary>Доходит ли прокси Telegram до серверов Telegram напрямую.</summary>
        public bool TelegramUpstream { get; set; } = true;

        /// <summary>Сколько следующих проверок провалить вне зависимости от стратегии.</summary>
        public int FailNext { get; set; }

        public List<IReadOnlyCollection<Service>> Requested { get; } = [];

        public Task<HealthReport> CheckAsync(IReadOnlyCollection<Service> services, CancellationToken ct)
        {
            Requested.Add(services);
            var effect = zapret.Effect;
            var fail = FailNext > 0;
            if (fail)
                FailNext--;

            var results = services.ToDictionary(s => s, s => Internet && s switch
            {
                Service.YouTube => !fail && effect.YouTube || vpn.Carries(RouteSet.YouTube),
                Service.Discord => !fail && effect.Discord || vpn.Carries(RouteSet.Discord),
                Service.Telegram => telegram.IsRunning && (TelegramUpstream || vpn.Carries(RouteSet.Telegram)),
                Service.Ai => vpn.HasKey && !vpn.Stopped,
                _ => false,
            });
            return Task.FromResult(new HealthReport(results, Internet));
        }
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
