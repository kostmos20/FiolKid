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
}

/// <summary>
/// Следит за zapret, прокси Telegram и VPN: периодически проверяет сервисы, перезапускает
/// упавшее, перебирает стратегии (docs/PLAN.md, раздел «Zapret») и включает запасной режим через VPN.
/// </summary>
public sealed class Watchdog
{
    private static readonly Service[] ZapretServices = [Service.YouTube, Service.Discord];

    private readonly IZapretRunner _zapret;
    private readonly ITelegramProxy _telegram;
    private readonly IVpn _vpn;
    private readonly IHealthProbe _probe;
    private readonly IZapretSpeedProbe? _speed;
    private readonly SettingsStore _settings;
    private readonly WatchdogOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ZapretState _state = ZapretState.Stopped;
    private int _current = -1;
    private int _failures;
    private DateTimeOffset _lastSearch;
    private HealthReport? _lastReport;
    private string _lastProblems = "";
    private string? _lastError;
    private bool _needsReboot;
    private bool _measuring;

    // Запасной режим: какие сервисы zapret сейчас отдал VPN, и с какого момента Telegram идёт через VPN
    private readonly HashSet<Service> _zapretFallback = [];
    private DateTimeOffset? _telegramFallbackSince;
    private int _telegramFailures;
    private VpnStatus _vpnStatus = VpnStatus.Initial;

    public Watchdog(IZapretRunner zapret, ITelegramProxy telegram, IVpn vpn, IHealthProbe probe, SettingsStore settings,
        WatchdogOptions? options = null, TimeProvider? time = null, IZapretSpeedProbe? speed = null)
    {
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

    public WatchdogStatus Status => new(
        _state,
        _current >= 0 ? _zapret.Strategies[_current].Id : null,
        _current + 1,
        SafeCount(),
        _lastReport,
        _telegram.IsRunning,
        ViaVpn(),
        _vpnStatus,
        _lastError) { NeedsReboot = _needsReboot, Measuring = _measuring };

    /// <summary>Запуск и бесконечный цикл проверок до отмены.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        await StartAsync(ct);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_options.CheckInterval, _time, ct);
            await CheckAsync(ct);
        }
    }

    /// <summary>Запуск: прокси Telegram, VPN для нейросетей и последняя рабочая стратегия, если она ещё помогает.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureTelegramAsync(ct);
            await ApplyVpnAsync(ct);
            await StartStrategyAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Сменили режим выбора альта в настройках — применить сразу.</summary>
    public async Task ApplyStrategySettingsAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state != ZapretState.Stopped)
                await StartStrategyAsync(ct, "настройки изменены");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Кнопка «Замерить сейчас»: все альты по скорости, затем самый быстрый.</summary>
    public async Task BenchmarkAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state != ZapretState.Stopped && _speed is not null)
                await BenchmarkCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
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
            await BenchmarkCoreAsync(ct);
            return;
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
        _measuring = true;
        SetState(ZapretState.Searching);
        StrategyScore? best;
        try
        {
            if (_zapretFallback.Count > 0)
            {
                _zapretFallback.Clear();
                await ApplyVpnAsync(ct);
            }

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

    /// <summary>Одна плановая проверка. Можно вызвать и вне расписания («Проверить сейчас»).</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state == ZapretState.Stopped)
                return;

            await EnsureTelegramAsync(ct);

            if (_state is ZapretState.Failed or ZapretState.Partial
                && _time.GetUtcNow() - _lastSearch >= _options.RetryInterval)
            {
                Write("Прошёл час — пробую снова обойтись без запасного канала");
                if (ManualIndex(_settings.Load()) is { } manual)
                    await SearchAsync(manual, ct, only: true);
                else
                    await SearchAsync(_state == ZapretState.Failed ? 0 : _current + 1, ct);
                return;
            }

            if (_state != ZapretState.Failed && !_zapret.IsRunning)
            {
                Write("winws не работает — перезапускаю");
                if (!await _zapret.StartAsync(_zapret.Strategies[_current], ct))
                {
                    _lastError = _zapret.LastError;
                    await RecoverStrategyAsync(ct);
                    return;
                }
            }

            var report = await ProbeAsync(ct);
            if (!report.InternetOk)
            {
                if (_state != ZapretState.Failed)
                    SetState(ZapretState.NoInternet);
                _failures = 0;
                return;
            }

            UpdateTelegramFallback(report);

            if (_state != ZapretState.Failed)
            {
                // Сервисы, отданные VPN, проверка zapret не касается: они открываются через туннель
                var active = ZapretServices.Except(_zapretFallback).ToList();
                if (active.All(s => report[s]))
                {
                    _failures = 0;
                    var state = _zapretFallback.Count == 0 ? ZapretState.Working : ZapretState.Partial;
                    if (state == ZapretState.Working && _state != ZapretState.Working)
                        Remember(_current);
                    SetState(state);
                }
                else
                {
                    _failures++;
                    Write($"Проверка не прошла ({_failures} из {_options.FailuresBeforeSwitch})");
                    if (_failures >= _options.FailuresBeforeSwitch)
                    {
                        await RecoverStrategyAsync(ct);
                        return;
                    }
                    Notify();
                }
            }

            await ApplyVpnAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Останавливает winws, прокси Telegram и VPN. Сначала отмени токен <see cref="RunAsync"/> и дождись его,
    /// иначе идущий перебор успеет запустить всё заново.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        // Драйвер WinDivert не выгружаем: принудительная остановка могла оставить его «застрявшим»,
        // и до перезагрузки zapret не запускался (FWP_E_IN_USE). Он выгрузится сам при перезагрузке.
        _zapret.Stop();
        _telegram.Stop();
        await _vpn.StopAsync(ct);
        _vpnStatus = _vpn.Status;
        _current = -1;
        _zapretFallback.Clear();
        _telegramFallbackSince = null;
        _telegramFailures = 0;
        SetState(ZapretState.Stopped);
    }

    /// <summary>
    /// Перебирает все стратегии по кругу, начиная с <paramref name="start"/>.
    /// Первая, при которой открылось всё, становится рабочей. Если такой нет — берём лучшую из
    /// частично рабочих, а остальное отдаём VPN; если не помогла ни одна — YouTube и Discord идут через VPN.
    /// </summary>
    /// <param name="only">Проверить только альт <paramref name="start"/> (выбран вручную): не помог — недостающее через VPN.</param>
    private async Task SearchAsync(int start, CancellationToken ct, bool only = false)
    {
        var strategies = _zapret.Strategies;
        _failures = 0;
        _lastSearch = _time.GetUtcNow();
        SetState(ZapretState.Searching);

        // Пока ищем, YouTube и Discord должны идти мимо VPN — иначе проверка покажет успех туннеля, а не zapret
        if (_zapretFallback.Count > 0)
        {
            _zapretFallback.Clear();
            await ApplyVpnAsync(ct);
        }

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
            var report = await ProbeAsync(ct);

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
                await ApplyVpnAsync(ct);
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
            _zapretFallback.UnionWith(best.Missing);
            Write($"Полностью не помогла ни одна стратегия, оставляю «{strategies[best.Index].Id}», " +
                  $"через запасной канал: {string.Join(", ", best.Missing)}");
            SetState(ZapretState.Partial);
        }
        else
        {
            _zapret.Stop();
            _current = -1;
            _zapretFallback.UnionWith(ZapretServices);
            if (driverBroken)
                _lastError = _zapret.LastError;
            else if (startErrors == count)
                _lastError = $"zapret не запускается ни с одной стратегией. Последняя ошибка: {_zapret.LastError}";
            Write("Ни одна стратегия не помогла — YouTube и Discord через запасной канал");
            SetState(ZapretState.Failed);
        }

        await ApplyVpnAsync(ct);
        if (_vpn.HasKey)
            await ProbeAsync(ct);
    }

    /// <summary>
    /// Telegram через VPN, если прокси работает, но до серверов Telegram не достучаться два раза подряд.
    /// Через час пробуем снова без VPN.
    /// </summary>
    private void UpdateTelegramFallback(HealthReport report)
    {
        if (!_telegram.IsRunning)
            return;

        if (_telegramFallbackSince is { } since)
        {
            if (_time.GetUtcNow() - since >= _options.RetryInterval)
            {
                Write("Прошёл час — пробую Telegram без запасного канала");
                _telegramFallbackSince = null;
                _telegramFailures = 0;
            }
            return;
        }

        if (report[Service.Telegram])
        {
            _telegramFailures = 0;
            return;
        }

        if (++_telegramFailures >= _options.FailuresBeforeSwitch)
        {
            Write("Telegram не работает через прокси — включаю запасной канал");
            _telegramFallbackSince = _time.GetUtcNow();
        }
    }

    private async Task ApplyVpnAsync(CancellationToken ct)
    {
        var sets = new HashSet<RouteSet>();
        if (_zapretFallback.Contains(Service.YouTube))
            sets.Add(RouteSet.YouTube);
        if (_zapretFallback.Contains(Service.Discord))
            sets.Add(RouteSet.Discord);
        if (_telegramFallbackSince is not null)
            sets.Add(RouteSet.Telegram);

        try
        {
            _vpnStatus = await _vpn.ApplyAsync(sets, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                  or System.ComponentModel.Win32Exception)
        {
            _vpnStatus = new VpnStatus(VpnState.Failed, 0, sets, e.Message);
        }

        if (_vpnStatus.Error is { } error && _vpnStatus.State != VpnState.NoKey)
            Write($"VPN: {error}");
        Notify();
    }

    private async Task<HealthReport> ProbeAsync(CancellationToken ct)
    {
        var services = new List<Service>(ZapretServices) { Service.Telegram };
        if (_vpn.HasKey)
            services.Add(Service.Ai);
        _lastReport = await _probe.CheckAsync(services, ct);

        // Причины провалов пишем в журнал только когда они меняются, чтобы не засорять его каждые 2,5 минуты
        var problems = string.Join("; ", _lastReport.Problems.OrderBy(p => p.Key).Select(p => $"{p.Key}: {p.Value}"));
        if (problems != _lastProblems)
        {
            Write(problems.Length > 0 ? $"Не прошло: {problems}" : "Все проверки прошли");
            _lastProblems = problems;
        }

        Notify();
        return _lastReport;
    }

    /// <summary>Кнопка «Перезапустить прокси»: остановить и запустить заново, затем проверить.</summary>
    public async Task RestartTelegramAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_state == ZapretState.Stopped)
                return;
            Write("Перезапускаю прокси Telegram по просьбе");
            _telegram.Stop();
            _telegramFallbackSince = null;
            _telegramFailures = 0;
            await EnsureTelegramAsync(ct);
            await ProbeAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
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
