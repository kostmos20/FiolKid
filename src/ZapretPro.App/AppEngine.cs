using System.IO;
using System.Text;
using ZapretPro.Core.Dns;
using ZapretPro.Core.Health;
using ZapretPro.Core.Infrastructure;
using ZapretPro.Core.Key;
using ZapretPro.Core.Vpn;
using ZapretPro.Core.Zapret;

namespace ZapretPro.App;

/// <summary>Состояние сервиса так, как его видит пользователь.</summary>
public enum ServiceLook
{
    Off,
    Checking,
    Working,
    NotWorking,
    /// <summary>Telegram: прокси готов, но сам Telegram к нему ещё не подключили.</summary>
    NeedsConnect,
    /// <summary>Нейросети: нужен ключ (или вставленный не подходит).</summary>
    NeedsKey,
}

/// <summary>
/// «Мотор» программы: zapret, прокси Telegram, VPN и Watchdog. Окна только показывают его состояние
/// и вызывают его методы — их можно закрывать и открывать, работа продолжается.
/// </summary>
public sealed class AppEngine
{
    private const int MaxLogLines = 500;
    private static readonly TimeSpan ListRefreshInterval = TimeSpan.FromHours(6);

    private readonly SettingsStore _settings = new();
    private readonly ZapretManager _zapret = new();
    private readonly VpnController _vpn;
    private readonly Watchdog _watchdog;
    private readonly ListUpdater _lists;
    private readonly List<string> _log = [];
    private CancellationTokenSource? _cts;
    private Task? _run;

    public AppEngine()
    {
        var secret = _settings.Load().TelegramSecret;
        if (secret is null)
        {
            secret = TelegramProxy.NewSecret();
            _settings.Update(s => s.TelegramSecret = secret);
        }
        Telegram = new TelegramProxy(secret);

        var vpnDir = Path.Combine(AppPaths.InstallDir, "vpn");
        var updatedDir = Path.Combine(AppPaths.DataDir, "lists");
        var zapretLists = Path.Combine(AppPaths.ZapretDir, "lists");
        _lists = new ListUpdater(updatedDir);
        // Если программа в прошлый раз упала, не вернув DNS Windows, — вернуть первым делом
        try
        {
            Task.Run(() => DnsHijack.RecoverAsync(VpnManager.TunnelName)).Wait(TimeSpan.FromSeconds(20));
        }
        catch (AggregateException e)
        {
            Write($"Не удалось вернуть DNS после прошлого запуска: {e.InnerException?.Message}");
        }

        var routes = new TunnelRoutes(VpnManager.TunnelName);
        routes.Log += Write;
        var dns = new DnsHijack(VpnManager.TunnelName);
        dns.Log += Write;
        _amnezia = new AmneziaProfile(Keys, new VpnManager());
        _subscription = new SubscriptionProfile(Subscriptions, _subscriptionClient, new SingBoxTunnel());
        _subscription.Log += Write;
        _vpn = new VpnController([_amnezia, _subscription],
            () => new Router(RouteLists.Load(vpnDir, updatedDir,
                [Path.Combine(zapretLists, "list-general.txt"), Path.Combine(zapretLists, "list-google.txt")])),
            routes, dns, _settings);
        _vpn.Log += Write;

        _watchdog = new Watchdog(_zapret, Telegram, _vpn, new HealthChecker(secret), _settings);
        _watchdog.StatusChanged += s =>
        {
            Status = s;
            StatusChanged?.Invoke();
        };
        _watchdog.Log += Write;
        Status = _watchdog.Status;
    }

    private readonly AmneziaProfile _amnezia;
    private readonly SubscriptionProfile _subscription;
    private readonly SubscriptionClient _subscriptionClient = new();

    public KeyStore Keys { get; } = new();
    public SubscriptionStore Subscriptions { get; } = new();
    public TelegramProxy Telegram { get; }
    public WatchdogStatus Status { get; private set; }
    public bool IsOn => _run is not null;

    /// <summary>Что-то поменялось — окна перерисовываются. Вызывается не из UI-потока.</summary>
    public event Action? StatusChanged;
    public event Action<string>? LogAdded;

    public bool WasEnabled => _settings.Load().Enabled;

    public IReadOnlyList<string> Log
    {
        get
        {
            lock (_log)
                return _log.ToList();
        }
    }

    public IReadOnlyList<string> WinwsOutput => _zapret.RecentOutput;

    public void TurnOn()
    {
        if (_run is not null)
            return;
        _settings.Update(s => s.Enabled = true);
        Write("Включено");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _run = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(_watchdog.RunAsync(ct), RefreshListsLoopAsync(ct));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                Write($"ОШИБКА: {e}");
            }
        }, ct);
        StatusChanged?.Invoke();
    }

    public async Task TurnOffAsync(bool remember = true)
    {
        if (remember)
            _settings.Update(s => s.Enabled = false);
        _cts?.Cancel();
        if (_run is not null)
            await _run;
        _run = null;
        try
        {
            await _watchdog.StopAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            Write($"Ошибка при остановке: {e.Message}");
        }
        Write("Выключено");
        StatusChanged?.Invoke();
    }

    public async Task CheckNowAsync()
    {
        if (_cts is not { IsCancellationRequested: false } cts)
            return;
        try
        {
            await Task.Run(() => _watchdog.CheckAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Вставили ключ или ссылку: сама понимает, что это. Ключ Amnezia (vpn://) проверяет и сохраняет;
    /// ссылку-подписку сначала скачивает и проверяет, что в ней есть подходящие серверы. Прежнее
    /// заменяется только после успешной проверки. Возвращает, что сохранено — для подсказки.
    /// </summary>
    public async Task<string> SaveKeyAsync(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith(KeyParser.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            AwgConfig.FromKey(KeyParser.Parse(trimmed));
            var parsed = Keys.Save(trimmed);
            Write($"Ключ сохранён: {VpnProtocolInfo.DisplayName(parsed.Protocol)}");
            AfterCredentialsChanged();
            return "Ключ Amnezia вставлен";
        }

        if (!SubscriptionClient.LooksLikeSubscription(trimmed))
            throw new KeyParseException("Это не ключ и не ссылка на подписку. Ключ начинается с vpn://, ссылка — с https://");

        CachedSubscription loaded;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            loaded = await _subscriptionClient.LoadAsync(trimmed, TimeProvider.System, cts.Token);
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or UriFormatException)
        {
            throw new KeyParseException($"Ссылка не открылась — проверь, что скопировал её целиком. ({e.Message})");
        }

        var parsedSub = SubscriptionParser.Parse(loaded.Body, loaded.Headers);
        if (parsedSub.Servers.Count == 0)
            throw new KeyParseException("В подписке нет серверов, с которыми умеет работать программа.");

        Subscriptions.SaveSource(trimmed);
        Subscriptions.SaveCache(loaded);
        var name = parsedSub.Title is { Length: > 0 } t ? $"«{t}»" : "";
        Write($"Подписка {name} сохранена: серверов {parsedSub.Servers.Count}" +
              (parsedSub.Skipped > 0 ? $", пропущено неподдерживаемых {parsedSub.Skipped}" : ""));
        AfterCredentialsChanged();
        return $"Подписка {name} вставлена: {parsedSub.Servers.Count} серверов";
    }

    public void DeleteKey()
    {
        Keys.Delete();
        Write("Ключ Amnezia удалён");
        AfterCredentialsChanged();
    }

    public void DeleteSubscription()
    {
        Subscriptions.Delete();
        Write("Подписка удалена");
        AfterCredentialsChanged();
    }

    private void AfterCredentialsChanged()
    {
        _vpn.ProfilesChanged();
        _ = CheckNowAsync();
    }

    /// <summary>Есть ли хоть что-то для VPN.</summary>
    public bool HasVpnCredentials => Keys.HasKey || Subscriptions.HasSubscription;

    /// <summary>Что с ключом Amnezia — человеческими словами.</summary>
    public (bool Ok, string Text) KeyState()
    {
        var key = Keys.Load();
        if (key is null)
            return (false, Keys.HasKey ? "Сохранённый ключ не читается — вставь его заново" : "Ключа нет");
        try
        {
            AwgConfig.FromKey(KeyParser.Parse(key));
            return (true, "Ключ Amnezia вставлен и подходит");
        }
        catch (KeyParseException e)
        {
            return (false, e.Message);
        }
    }

    /// <summary>Что с подпиской: название, сколько серверов, до какого числа. Без ссылки и паролей.</summary>
    public (bool Ok, string Text) SubscriptionState()
    {
        if (!Subscriptions.HasSubscription)
            return (false, "Подписки нет");
        var cache = Subscriptions.LoadCache();
        if (cache is null)
            return (false, "Подписка вставлена, но ещё не скачалась");
        var info = SubscriptionParser.Parse(cache.Body, cache.Headers);
        var title = info.Title is { Length: > 0 } t ? $"«{t}»" : "";
        var until = info.Expires is { } e ? $", до {e.ToLocalTime():dd.MM.yyyy}" : "";
        var expired = info.Expires is { } ex && ex < DateTimeOffset.UtcNow;
        return (info.Servers.Count > 0 && !expired,
            expired ? $"Подписка {title}: срок закончился{until}" : $"Подписка {title}: серверов {info.Servers.Count}{until}");
    }

    public ServiceLook Look(Service service)
    {
        var s = Status;
        if (!IsOn)
            return service == Service.Ai && !HasVpnCredentials ? ServiceLook.NeedsKey : ServiceLook.Off;

        if (service == Service.Ai && s.Vpn.State is VpnState.NoKey or VpnState.BadKey)
            return ServiceLook.NeedsKey;

        var report = s.LastReport;
        if (report is null || !report.Services.ContainsKey(service))
            return ServiceLook.Checking;

        if (service == Service.Ai && !report[service] && s.Vpn.State == VpnState.Connecting)
            return ServiceLook.Checking;

        if (!report[service])
            return ServiceLook.NotWorking;

        // Прокси исправен, но Telegram к нему не подключён — значит, у человека Telegram не работает
        if (service == Service.Telegram && report.TelegramClients is { Count: 0 })
            return ServiceLook.NeedsConnect;

        return ServiceLook.Working;
    }

    public enum Overall
    {
        Off,
        Busy,
        Ok,
        Problem,
    }

    /// <summary>Одним словом — для значка в трее и уведомлений.</summary>
    public Overall OverallState()
    {
        if (!IsOn)
            return Overall.Off;
        var looks = Enum.GetValues<Service>().Select(Look).ToList();
        if (Status.State == ZapretState.Searching || looks.Contains(ServiceLook.Checking))
            return Overall.Busy;
        return looks.Any(l => l is ServiceLook.NotWorking or ServiceLook.NeedsConnect) ? Overall.Problem : Overall.Ok;
    }

    /// <summary>Текст отчёта (вкладка «Журнал» → «Скопировать отчёт»). Ключа, секретов и адреса сервера в нём нет.</summary>
    public string BuildReport()
    {
        var s = Status;
        var sb = new StringBuilder();
        sb.AppendLine($"FiolKid {typeof(AppEngine).Assembly.GetName().Version}, {DateTime.Now:yyyy-MM-dd HH:mm}, {Environment.OSVersion}");
        sb.AppendLine($"Включено: {(IsOn ? "да" : "нет")}; состояние: {s.State}; стратегия: {s.StrategyId ?? "—"} ({s.StrategyNumber} из {s.StrategyCount})");
        sb.AppendLine($"VPN: {s.Vpn.State}, маршрутов {s.Vpn.RouteCount}, свой DNS: {(s.Vpn.SmartDns ? "да" : "нет")}, запасной канал: {string.Join(", ", s.Vpn.Fallback)}");
        sb.AppendLine($"VPN через: {s.Vpn.Profile ?? "—"}; {KeyState().Text}; {SubscriptionState().Text}");
        foreach (var service in Enum.GetValues<Service>())
            sb.AppendLine($"{service}: {Look(service)}{(s.LastReport?.Problems.TryGetValue(service, out var p) == true ? $" — {p}" : "")}");
        if (s.LastReport is { } r)
            sb.AppendLine($"Интернет: {(r.InternetOk ? "есть" : "нет")}; нейросети видят страну: {r.AiCountry ?? "—"}; " +
                          $"к прокси Telegram подключены: {(r.TelegramClients is null ? "?" : string.Join(", ", r.TelegramClients))}");
        if (s.LastError is not null)
            sb.AppendLine($"Ошибка: {s.LastError}");
        if (s.Vpn.Error is not null)
            sb.AppendLine($"VPN: {s.Vpn.Error}");
        sb.AppendLine("--- журнал ---");
        foreach (var line in Log)
            sb.AppendLine(line);
        if (WinwsOutput.Count > 0)
        {
            sb.AppendLine("--- вывод winws ---");
            foreach (var line in WinwsOutput)
                sb.AppendLine(line);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Отчёт плюс проверка сайтов так, как их открывает браузер (IPv6, адреса из DNS, маршруты VPN).
    /// Занимает несколько секунд.
    /// </summary>
    public async Task<string> BuildFullReportAsync()
    {
        var report = BuildReport();
        if (!IsOn || !Keys.HasKey)
            return report;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var sites = await SiteProbe.RunAsync(_vpn.CurrentRoutes, cts.Token);
            var marker = "--- журнал ---";
            var at = report.IndexOf(marker, StringComparison.Ordinal);
            var recent = _vpn.RecentDns;
            var block = $"--- сайты через VPN ---{Environment.NewLine}{sites}{Environment.NewLine}" +
                        (recent.Count == 0 ? "" : $"--- свой DNS: последние сайты в VPN ---{Environment.NewLine}{string.Join(Environment.NewLine, recent)}{Environment.NewLine}");
            return at < 0 ? report + block : report.Insert(at, block);
        }
        catch (OperationCanceledException)
        {
            return report + "--- сайты через VPN ---" + Environment.NewLine + "проверка не успела за 40 с" + Environment.NewLine;
        }
    }

    /// <summary>Версии того, что внутри, — для раздела «Обновления».</summary>
    public IReadOnlyList<(string Name, string Version)> ComponentVersions()
    {
        var list = new List<(string, string)>
        {
            ("Программа", typeof(AppEngine).Assembly.GetName().Version?.ToString(3) ?? "?"),
        };
        try
        {
            list.Add(("Способы обхода (zapret)", StrategyFile.Load(Path.Combine(AppPaths.ZapretDir, "strategies.json")).Source ?? "?"));
        }
        catch (InvalidDataException)
        {
            list.Add(("Способы обхода (zapret)", "не найдены"));
        }

        var versions = Path.Combine(AppPaths.InstallDir, "versions.txt");
        if (File.Exists(versions))
        {
            foreach (var line in File.ReadAllLines(versions))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2)
                    list.Add((parts[0].Trim(), parts[1].Trim()));
            }
        }

        var blocked = Path.Combine(AppPaths.DataDir, "lists", "blocked-domains.txt");
        list.Add(("Список заблокированных сайтов", File.Exists(blocked)
            ? $"обновлён {File.GetLastWriteTime(blocked):dd.MM.yyyy HH:mm}"
            : "встроенный"));
        return list;
    }

    private async Task RefreshListsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await _lists.RefreshIfStaleAsync(ct))
                {
                    Write("Список заблокированных сайтов обновлён");
                    _vpn.ReloadLists();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Write($"Не удалось сохранить список сайтов: {e.Message}");
            }
            await Task.Delay(ListRefreshInterval, ct);
        }
    }

    private void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (_log)
        {
            _log.Add(line);
            if (_log.Count > MaxLogLines)
                _log.RemoveRange(0, _log.Count - MaxLogLines);
        }
        LogAdded?.Invoke(line);
    }
}
