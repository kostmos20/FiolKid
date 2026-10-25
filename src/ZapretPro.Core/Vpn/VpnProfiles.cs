using ZapretPro.Core.Infrastructure;
using ZapretPro.Core.Key;

namespace ZapretPro.Core.Vpn;

/// <summary>Что нужно, чтобы поднять туннель: конфиг для ядра и адреса серверов (их нельзя пускать в сам туннель).</summary>
public sealed record TunnelPlan(string Conf, IReadOnlyList<string> EndpointHosts);

/// <summary>Один из способов подключения VPN: ключ Amnezia или подписка. Работает один за раз.</summary>
public interface IVpnProfile
{
    /// <summary>Постоянный код способа — чтобы запомнить выбор: «amnezia», «subscription».</summary>
    string Id { get; }

    /// <summary>Для человека: «ключ Amnezia», «подписка «Quattro VPN»».</summary>
    string Name { get; }

    /// <summary>Вставлен ли ключ или ссылка для этого способа.</summary>
    bool Configured { get; }

    IVpnTunnel Tunnel { get; }

    /// <summary>Почему не получилось подготовить подключение (ключ не подходит, подписка не скачалась и т. п.).</summary>
    string? Error { get; }

    /// <summary>Готовит конфиг. Null — запускать нечего, причина в <see cref="Error"/>.</summary>
    Task<TunnelPlan?> PrepareAsync(CancellationToken ct);
}

/// <summary>Ключ vpn:// от Amnezia → AmneziaWG (amneziawg.exe).</summary>
public sealed class AmneziaProfile : IVpnProfile
{
    private readonly KeyStore _keys;
    private string? _keyLoaded;
    private AwgConfig? _config;

    public AmneziaProfile(KeyStore keys, IVpnTunnel tunnel)
    {
        _keys = keys;
        Tunnel = tunnel;
    }

    public string Id => "amnezia";
    public string Name => "ключ Amnezia";
    public bool Configured => _keys.HasKey;
    public IVpnTunnel Tunnel { get; }
    public string? Error { get; private set; }

    public Task<TunnelPlan?> PrepareAsync(CancellationToken ct)
    {
        var key = _keys.Load();
        if (key is null)
        {
            _keyLoaded = null;
            _config = null;
            Error = _keys.HasKey ? "Сохранённый ключ не читается. Вставь ключ заново." : null;
            return Task.FromResult<TunnelPlan?>(null);
        }

        if (key != _keyLoaded)
        {
            _keyLoaded = key;
            try
            {
                _config = AwgConfig.FromKey(KeyParser.Parse(key));
                Error = null;
            }
            catch (KeyParseException e)
            {
                _config = null;
                Error = e.Message;
            }
        }

        // Маршрутами управляет программа: служба пускает в туннель всё, что направит Windows
        return Task.FromResult(_config is null ? null : new TunnelPlan(_config.ToConf(["0.0.0.0/0"], tableOff: true), [_config.EndpointHost]));
    }
}

/// <summary>Ссылка-подписка (VLESS, Hysteria2 и т. п.) → sing-box. Список серверов обновляется сам.</summary>
public sealed class SubscriptionProfile : IVpnProfile
{
    private readonly SubscriptionStore _store;
    private readonly SubscriptionClient _client;
    private readonly TimeProvider _time;
    private readonly SettingsStore? _settings;
    private readonly ServerBenchmark? _benchmark;
    private readonly SemaphoreSlim _benchmarkGate = new(1, 1);
    private DateTimeOffset _retryFetchAt;

    // Замер серверов повторяем раз в сутки: серверы подписки и их загрузка меняются
    private static readonly TimeSpan BenchmarkLifetime = TimeSpan.FromHours(24);

    public SubscriptionProfile(SubscriptionStore store, SubscriptionClient client, IVpnTunnel tunnel, TimeProvider? time = null,
        SettingsStore? settings = null, ServerBenchmark? benchmark = null)
    {
        _store = store;
        _client = client;
        Tunnel = tunnel;
        _time = time ?? TimeProvider.System;
        _settings = settings;
        _benchmark = benchmark;
        if (_benchmark is not null)
            _benchmark.Log += m => Log?.Invoke(m);
    }

    /// <summary>Сейчас идёт замер серверов — для экрана настроек.</summary>
    public bool Measuring { get; private set; }

    /// <summary>Серверы, через которые подключён туннель (лучшие по замеру или выбранный вручную).</summary>
    public IReadOnlyList<string> ChosenNames { get; private set; } = [];

    public event Action<string>? Log;

    public string Id => "subscription";
    public string Name => Info?.Title is { Length: > 0 } title ? $"подписка «{title}»" : "подписка";
    public bool Configured => _store.HasSubscription;
    public IVpnTunnel Tunnel { get; }
    public string? Error { get; private set; }

    /// <summary>Что известно о подписке: название, срок, сколько серверов — для настроек.</summary>
    public ParsedSubscription? Info { get; private set; }

    public async Task<TunnelPlan?> PrepareAsync(CancellationToken ct)
    {
        var source = _store.LoadSource();
        if (source is null)
        {
            Info = null;
            Error = _store.HasSubscription ? "Сохранённая ссылка не читается. Вставь её заново." : null;
            return null;
        }

        var cache = _store.LoadCache();
        var stale = cache is null || _time.GetUtcNow() - cache.FetchedAt >= (Info?.UpdateInterval ?? TimeSpan.FromHours(3));
        if (stale && _time.GetUtcNow() >= _retryFetchAt)
        {
            try
            {
                var fresh = await _client.LoadAsync(source, _time, ct);
                if (SubscriptionParser.Parse(fresh.Body, fresh.Headers).Servers.Count > 0)
                {
                    _store.SaveCache(fresh);
                    cache = fresh;
                    Log?.Invoke("Список серверов подписки обновлён");
                }
                else
                    Log?.Invoke("Подписка ответила без подходящих серверов — оставляю прежний список");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
            {
                // Сайт подписки недоступен — работаем на прошлом списке, повторим позже
                _retryFetchAt = _time.GetUtcNow() + TimeSpan.FromMinutes(15);
                Log?.Invoke($"Подписка не скачалась: {e.Message}");
            }
        }

        if (cache is null)
        {
            Error = "Подписка не скачалась — проверь ссылку или интернет.";
            return null;
        }

        Info = SubscriptionParser.Parse(cache.Body, cache.Headers);
        if (Info.Servers.Count == 0)
        {
            Error = "В подписке нет серверов, которые поддерживает программа.";
            return null;
        }
        if (Info.Expires is { } expires && expires < _time.GetUtcNow())
        {
            Error = $"Срок подписки закончился {expires.ToLocalTime():dd.MM.yyyy}.";
            return null;
        }

        Error = null;
        var selected = await ChooseServersAsync(Info.Servers, source, ct);
        ChosenNames = selected.Select(s => s.Name).ToList();
        return new TunnelPlan(SingBoxConfig.Build(selected, VpnManager.TunnelName), selected.Select(s => s.Host).Distinct().ToList());
    }

    /// <summary>
    /// Через какие серверы подключаться: выбранный вручную; иначе — три лучших по замеру скорости
    /// (sing-box держит их в автовыборе и переключится, если один перестанет отвечать); замера нет —
    /// как раньше, до 20 серверов по типу.
    /// </summary>
    private async Task<IReadOnlyList<ProxyServer>> ChooseServersAsync(IReadOnlyList<ProxyServer> servers, string source, CancellationToken ct)
    {
        var settings = _settings?.Load();
        if (settings?.ServerAutoSelect == false)
        {
            var manual = servers.FirstOrDefault(s => s.Key == settings.ManualServerKey);
            if (manual is not null)
                return [manual];
            Log?.Invoke("Выбранного вручную сервера больше нет в подписке — выбираю сам");
        }

        if (settings is not null)
        {
            var keys = servers.Select(s => s.Key).ToHashSet();
            var scores = settings.ServerBenchmark;
            var fresh = scores is { Count: > 0 } && settings.ServerBenchmarkAt is { } at && _time.GetUtcNow() - at < BenchmarkLifetime
                        && scores.All(s => keys.Contains(s.Key));
            if (!fresh && _benchmark is not null)
                scores = (await MeasureAsync(servers, ct))?.ToList() ?? scores;
            var best = ServerBenchmark.Best(scores ?? []).Select(k => servers.FirstOrDefault(s => s.Key == k)).OfType<ProxyServer>().ToList();
            if (best.Count > 0)
                return best;
        }

        // Один и тот же выбор серверов для одной и той же подписки: иначе конфиг менялся бы при каждом
        // обновлении списка и VPN зря переподключался бы
        return SingBoxConfig.Select(servers, random: new Random(StableHash(source)));
    }

    /// <summary>Кнопка «Замерить серверы»: заново по сохранённому списку подписки.</summary>
    public async Task<IReadOnlyList<ServerScore>?> MeasureNowAsync(CancellationToken ct)
    {
        var cache = _store.LoadCache();
        if (cache is null)
            return null;
        var servers = SubscriptionParser.Parse(cache.Body, cache.Headers).Servers;
        return servers.Count == 0 ? null : await MeasureAsync(servers, ct);
    }

    private async Task<IReadOnlyList<ServerScore>?> MeasureAsync(IReadOnlyList<ProxyServer> servers, CancellationToken ct)
    {
        if (_benchmark is null || _settings is null)
            return null;
        await _benchmarkGate.WaitAsync(ct);
        Measuring = true;
        try
        {
            // Сначала лучшие по типу: если серверов больше 40, меряем самые перспективные
            var candidates = SingBoxConfig.Select(servers, ServerBenchmark.MaxServers, new Random(1));
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(120));
            var scores = await _benchmark.RunAsync(candidates, limit.Token);
            _settings.Update(s =>
            {
                s.ServerBenchmarkAt = _time.GetUtcNow();
                s.ServerBenchmark = scores.ToList();
            });
            var working = scores.Count(s => s.Works);
            var top = scores.FirstOrDefault(s => s.Works);
            Log?.Invoke($"Замер серверов: отвечают {working} из {scores.Count}" + (top is null ? "" :
                $"; лучший «{top.Name}» — {top.PingMs} мс" + (top.Mbps is { } m ? $", {m} Мбит/с" : "") +
                (top.Country is { } c ? $", выход {c}" : "")));
            return scores;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception || e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            Log?.Invoke($"Замер серверов не получился: {e.Message}");
            return null;
        }
        finally
        {
            Measuring = false;
            _benchmarkGate.Release();
        }
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in text)
                hash = (hash ^ c) * 16777619;
            return hash;
        }
    }
}
