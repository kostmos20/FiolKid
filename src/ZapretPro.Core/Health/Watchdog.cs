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
}

public sealed record WatchdogOptions
{
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMinutes(2.5);
    /// <summary>Пауза после смены стратегии, прежде чем проверять.</summary>
    public TimeSpan WarmUp { get; init; } = TimeSpan.FromSeconds(1.5);
    public int FailuresBeforeSwitch { get; init; } = 2;
    /// <summary>Как часто пробовать вернуться с запасного канала (чтобы не гонять видео через сервер знакомого).</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Следит за zapret, прокси Telegram и VPN: периодически проверяет сервисы, перезапускает
/// упавшее, перебирает стратегии (PLAN.md, раздел «Zapret») и включает запасной режим через VPN.
/// </summary>
public sealed class Watchdog
{
    private static readonly Service[] ZapretServices = [Service.YouTube, Service.Discord];

    private readonly IZapretRunner _zapret;
    private readonly ITelegramProxy _telegram;
    private readonly IVpn _vpn;
    private readonly IHealthProbe _probe;
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

    // Запасной режим: какие сервисы zapret сейчас отдал VPN, и с какого момента Telegram идёт через VPN
    private readonly HashSet<Service> _zapretFallback = [];
    private DateTimeOffset? _telegramFallbackSince;
    private int _telegramFailures;
    private VpnStatus _vpnStatus = VpnStatus.Initial;

    public Watchdog(IZapretRunner zapret, ITelegramProxy telegram, IVpn vpn, IHealthProbe probe, SettingsStore settings,
        WatchdogOptions? options = null, TimeProvider? time = null)
    {
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
        _lastError) { NeedsReboot = _needsReboot };

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

            var saved = _settings.Load().LastStrategyId;
            var start = Math.Max(0, IndexOf(saved));
            Write(saved is null ? "Сохранённой стратегии нет, начинаю с первой" : $"Начинаю с сохранённой стратегии «{saved}»");
            await SearchAsync(start, ct);
        }
        finally
        {
            _gate.Release();
        }
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
                await SearchAsync(_state == ZapretState.Failed ? 0 : _current + 1, ct);
                return;
            }

            if (_state != ZapretState.Failed && !_zapret.IsRunning)
            {
                Write("winws не работает — перезапускаю");
                if (!await _zapret.StartAsync(_zapret.Strategies[_current], ct))
                {
                    _lastError = _zapret.LastError;
                    await SearchAsync(_current + 1, ct);
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
                        await SearchAsync(_current + 1, ct);
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
    private async Task SearchAsync(int start, CancellationToken ct)
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

        for (var i = 0; i < strategies.Count; i++)
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

        if (best.Index >= 0 && await _zapret.StartAsync(strategies[best.Index], ct))
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
            else if (startErrors == strategies.Count)
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
