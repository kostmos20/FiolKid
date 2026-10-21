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
    private DateTimeOffset _retryFetchAt;

    public SubscriptionProfile(SubscriptionStore store, SubscriptionClient client, IVpnTunnel tunnel, TimeProvider? time = null)
    {
        _store = store;
        _client = client;
        Tunnel = tunnel;
        _time = time ?? TimeProvider.System;
    }

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
        // Один и тот же выбор серверов для одной и той же подписки: иначе конфиг менялся бы при каждом
        // обновлении списка и VPN зря переподключался бы
        var seed = StableHash(source);
        var selected = SingBoxConfig.Select(Info.Servers, random: new Random(seed));
        return new TunnelPlan(SingBoxConfig.Build(selected, VpnManager.TunnelName), selected.Select(s => s.Host).Distinct().ToList());
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
