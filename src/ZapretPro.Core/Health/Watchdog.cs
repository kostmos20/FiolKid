using ZapretPro.Core.Infrastructure;
using ZapretPro.Core.Zapret;

namespace ZapretPro.Core.Health;

public enum ZapretState
{
    Stopped,
    /// <summary>Перебираем стратегии.</summary>
    Searching,
    /// <summary>YouTube и Discord открываются.</summary>
    Working,
    /// <summary>Лучшая из стратегий открывает только часть сервисов.</summary>
    Partial,
    /// <summary>Ни одна стратегия не помогла. На этапе 3 здесь включится запасной канал через VPN.</summary>
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
    string? LastError);

public sealed record WatchdogOptions
{
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromMinutes(2.5);
    /// <summary>Пауза после смены стратегии, прежде чем проверять.</summary>
    public TimeSpan WarmUp { get; init; } = TimeSpan.FromSeconds(1.5);
    public int FailuresBeforeSwitch { get; init; } = 2;
    /// <summary>Как часто пробовать заново, если zapret не справился (чтобы не гонять видео через сервер знакомого).</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Следит за zapret и прокси Telegram: периодически проверяет сервисы, перезапускает
/// упавшие процессы и перебирает стратегии по алгоритму из PLAN.md (раздел «Zapret»).
/// </summary>
public sealed class Watchdog
{
    private static readonly Service[] ZapretServices = [Service.YouTube, Service.Discord];

    private readonly IZapretRunner _zapret;
    private readonly ITelegramProxy _telegram;
    private readonly IHealthProbe _probe;
    private readonly SettingsStore _settings;
    private readonly WatchdogOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ZapretState _state = ZapretState.Stopped;
    private int _current = -1;
    private int _failures;
    private int _expectedOk;
    private DateTimeOffset _lastSearch;
    private HealthReport? _lastReport;
    private string? _lastError;

    public Watchdog(IZapretRunner zapret, ITelegramProxy telegram, IHealthProbe probe, SettingsStore settings,
        WatchdogOptions? options = null, TimeProvider? time = null)
    {
        _zapret = zapret;
        _telegram = telegram;
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
        _lastError);

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

    /// <summary>Запуск: прокси Telegram и последняя рабочая стратегия, если она ещё помогает.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureTelegramAsync(ct);

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
            await EnsureTelegramAsync(ct);

            switch (_state)
            {
                case ZapretState.Stopped:
                    return;

                case ZapretState.Failed:
                    if (_time.GetUtcNow() - _lastSearch >= _options.RetryInterval)
                    {
                        Write("Прошёл час — снова пробую обойти блокировку без запасного канала");
                        await SearchAsync(0, ct);
                    }
                    else
                        await ProbeAsync(ct);
                    return;

                case ZapretState.Partial when _time.GetUtcNow() - _lastSearch >= _options.RetryInterval:
                    Write("Прошёл час — ищу стратегию, при которой откроется всё");
                    await SearchAsync(_current + 1, ct);
                    return;
            }

            if (!_zapret.IsRunning)
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
                SetState(ZapretState.NoInternet);
                _failures = 0;
                return;
            }

            var ok = report.CountOk(ZapretServices);
            if (ok == ZapretServices.Length)
            {
                _failures = 0;
                if (_state != ZapretState.Working)
                    Remember(_current);
                SetState(ZapretState.Working);
                return;
            }

            // Частичный режим: пока открывается не меньше, чем при выборе стратегии, — всё по плану
            if (ok >= _expectedOk && _expectedOk < ZapretServices.Length)
            {
                _failures = 0;
                SetState(ZapretState.Partial);
                return;
            }

            _failures++;
            Write($"Проверка не прошла ({_failures} из {_options.FailuresBeforeSwitch})");
            if (_failures >= _options.FailuresBeforeSwitch)
                await SearchAsync(_current + 1, ct);
            else
                Notify();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Останавливает winws и прокси Telegram. Сначала отмени токен <see cref="RunAsync"/> и дождись его,
    /// иначе идущий перебор успеет запустить winws заново.
    /// </summary>
    public void Stop()
    {
        _zapret.Stop();
        _telegram.Stop();
        _current = -1;
        SetState(ZapretState.Stopped);
    }

    /// <summary>
    /// Перебирает все стратегии по кругу, начиная с <paramref name="start"/>.
    /// Первая, при которой открылось всё, становится рабочей. Если такой нет —
    /// берём лучшую из частично рабочих, а если и их нет — сдаёмся до следующей попытки через час.
    /// </summary>
    private async Task SearchAsync(int start, CancellationToken ct)
    {
        var strategies = _zapret.Strategies;
        _failures = 0;
        _lastSearch = _time.GetUtcNow();
        SetState(ZapretState.Searching);

        var best = (Ok: 0, Index: -1);
        var startErrors = 0;

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
                continue;
            }

            await Task.Delay(_options.WarmUp, _time, ct);
            var report = await ProbeAsync(ct);

            if (!report.InternetOk)
            {
                // Нет интернета — перебирать бессмысленно, оставляем эту стратегию и ждём.
                // Что она умеет, неизвестно, поэтому после появления сети ждём от неё всего.
                Write("Нет интернета, перебор остановлен");
                _expectedOk = ZapretServices.Length;
                SetState(ZapretState.NoInternet);
                return;
            }

            var ok = report.CountOk(ZapretServices);
            Write($"Стратегия «{strategies[index].Id}»: открылось {ok} из {ZapretServices.Length}");
            if (ok == ZapretServices.Length)
            {
                _expectedOk = ok;
                _lastError = null;
                Remember(index);
                SetState(ZapretState.Working);
                return;
            }

            if (ok > best.Ok)
                best = (ok, index);
        }

        if (best.Index >= 0 && await _zapret.StartAsync(strategies[best.Index], ct))
        {
            _current = best.Index;
            _expectedOk = best.Ok;
            Write($"Полностью не помогла ни одна стратегия, оставляю лучшую «{strategies[best.Index].Id}»");
            SetState(ZapretState.Partial);
            return;
        }

        _zapret.Stop();
        _current = -1;
        _expectedOk = 0;
        if (startErrors == strategies.Count)
            _lastError = $"zapret не запускается ни с одной стратегией. Последняя ошибка: {_zapret.LastError}";
        Write("Ни одна стратегия не помогла");
        SetState(ZapretState.Failed);
    }

    private async Task<HealthReport> ProbeAsync(CancellationToken ct)
    {
        var services = _state is ZapretState.Failed or ZapretState.Stopped
            ? [Service.Telegram]
            : ZapretServices.Append(Service.Telegram).ToArray();
        _lastReport = await _probe.CheckAsync(services, ct);
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
