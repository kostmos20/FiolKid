using ZapretPro.Core.Infrastructure;
using ZapretPro.Core.Vpn;
using ZapretPro.Core.Zapret;

namespace ZapretPro.Core.Health;

public enum ZapretState
{
    Stopped,
    /// <summary>Перебираем стратегии.</summary>
    Searching,
    /// <summary>YouTube и Discord открываются через zapret.</summary>
    Working,
    /// <summary>Лучшая из стратегий открывает только часть сервисов, остальные — через VPN.</summary>
    Partial,
    /// <summary>Ни одна стратегия не помогла: YouTube и Discord идут через VPN.</summary>
    Failed,
    /// <summary>Не открывается даже российский сайт — ждём интернет, стратегию не трогаем.</summary>
    NoInternet,
}

public sealed record WatchdogStatus(
    ZapretState State,
    string? StrategyId,
    int StrategyNumber,
    int StrategyCount,
    HealthReport? LastReport,
    bool TelegramRunning,
    IReadOnlySet<Service> ViaVpn,
    VpnStatus Vpn,
    string? LastError)
{
    /// <summary>Драйвер zapret завис — поможет только перезагрузка. Об этом надо сказать человеку прямо.</summary>
    public bool NeedsReboot { get; init; }

    /// <summary>Идёт замер скорости всех альтов (режим «самый быстрый»).</summary>
    public bool Measuring { get; init; }
}

public sealed record WatchdogOptions
{
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMinutes(2.5);
    /// <summary>Пауза после смены стратегии, прежде чем проверять.</summary>
    public TimeSpan WarmUp { get; init; } = TimeSpan.FromSeconds(1.5);
    public int FailuresBeforeSwitch { get; init; } = 2;
    /// <summary>
    /// Не чаще этого — новый перебор стратегий. При массовых блокировках иначе программа металась бы
    /// без конца: перезапуски zapret и замеры скорости выкидывают из игр и забивают канал.
    /// </summary>
    public TimeSpan MinSwitchInterval { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Как часто пробовать вернуться с запасного канала (чтобы не гонять видео через сервер знакомого).</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromHours(1);
    /// <summary>Через сколько замер скорости альтов считается устаревшим.</summary>
    public TimeSpan BenchmarkLifetime { get; init; } = TimeSpan.FromDays(7);
    /// <summary>Прогрев перед быстрым отсевом альтов: winws уже сказал, что готов, нужна лишь пауза.</summary>
    public TimeSpan ScreenWarmUp { get; init; } = TimeSpan.FromSeconds(0.3);
    /// <summary>Сколько лучших альтов меряем точно во втором проходе.</summary>
    public int Finalists { get; init; } = 3;
    /// <summary>«Быстрый» при отсеве: все четыре адреса вместе ответили не дольше этого.</summary>
    public double FastScreenMilliseconds { get; init; } = 2000;
    /// <summary>Паузы между попытками поднять неработающий VPN: каждая следующая длиннее, последняя повторяется.</summary>
    public IReadOnlyList<TimeSpan> VpnRetryDelays { get; init; } =
        [TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];
}

/// <summary>
/// Следит за тремя независимыми частями: zapret (YouTube и Discord), прокси Telegram и VPN. У каждой свой
/// цикл проверок, свой замок и своё состояние — зависание или поломка одной не держит остальные:
/// перебор альтов не ждёт подключения VPN, а мёртвый VPN не мешает zapret и Telegram.
/// Друг другу части только сообщают: zapret — «YouTube не тяну, возьми в туннель», Telegram — «возьми меня
/// в туннель»; VPN применяет это, когда может, никого не дожидаясь (docs/PLAN.md, раздел «Zapret»).
/// </summary>
public sealed class Watchdog
{
    private static readonly Service[] ZapretServices = [Service.YouTube, Service.Discord];

    private readonly IZapretRunner _zapret;
    private readonly ITelegramProxy _telegram;
    private readonly IVpn _vpn;
    private readonly IHealthProbe _probe;
    private readonly IZapretSpeedProbe? _speed;
    private readonly IActivityMonitor? _activity;
    private readonly SettingsStore _settings;
    private readonly WatchdogOptions _options;
    private readonly TimeProvider _time;

    // Свой замок у каждой части: перебор альтов (минута и дольше) не держит ни VPN, ни Telegram
    private readonly SemaphoreSlim _zapretGate = new(1, 1);
    private readonly SemaphoreSlim _telegramGate = new(1, 1);
    private readonly SemaphoreSlim _vpnGate = new(1, 1);
    // Общее для частей: запасные наборы, отчёты, состояние VPN
    private readonly object _sync = new();

    private volatile bool _running;
    private CancellationTokenSource _life = new();

    // ---- zapret ----
    private ZapretState _state = ZapretState.Stopped;
    private int _current = -1;
    private int _failures;
    private DateTimeOffset _lastSearch;
    private string? _lastError;
    private bool _needsReboot;
    private bool _measuring;
    /// <summary>Перебор нужен, но отложен (рано или компьютер занят): сломавшееся пока через запасной канал.</summary>
    private bool _deferred;
    private string? _lastDeferReason;
    /// <summary>Какие сервисы zapret сейчас отдал VPN.</summary>
    private readonly HashSet<Service> _zapretFallback = [];
    /// <summary>Открывается ли контрольный сайт напрямую — по последней проверке zapret.</summary>
    private bool _internetOk = true;

    // ---- Telegram ----
    private DateTimeOffset? _telegramFallbackSince;
    private int _telegramFailures;
    private string? _telegramNote;

    // ---- VPN ----
    private VpnStatus _vpnStatus = VpnStatus.Initial;
    private int _vpnFailures;
    private DateTimeOffset _vpnRetryAt = DateTimeOffset.MinValue;
    private HashSet<RouteSet>? _appliedSets;
    // Просьбы частей поменять наборы VPN разбирает одна фоновая задача: просьба не ждёт подключения
    private bool _vpnDirty;
    private bool _vpnDraining;
    private Task _vpnDrain = Task.CompletedTask;

    // ---- отчёты частей: из них складывается общий для окна ----
    private HealthReport? _zapretReport;
    private HealthReport? _telegramReport;
    private HealthReport? _vpnReport;
    private readonly Dictionary<string, string> _loggedProblems = new();

    public Watchdog(IZapretRunner zapret, ITelegramProxy telegram, IVpn vpn, IHealthProbe probe, SettingsStore settings,
        WatchdogOptions? options = null, TimeProvider? time = null, IZapretSpeedProbe? speed = null,
        IActivityMonitor? activity = null)
    {
        _activity = activity;
        _speed = speed;
        _zapret = zapret;
        _telegram = telegram;
        _vpn = vpn;
        _probe = probe;
        _settings = settings;
        _options = options ?? new WatchdogOptions();
        _time = time ?? TimeProvider.System;
    }

    public event Action<WatchdogStatus>? StatusChanged;

    /// <summary>Технические сообщения для отчёта и тестового окна.</summary>
    public event Action<string>? Log;

    public WatchdogStatus Status
    {
        get
        {
            lock (_sync)
            {
                return new WatchdogStatus(
                    _state,
                    _current >= 0 ? _zapret.Strategies[_current].Id : null,
                    _current + 1,
                    SafeCount(),
                    MergedReport(),
                    _telegram.IsRunning,
                    ViaVpn(),
                    _vpnStatus,
                    _lastError) { NeedsReboot = _needsReboot, Measuring = _measuring };
            }
        }
    }

    // ===================================================================== запуск и остановка

    /// <summary>
    /// Запуск и три независимых цикла проверок до отмены: zapret, Telegram, VPN. Упавший или зависший цикл
    /// одной части не останавливает и не тормозит остальные.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        Begin(ct);
        await Task.WhenAll(
            LoopAsync("zapret", StartZapretAsync, c => CheckZapretAsync(c, manual: false), ct),
            LoopAsync("Telegram", StartTelegramAsync, CheckTelegramAsync, ct),
            VpnLoopAsync(ct));
    }

    /// <summary>
    /// Запуск всех частей сразу, параллельно: прокси Telegram, VPN и последняя рабочая стратегия.
    /// Возвращается, когда каждая часть сделала первый шаг (для тестов и ручного вызова).
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        Begin(ct);
        await Task.WhenAll(Guard("Telegram", StartTelegramAsync, ct), Guard("VPN", RefreshVpnAsync, ct), Guard("zapret", StartZapretAsync, ct));
        await VpnSettledAsync();
    }

    /// <summary>
    /// Одна проверка всех частей сразу. Можно вызвать и вне расписания: <paramref name="manual"/> — человек нажал
    /// «Проверить сейчас», ему перебор разрешён без пауз и даже во время игры.
    /// </summary>
    public async Task CheckAsync(CancellationToken ct, bool manual = false)
    {
        if (!_running)
            return;
        await Task.WhenAll(Guard("Telegram", CheckTelegramAsync, ct), Guard("VPN", RefreshVpnAsync, ct),
            Guard("zapret", c => CheckZapretAsync(c, manual), ct));
        await VpnSettledAsync();
    }

    /// <summary>
    /// Останавливает winws, прокси Telegram и VPN. Сначала отмени токен <see cref="RunAsync"/> и дождись его,
    /// иначе идущий перебор успеет запустить всё заново.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        _running = false;
        _life.Cancel();
        await VpnSettledAsync();

        // Драйвер WinDivert не выгружаем: принудительная остановка могла оставить его «застрявшим»,
        // и до перезагрузки zapret не запускался (FWP_E_IN_USE). Он выгрузится сам при перезагрузке.
        _zapret.Stop();
        _telegram.Stop();
        await _vpn.StopAsync(ct);
        lock (_sync)
        {
            _vpnStatus = _vpn.Status;
            _current = -1;
            _zapretFallback.Clear();
            _telegramFallbackSince = null;
            _telegramFailures = 0;
            _telegramNote = null;
            _deferred = false;
            _lastDeferReason = null;
            _appliedSets = null;
            _vpnFailures = 0;
            _vpnRetryAt = DateTimeOffset.MinValue;
            _vpnDirty = false;
            _zapretReport = _telegramReport = _vpnReport = null;
            _internetOk = true;
        }
        SetState(ZapretState.Stopped);
    }

    private void Begin(CancellationToken ct)
    {
        _life.Dispose();
        _life = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = true;
    }

    private async Task LoopAsync(string part, Func<CancellationToken, Task> start, Func<CancellationToken, Task> check, CancellationToken ct)
    {
        await Guard(part, start, ct);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_options.CheckInterval, _time, ct);
            await Guard(part, check, ct);
        }
    }

    private async Task VpnLoopAsync(CancellationToken ct)
    {
        await Guard("VPN", RefreshVpnAsync, ct);
        while (!ct.IsCancellationRequested)
        {
            // VPN не работает — следующая попытка по нарастающей паузе, а не через обычные 2,5 минуты
            var wait = _options.CheckInterval;
            DateTimeOffset retryAt;
            lock (_sync)
                retryAt = _vpnRetryAt;
            var untilRetry = retryAt - _time.GetUtcNow();
            if (untilRetry > TimeSpan.Zero && untilRetry < wait)
                wait = untilRetry;
            await Task.Delay(wait, _time, ct);
            await Guard("VPN", RefreshVpnAsync, ct);
        }
    }

    /// <summary>Ошибка одной части пишется в журнал и не роняет остальные.</summary>
    private async Task Guard(string part, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        try
        {
            await action(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Write($"{part}: ошибка — {e.Message}");
        }
    }

    // ===================================================================== zapret

    private async Task StartZapretAsync(CancellationToken ct)
    {
        await _zapretGate.WaitAsync(ct);
        try
        {
            await StartStrategyAsync(ct);
        }
        finally
        {
            _zapretGate.Release();
        }
    }

    /// <summary>Сменили режим выбора альта в настройках — применить сразу.</summary>
    public async Task ApplyStrategySettingsAsync(CancellationToken ct)
    {
        await _zapretGate.WaitAsync(ct);
        try
        {
            if (_state != ZapretState.Stopped)
                await StartStrategyAsync(ct, "настройки изменены");
        }
        finally
        {
            _zapretGate.Release();
        }
    }

    /// <summary>Кнопка «Замерить сейчас»: все альты по скорости, затем самый быстрый.</summary>
    public async Task BenchmarkAsync(CancellationToken ct)
    {
        await _zapretGate.WaitAsync(ct);
        try
        {
            if (_state != ZapretState.Stopped && _speed is not null)
                await BenchmarkCoreAsync(ct);
        }
        finally
        {
            _zapretGate.Release();
        }
    }

    /// <summary>С какого альта начать — по режиму из настроек.</summary>
    private async Task StartStrategyAsync(CancellationToken ct, string? reason = null)
    {
        var settings = _settings.Load();
        if (ManualIndex(settings) is { } manual)
        {
            Write($"Альт выбран вручную: «{settings.ManualStrategyId}»" + (reason is null ? "" : $" ({reason})"));
            await SearchAsync(manual, ct, only: true);
            return;
        }
        if (settings.EffectiveStrategyMode == StrategyModes.Fastest && _speed is not null && BenchmarkStale(settings))
        {
            // Замер качает тестовые файлы и перезапускает zapret раз двадцать — не во время игры
            if (_activity?.BusyReason() is { } busy)
                Write($"Замер скорости альтов отложен: {busy}");
            else
            {
                await BenchmarkCoreAsync(ct);
                return;
            }
        }

        var saved = settings.LastStrategyId;
        var start = Math.Max(0, IndexOf(saved));
        Write(saved is null ? "Сохранённой стратегии нет, начинаю с первой" : $"Начинаю с сохранённой стратегии «{saved}»");
        await SearchAsync(start, ct);
    }

    private int? ManualIndex(AppSettings settings) =>
        settings.StrategyMode == StrategyModes.Manual && IndexOf(settings.ManualStrategyId) is >= 0 and var index ? index : null;

    /// <summary>Замера нет, он старый или альты обновились — нужен новый.</summary>
    private bool BenchmarkStale(AppSettings settings)
    {
        if (settings.StrategyBenchmarkAt is not { } at || settings.StrategyBenchmark is not { Count: > 0 } results)
            return true;
        if (_time.GetUtcNow() - at >= _options.BenchmarkLifetime)
            return true;
        return !_zapret.Strategies.Select(s => s.Id).ToHashSet().SetEquals(results.Select(r => r.Id));
    }

    /// <summary>
    /// Подбор самого быстрого альта в два прохода. Первый — быстрый отсев: каждый альт пару секунд,
    /// «открываются ли YouTube и Discord вообще»; начинаем с тех, что были хороши в прошлый раз, и
    /// останавливаемся, когда нашлось несколько быстрых и полностью рабочих. Второй — точный замер скорости
    /// загрузки только у лучших. Самый быстрый становится текущим; если с ним что-то не открылось —
    /// обычный перебор с него.
    /// </summary>
    private async Task BenchmarkCoreAsync(CancellationToken ct)
    {
        var strategies = _zapret.Strategies;
        _failures = 0;
        _lastSearch = _time.GetUtcNow();
        _deferred = false;
        _measuring = true;
        SetState(ZapretState.Searching);
        StrategyScore? best;
        try
        {
            // Замер идёт напрямую, мимо туннеля, — запасной канал ему не мешает; снимаем его, потому что
            // сейчас выберем альт заново
            ClearZapretFallback();

            Write($"Подбираю самый быстрый альт ({strategies.Count} шт.)");
            var started = _time.GetUtcNow();
            var screened = new Dictionary<string, StrategyScore>();
            var driverBroken = false;
            foreach (var index in ScreeningOrder())
            {
                var id = strategies[index].Id;
                var score = await TryStrategyAsync(index, _options.ScreenWarmUp, _speed!.QuickCheckZapretAsync, ct);
                if (score is null)
                {
                    if (_zapret.DriverBroken)
                    {
                        driverBroken = true;
                        break;
                    }
                    screened[id] = new StrategyScore(id, 0, 0, double.MaxValue);
                    continue;
                }
                screened[id] = score;
                // Нашлось несколько быстрых и полностью рабочих — остальные вряд ли окажутся заметно лучше
                if (screened.Values.Count(s => s.AllOk && s.Milliseconds <= _options.FastScreenMilliseconds) >= _options.Finalists)
                {
                    Write("Нашлось достаточно быстрых альтов — остальные не проверяю");
                    break;
                }
            }

            // Второй проход: точная скорость только у лучших по отсеву
            var maxOk = screened.Values.Select(s => s.Ok).DefaultIfEmpty(0).Max();
            var finalists = screened.Values.Where(s => s.Ok > 0 && s.Ok == maxOk)
                .OrderBy(s => s.Milliseconds).Take(_options.Finalists).ToList();
            var measured = new List<StrategyScore>();
            if (finalists.Count > 1 && !driverBroken)
            {
                foreach (var finalist in finalists)
                {
                    var score = await TryStrategyAsync(IndexOf(finalist.Id), _options.WarmUp, _speed!.MeasureZapretAsync, ct);
                    if (score is not null)
                        measured.Add(score);
                }
            }
            // Быстрый отсев и точный замер меряют разное (заголовки и полную загрузку) — не смешиваем:
            // был точный замер — решает он, иначе лучший по отсеву
            best = measured.Where(s => s.Ok > 0).OrderByDescending(s => s.Ok).ThenBy(s => s.Milliseconds).FirstOrDefault()
                   ?? finalists.FirstOrDefault();
            Write($"Подбор занял {(_time.GetUtcNow() - started).TotalSeconds:0} с");

            // Неполный замер (завис драйвер) и пустой (не было интернета) не сохраняем: они не показывают, какой альт быстрее
            if (!driverBroken && best is not null)
            {
                var final = measured.ToDictionary(s => s.Id);
                _settings.Update(s =>
                {
                    s.StrategyBenchmarkAt = _time.GetUtcNow();
                    // −1 — не запустился, −2 — не проверяли (подбор остановился раньше)
                    s.StrategyBenchmark = strategies.Select(x =>
                        final.TryGetValue(x.Id, out var m) ? Entry(m)
                        : screened.TryGetValue(x.Id, out var q) ? Entry(q)
                        : new StrategyBenchmarkEntry(x.Id, 0, 0, -2)).ToList();
                });
            }
        }
        finally
        {
            _measuring = false;
        }

        if (best is null)
        {
            Write("Замер ничего не дал — перебираю альты по порядку");
            await SearchAsync(0, ct);
            return;
        }
        Write($"Самый быстрый альт: «{best.Id}» ({best.Milliseconds / 1000:0.0} с)");
        _settings.Update(s => s.LastStrategyId = best.Id);
        await SearchAsync(IndexOf(best.Id), ct);
    }

    private static StrategyBenchmarkEntry Entry(StrategyScore s) =>
        new(s.Id, s.Ok, s.Total, s.Milliseconds == double.MaxValue ? -1 : Math.Round(s.Milliseconds));

    /// <summary>Запустить альт, дать ему прогреться и проверить. Null — альт не запустился.</summary>
    private async Task<StrategyScore?> TryStrategyAsync(int index, TimeSpan warmUp,
        Func<CancellationToken, Task<(int Ok, int Total, double Milliseconds)>> check, CancellationToken ct)
    {
        _current = index;
        Notify();
        var id = _zapret.Strategies[index].Id;
        if (!await _zapret.StartAsync(_zapret.Strategies[index], ct))
        {
            Write($"Альт «{id}» не запустился: {_zapret.LastError}");
            return null;
        }
        await Task.Delay(warmUp, _time, ct);
        var (ok, total, ms) = await check(ct);
        Write($"Альт «{id}»: открылось {ok} из {total}, {ms / 1000:0.0} с");
        return new StrategyScore(id, ok, total, ms);
    }

    /// <summary>
    /// В каком порядке проверять альты: сначала лучшие по прошлому замеру и текущий, потом остальные
    /// по списку. Так быстрые находятся сразу, и подбор чаще заканчивается раньше.
    /// </summary>
    private IEnumerable<int> ScreeningOrder()
    {
        var settings = _settings.Load();
        var previous = (settings.StrategyBenchmark ?? [])
            .Where(b => b.Ok > 0 && b.Milliseconds >= 0)
            .OrderByDescending(b => b.Ok).ThenBy(b => b.Milliseconds)
            .Select(b => b.Id);
        var first = new[] { settings.LastStrategyId }.OfType<string>().Concat(previous)
            .Select(IndexOf).Where(i => i >= 0);
        return first.Concat(Enumerable.Range(0, _zapret.Strategies.Count)).Distinct().ToList();
    }

    /// <summary>Текущий альт перестал помогать: что делать — зависит от режима.</summary>
    private async Task RecoverStrategyAsync(CancellationToken ct)
    {
        var settings = _settings.Load();
        if (ManualIndex(settings) is { } manual)
            await SearchAsync(manual, ct, only: true);
        else if (settings.EffectiveStrategyMode == StrategyModes.Fastest && _speed is not null)
            await BenchmarkCoreAsync(ct);
        else
            await SearchAsync(_current + 1, ct);
    }

    /// <summary>
    /// Почему сейчас нельзя устраивать перебор, или null. Нельзя чаще <see cref="WatchdogOptions.MinSwitchInterval"/>
    /// и пока компьютер занят (игра, полноэкранное приложение, большая загрузка).
    /// </summary>
    private string? SwitchBlocked()
    {
        var since = _time.GetUtcNow() - _lastSearch;
        if (since < _options.MinSwitchInterval)
            return $"с прошлого перебора прошло {since.TotalMinutes:0} мин, нужно не меньше {_options.MinSwitchInterval.TotalMinutes:0}";
        return _activity?.BusyReason();
    }

    /// <summary>
    /// Перебор отложен: сломавшиеся сервисы — через запасной канал, winws не трогаем. В журнал пишем, только
    /// когда что-то меняется, — чтобы не засорять его каждые 2,5 минуты.
    /// </summary>
    private void Defer(string reason, IEnumerable<Service> toVpn)
    {
        _deferred = true;
        _failures = 0;
        List<Service> added;
        lock (_sync)
            added = toVpn.Where(_zapretFallback.Add).ToList();
        if (added.Count > 0 || reason != _lastDeferReason)
            Write($"Альт не меняю: {reason}" +
                  (added.Count > 0 ? $". Пока через запасной канал: {string.Join(", ", added)}" : ""));
        _lastDeferReason = reason;
        SetState(_zapret.IsRunning ? ZapretState.Partial : ZapretState.Failed);
        if (added.Count > 0)
            RequestVpn();
    }

    /// <summary>Плановая проверка zapret. VPN и Telegram она не касается и их не ждёт.</summary>
    private async Task CheckZapretAsync(CancellationToken ct, bool manual)
    {
        await _zapretGate.WaitAsync(ct);
        try
        {
            if (_state == ZapretState.Stopped)
                return;

            // Отложенный перебор — как только станет можно; обычный запасной режим — раз в час
            var retryAfter = _deferred ? _options.MinSwitchInterval : _options.RetryInterval;
            if (_state is ZapretState.Failed or ZapretState.Partial
                && (manual && _deferred || _time.GetUtcNow() - _lastSearch >= retryAfter))
            {
                var blocked = manual ? null : _activity?.BusyReason();
                if (blocked is null)
                {
                    var wasDeferred = _deferred;
                    _deferred = false;
                    _lastDeferReason = null;
                    Write(wasDeferred ? "Подбираю альт — перебор был отложен" : "Прошёл час — пробую снова обойтись без запасного канала");
                    if (ManualIndex(_settings.Load()) is { } manualIndex)
                        await SearchAsync(manualIndex, ct, only: true);
                    else if (wasDeferred)
                        await RecoverStrategyAsync(ct);
                    else
                        await SearchAsync(_state == ZapretState.Failed ? 0 : _current + 1, ct);
                    return;
                }
                if (blocked != _lastDeferReason)
                    Write($"Перебор откладываю: {blocked}");
                _lastDeferReason = blocked;
            }

            if (_state != ZapretState.Failed && !_zapret.IsRunning && _current >= 0)
            {
                Write("winws не работает — перезапускаю");
                if (!await _zapret.StartAsync(_zapret.Strategies[_current], ct))
                {
                    _lastError = _zapret.LastError;
                    if (!manual && SwitchBlocked() is { } reason)
                    {
                        Defer(reason, ZapretServices);
                        return;
                    }
                    await RecoverStrategyAsync(ct);
                    return;
                }
            }

            var report = await ProbeZapretAsync(ct);
            if (!report.InternetOk)
            {
                if (_state != ZapretState.Failed)
                    SetState(ZapretState.NoInternet);
                _failures = 0;
                return;
            }

            if (_state == ZapretState.Failed)
                return;

            // Сервисы, отданные VPN, проверка zapret не касается: они открываются через туннель
            var active = ActiveZapretServices();
            if (active.All(s => report[s]))
            {
                _failures = 0;
                var state = active.Count == ZapretServices.Length ? ZapretState.Working : ZapretState.Partial;
                if (state == ZapretState.Working && _state != ZapretState.Working)
                    Remember(_current);
                SetState(state);
                return;
            }

            _failures++;
            Write($"Проверка не прошла ({_failures} из {_options.FailuresBeforeSwitch})");
            if (_failures < _options.FailuresBeforeSwitch)
            {
                Notify();
                return;
            }
            if (!manual && SwitchBlocked() is { } blockedReason)
            {
                // Не перебираем, а отдаём сломавшееся VPN: winws и остальное работают как работали
                Defer(blockedReason, active.Where(s => !report[s]));
                return;
            }
            await RecoverStrategyAsync(ct);
        }
        finally
        {
            _zapretGate.Release();
        }
    }

    /// <summary>
    /// Перебирает все стратегии по кругу, начиная с <paramref name="start"/>.
    /// Первая, при которой открылось всё, становится рабочей. Если такой нет — берём лучшую из
    /// частично рабочих, а остальное отдаём VPN; если не помогла ни одна — YouTube и Discord идут через VPN.
    /// Проверки идут напрямую, мимо туннеля, поэтому перебор не ждёт VPN и не путает его с рабочим альтом.
    /// </summary>
    /// <param name="only">Проверить только альт <paramref name="start"/> (выбран вручную): не помог — недостающее через VPN.</param>
    private async Task SearchAsync(int start, CancellationToken ct, bool only = false)
    {
        var strategies = _zapret.Strategies;
        _failures = 0;
        _lastSearch = _time.GetUtcNow();
        _deferred = false;
        SetState(ZapretState.Searching);
        ClearZapretFallback();

        var best = (Ok: 0, Index: -1, Missing: (IReadOnlyList<Service>)ZapretServices);
        var startErrors = 0;
        var driverBroken = false;

        var count = only ? 1 : strategies.Count;
        for (var i = 0; i < count; i++)
        {
            var index = (start + i) % strategies.Count;
            _current = index;
            Notify();

            if (!await _zapret.StartAsync(strategies[index], ct))
            {
                startErrors++;
                _lastError = _zapret.LastError;
                Write($"Стратегия «{strategies[index].Id}» не запустилась: {_zapret.LastError}");
                // Не открылся драйвер — остальные стратегии упадут так же, перебирать бессмысленно
                if (_zapret.DriverBroken)
                {
                    driverBroken = true;
                    break;
                }
                continue;
            }

            await Task.Delay(_options.WarmUp, _time, ct);
            var report = await ProbeZapretAsync(ct);

            if (!report.InternetOk)
            {
                // Нет интернета — перебирать бессмысленно, оставляем эту стратегию и ждём
                Write("Нет интернета, перебор остановлен");
                SetState(ZapretState.NoInternet);
                return;
            }

            var missing = ZapretServices.Where(s => !report[s]).ToList();
            var ok = ZapretServices.Length - missing.Count;
            Write($"Стратегия «{strategies[index].Id}»: открылось {ok} из {ZapretServices.Length}");
            if (missing.Count == 0)
            {
                _lastError = null;
                _needsReboot = false;
                Remember(index);
                SetState(ZapretState.Working);
                return;
            }

            if (ok > best.Ok)
                best = (ok, index, missing);
        }

        _needsReboot = driverBroken;
        if (driverBroken)
            Write("Нужна перезагрузка компьютера: драйвер zapret завис");

        // Лучший и так запущен последним (например, единственный выбранный вручную) — не перезапускаем
        if (best.Index >= 0 && (best.Index == _current && _zapret.IsRunning || await _zapret.StartAsync(strategies[best.Index], ct)))
        {
            _current = best.Index;
            lock (_sync)
                _zapretFallback.UnionWith(best.Missing);
            Write($"Полностью не помогла ни одна стратегия, оставляю «{strategies[best.Index].Id}», " +
                  $"через запасной канал: {string.Join(", ", best.Missing)}");
            SetState(ZapretState.Partial);
        }
        else
        {
            _zapret.Stop();
            _current = -1;
            lock (_sync)
                _zapretFallback.UnionWith(ZapretServices);
            if (driverBroken)
                _lastError = _zapret.LastError;
            else if (startErrors == count)
                _lastError = $"zapret не запускается ни с одной стратегией. Последняя ошибка: {_zapret.LastError}";
            Write("Ни одна стратегия не помогла — YouTube и Discord через запасной канал");
            SetState(ZapretState.Failed);
        }
        RequestVpn();
    }

    private void ClearZapretFallback()
    {
        bool changed;
        lock (_sync)
        {
            changed = _zapretFallback.Count > 0;
            _zapretFallback.Clear();
        }
        if (changed)
            RequestVpn();
    }

    private List<Service> ActiveZapretServices()
    {
        lock (_sync)
            return ZapretServices.Except(_zapretFallback).ToList();
    }

    /// <summary>Проверка zapret: контрольный сайт и сервисы, которые он сейчас тянет, — напрямую.</summary>
    private async Task<HealthReport> ProbeZapretAsync(CancellationToken ct)
    {
        var services = ActiveZapretServices();
        var report = await _probe.CheckAsync(services, services.ToHashSet(), ct);
        lock (_sync)
        {
            _zapretReport = report;
            _internetOk = report.InternetOk;
        }
        LogProblems("zapret", report);
        Notify();
        return report;
    }

    // ===================================================================== Telegram

    private async Task StartTelegramAsync(CancellationToken ct)
    {
        await _telegramGate.WaitAsync(ct);
        try
        {
            await EnsureTelegramAsync(ct);
            await ProbeTelegramAsync(ct);
        }
        finally
        {
            _telegramGate.Release();
        }
    }

    private async Task CheckTelegramAsync(CancellationToken ct)
    {
        await _telegramGate.WaitAsync(ct);
        try
        {
            if (!_running)
                return;
            await EnsureTelegramAsync(ct);
            var report = await ProbeTelegramAsync(ct);
            UpdateTelegramFallback(report);
        }
        finally
        {
            _telegramGate.Release();
        }
    }

    /// <summary>Кнопка «Перезапустить прокси»: остановить и запустить заново, затем проверить. Перебор альтов не ждёт.</summary>
    public async Task RestartTelegramAsync(CancellationToken ct)
    {
        await _telegramGate.WaitAsync(ct);
        try
        {
            if (!_running)
                return;
            Write("Перезапускаю прокси Telegram по просьбе");
            _telegram.Stop();
            bool hadFallback;
            lock (_sync)
            {
                hadFallback = _telegramFallbackSince is not null;
                _telegramFallbackSince = null;
                _telegramFailures = 0;
            }
            if (hadFallback)
                RequestVpn();
            await EnsureTelegramAsync(ct);
            await ProbeTelegramAsync(ct);
        }
        finally
        {
            _telegramGate.Release();
        }
    }

    private async Task<HealthReport> ProbeTelegramAsync(CancellationToken ct)
    {
        var report = await _probe.CheckAsync([Service.Telegram], new HashSet<Service>(), ct);
        lock (_sync)
            _telegramReport = report;
        LogProblems("Telegram", report);
        Notify();
        return report;
    }

    /// <summary>
    /// Telegram через VPN, если прокси работает, но до серверов Telegram не достучаться два раза подряд — и только
    /// пока VPN жив: в мёртвый туннель Telegram ушёл бы «в никуда». Через час пробуем снова без VPN.
    /// </summary>
    private void UpdateTelegramFallback(HealthReport report)
    {
        if (!_telegram.IsRunning)
            return;

        bool changed;
        string? message;
        lock (_sync)
            (changed, message) = UpdateTelegramFallbackLocked(report);
        if (message is not null)
            Write(message);
        if (changed)
            RequestVpn();
    }

    private (bool Changed, string? Message) UpdateTelegramFallbackLocked(HealthReport report)
    {
        if (_telegramFallbackSince is { } since)
        {
            if (_time.GetUtcNow() - since < _options.RetryInterval)
                return (false, null);
            _telegramFallbackSince = null;
            _telegramFailures = 0;
            return (true, "Прошёл час — пробую Telegram без запасного канала");
        }

        // Нет интернета вообще — это не поломка прокси
        if (report[Service.Telegram] || !_internetOk)
        {
            _telegramFailures = 0;
            _telegramNote = null;
            return (false, null);
        }

        if (++_telegramFailures < _options.FailuresBeforeSwitch)
            return (false, null);
        if (!VpnAlive(_vpnStatus))
        {
            const string note = "Telegram не работает через прокси, а запасной канал (VPN) сейчас не работает — жду VPN";
            var first = _telegramNote != note;
            _telegramNote = note;
            return (false, first ? note : null);
        }
        _telegramNote = null;
        _telegramFallbackSince = _time.GetUtcNow();
        return (true, "Telegram не работает через прокси — включаю запасной канал");
    }

    private async Task EnsureTelegramAsync(CancellationToken ct)
    {
        if (_telegram.IsRunning)
            return;

        Write("Запускаю прокси Telegram");
        if (!await _telegram.StartAsync(ct))
        {
            _lastError = _telegram.LastError;
            Write($"Прокси Telegram не запустился: {_telegram.LastError}");
        }
        Notify();
    }

    // ===================================================================== VPN

    private static bool VpnAlive(VpnStatus status) => status.State is VpnState.Connected or VpnState.Connecting;

    private static bool VpnBroken(VpnStatus status) => status.State is VpnState.Failed or VpnState.ServerNotResponding or VpnState.BadKey;

    private HashSet<RouteSet> DesiredSets()
    {
        lock (_sync)
        {
            var sets = new HashSet<RouteSet>();
            if (_zapretFallback.Contains(Service.YouTube))
                sets.Add(RouteSet.YouTube);
            if (_zapretFallback.Contains(Service.Discord))
                sets.Add(RouteSet.Discord);
            if (_telegramFallbackSince is not null)
                sets.Add(RouteSet.Telegram);
            return sets;
        }
    }

    /// <summary>
    /// Плановая проверка VPN: поднять (если лежит — по нарастающей паузе), привести наборы к нужным,
    /// проверить нейросети и то, что сейчас идёт через туннель.
    /// </summary>
    private async Task RefreshVpnAsync(CancellationToken ct)
    {
        await _vpnGate.WaitAsync(ct);
        try
        {
            if (!_running)
                return;
            DateTimeOffset retryAt;
            lock (_sync)
            {
                retryAt = _vpnRetryAt;
                _vpnDirty = false;
            }
            if (_time.GetUtcNow() < retryAt)
                return; // ещё рано переподключать мёртвый VPN
            await ApplyVpnAsync(DesiredSets(), ct);
            await ProbeVpnAsync(ct);
        }
        finally
        {
            _vpnGate.Release();
        }
    }

    /// <summary>
    /// Просьба части поменять наборы VPN: «возьми YouTube», «отдай Telegram». Не ждёт: применяет фоновая
    /// задача, когда VPN свободен. Несколько просьб подряд сливаются в одно применение.
    /// </summary>
    private void RequestVpn()
    {
        if (!_running)
            return;
        lock (_sync)
        {
            _vpnDirty = true;
            if (_vpnDraining)
                return;
            _vpnDraining = true;
            var ct = _life.Token;
            _vpnDrain = Task.Run(() => DrainVpnAsync(ct));
        }
    }

    private async Task DrainVpnAsync(CancellationToken ct)
    {
        try
        {
            await _vpnGate.WaitAsync(ct);
            try
            {
                while (true)
                {
                    HashSet<RouteSet> sets;
                    lock (_sync)
                    {
                        if (!_vpnDirty || !_running)
                        {
                            _vpnDraining = false;
                            return;
                        }
                        _vpnDirty = false;
                        sets = DesiredSets();
                        // Туннель не поднялся — менять в нём нечего; нужные наборы применит следующая попытка
                        if (_vpnStatus.State == VpnState.Failed && _time.GetUtcNow() < _vpnRetryAt
                            || _appliedSets is not null && _appliedSets.SetEquals(sets) && !VpnBroken(_vpnStatus))
                            continue;
                    }
                    await ApplyVpnAsync(sets, ct);
                    await ProbeVpnAsync(ct);
                }
            }
            finally
            {
                _vpnGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Write($"VPN: ошибка — {e.Message}");
        }
        finally
        {
            lock (_sync)
                _vpnDraining = false;
        }
    }

    /// <summary>Дождаться, пока фоновая задача применит все просьбы (для тестов, кнопок и остановки).</summary>
    private async Task VpnSettledAsync()
    {
        Task drain;
        lock (_sync)
            drain = _vpnDrain;
        try
        {
            await drain;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ApplyVpnAsync(HashSet<RouteSet> sets, CancellationToken ct)
    {
        VpnStatus status;
        try
        {
            status = await _vpn.ApplyAsync(sets, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                  or System.ComponentModel.Win32Exception)
        {
            status = new VpnStatus(VpnState.Failed, 0, sets, e.Message);
        }

        var droppedTelegram = false;
        string? retryNote = null;
        lock (_sync)
        {
            _vpnStatus = status;
            _appliedSets = sets;
            if (VpnBroken(status))
            {
                var delays = _options.VpnRetryDelays;
                var delay = delays.Count == 0 ? TimeSpan.Zero : delays[Math.Min(_vpnFailures, delays.Count - 1)];
                _vpnFailures++;
                _vpnRetryAt = _time.GetUtcNow() + delay;
                if (delay > TimeSpan.Zero)
                    retryNote = $"VPN: следующая попытка через {Describe(delay)}";
                // Запасной канал Telegram — только в живой VPN
                if (_telegramFallbackSince is not null)
                {
                    _telegramFallbackSince = null;
                    _telegramFailures = 0;
                    droppedTelegram = true;
                }
            }
            else
            {
                _vpnFailures = 0;
                _vpnRetryAt = DateTimeOffset.MinValue;
            }
        }

        if (status.Error is { } error && status.State != VpnState.NoKey)
            Write($"VPN: {error}");
        if (retryNote is not null)
            Write(retryNote);
        if (droppedTelegram)
        {
            Write("VPN не работает — Telegram снова через прокси, без запасного канала");
            RequestVpn();
        }
        Notify();
    }

    private static string Describe(TimeSpan delay) =>
        delay.TotalMinutes >= 1 ? $"{delay.TotalMinutes:0} мин" : $"{delay.TotalSeconds:0} с";

    /// <summary>Нейросети и то, что zapret отдал в туннель, — проверяем так, как откроет браузер.</summary>
    private async Task ProbeVpnAsync(CancellationToken ct)
    {
        if (!_vpn.HasKey)
        {
            lock (_sync)
                _vpnReport = null;
            Notify();
            return;
        }
        List<Service> services;
        lock (_sync)
            services = [Service.Ai, .. ZapretServices.Where(_zapretFallback.Contains)];
        var report = await _probe.CheckAsync(services, new HashSet<Service>(), ct);
        lock (_sync)
            _vpnReport = report;
        LogProblems("VPN", report);
        Notify();
    }

    // ===================================================================== общий отчёт

    /// <summary>
    /// Общий отчёт для окна: YouTube и Discord — от zapret, а если их взял VPN — от VPN; Telegram — свой;
    /// нейросети — от VPN. «Есть ли интернет» — по проверке напрямую.
    /// </summary>
    private HealthReport? MergedReport()
    {
        if (_zapretReport is null && _telegramReport is null && _vpnReport is null)
            return null;
        var services = new Dictionary<Service, bool>();
        var problems = new Dictionary<Service, string>();

        void Take(HealthReport? from, IEnumerable<Service> which)
        {
            if (from is null)
                return;
            foreach (var s in which)
            {
                if (!from.Services.TryGetValue(s, out var ok))
                    continue;
                services[s] = ok;
                if (from.Problems.TryGetValue(s, out var p))
                    problems[s] = p;
                else
                    problems.Remove(s);
            }
        }

        Take(_zapretReport, ZapretServices.Except(_zapretFallback));
        Take(_vpnReport, [Service.Ai, .. ZapretServices.Where(_zapretFallback.Contains)]);
        Take(_telegramReport, [Service.Telegram]);

        return new HealthReport(services, _zapretReport?.InternetOk ?? _internetOk)
        {
            AiCountry = _vpnReport?.AiCountry,
            AiExitIp = _vpnReport?.AiExitIp,
            TelegramClients = _telegramReport?.TelegramClients,
            TelegramAppRunning = _telegramReport?.TelegramAppRunning ?? false,
            Problems = problems,
        };
    }

    /// <summary>Причины провалов пишем в журнал только когда они меняются, чтобы не засорять его каждые 2,5 минуты.</summary>
    private void LogProblems(string part, HealthReport report)
    {
        var problems = string.Join("; ", report.Problems.OrderBy(p => p.Key).Select(p => $"{p.Key}: {p.Value}"));
        lock (_sync)
        {
            if (_loggedProblems.TryGetValue(part, out var last) ? last == problems : problems.Length == 0)
            {
                _loggedProblems[part] = problems;
                return;
            }
            _loggedProblems[part] = problems;
        }
        Write(problems.Length > 0 ? $"Не прошло: {problems}" : $"{part}: все проверки прошли");
    }

    // ===================================================================== прочее

    private IReadOnlySet<Service> ViaVpn()
    {
        var set = new HashSet<Service>(_zapretFallback);
        if (_telegramFallbackSince is not null)
            set.Add(Service.Telegram);
        set.Add(Service.Ai);
        return set;
    }

    private void Remember(int index)
    {
        var id = _zapret.Strategies[index].Id;
        _settings.Update(s => s.LastStrategyId = id);
    }

    private int IndexOf(string? id)
    {
        if (id is null)
            return -1;
        var strategies = _zapret.Strategies;
        for (var i = 0; i < strategies.Count; i++)
        {
            if (strategies[i].Id == id)
                return i;
        }
        return -1;
    }

    private int SafeCount()
    {
        try
        {
            return _zapret.Strategies.Count;
        }
        catch (InvalidDataException)
        {
            return 0;
        }
    }

    private void SetState(ZapretState state)
    {
        _state = state;
        Notify();
    }

    private void Notify() => StatusChanged?.Invoke(Status);

    private void Write(string message) => Log?.Invoke(message);
}
