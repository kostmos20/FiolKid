using ZapretPro.Core.Health;
using ZapretPro.Core.Infrastructure;
using ZapretPro.Core.Zapret;

namespace ZapretPro.Core.Tests;

public sealed class WatchdogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "OdnaKnopkaTests", Guid.NewGuid().ToString("N"));
    private readonly FakeZapret _zapret = new("general", "ALT", "ALT2", "ALT3");
    private readonly FakeTelegram _telegram = new();
    private readonly FakeTime _time = new();
    private readonly FakeProbe _probe;
    private readonly SettingsStore _settings;
    private readonly Watchdog _watchdog;

    public WatchdogTests()
    {
        _probe = new FakeProbe(_zapret, _telegram);
        _settings = new SettingsStore(Path.Combine(_dir, "settings.json"));
        _watchdog = new Watchdog(_zapret, _telegram, _probe, _settings,
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
    public async Task Stop_stops_everything()
    {
        _zapret.Works("general");
        await _watchdog.StartAsync(default);

        _watchdog.Stop();

        Assert.False(_zapret.IsRunning);
        Assert.False(_telegram.IsRunning);
        Assert.Equal(ZapretState.Stopped, State);
        Assert.Null(_watchdog.Status.StrategyId);
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

    private sealed class FakeProbe(FakeZapret zapret, FakeTelegram telegram) : IHealthProbe
    {
        public bool Internet { get; set; } = true;

        /// <summary>Сколько следующих проверок провалить вне зависимости от стратегии.</summary>
        public int FailNext { get; set; }

        public Task<HealthReport> CheckAsync(IReadOnlyCollection<Service> services, CancellationToken ct)
        {
            var effect = zapret.Effect;
            var fail = FailNext > 0;
            if (fail)
                FailNext--;

            var results = services.ToDictionary(s => s, s => s switch
            {
                Service.YouTube => Internet && !fail && effect.YouTube,
                Service.Discord => Internet && !fail && effect.Discord,
                Service.Telegram => telegram.IsRunning,
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
