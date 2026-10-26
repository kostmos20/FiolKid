using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZapretPro.Core.Health;
using ZapretPro.Core.Infrastructure;
using ZapretPro.Core.Key;
using ZapretPro.Core.Vpn;

namespace ZapretPro.App;

/// <summary>
/// Вкладка «Настройки»: ключ Amnezia и подписка (каждый своей кнопкой — чтобы ключи разных сервисов не
/// путались), выбор сервера и способа обхода, Telegram, запуск с Windows, отчёты. Простым языком, крупно.
/// </summary>
public partial class SettingsPage : UserControl
{
    private AppEngine _engine = null!;
    private bool _confirmDelete;
    private bool _confirmDeleteSubscription;
    private bool _updating;
    private bool _measuringServers;
    private bool? _autostart;
    private string? _strategiesShown;
    private string? _serversShown;

    public SettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>Сообщение для всплывающей подсказки главного окна.</summary>
    public event Action<string>? Toast;

    /// <summary>Строка списка альтов или серверов.</summary>
    public sealed record Row(string Key, string Title, string? Note, string Right, Brush RightBrush)
    {
        public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Init(AppEngine engine)
    {
        _engine = engine;
        _engine.StatusChanged += () => Dispatcher.BeginInvoke(Refresh);
        LogPathText.Text = $"Журнал за последние 7 дней хранится в файлах: {_engine.LogFolder}";
        Refresh();
    }

    public void Refresh()
    {
        _updating = true;
        try
        {
            RefreshCredentials();
            RefreshVpn();
            RefreshServers();
            RefreshStrategies();
            RefreshTelegram();
            NewYearSwitch.IsChecked = _engine.Settings.NewYearTheme != false;
            _autostart ??= _engine.AutostartEnabled;
            AutostartSwitch.IsChecked = _autostart;
            RefreshUpdates();
            // Привязка WPF видит только свойства, а у кортежа — поля
            VersionsList.ItemsSource = _engine.ComponentVersions().Select(v => new { Item1 = v.Name, Item2 = v.Version }).ToList();
        }
        finally
        {
            _updating = false;
        }
    }

    // ---------- Ключ и подписка ----------

    private void RefreshCredentials()
    {
        var (keyOk, keyText) = _engine.KeyState();
        KeyText.Text = keyText;
        KeyDot.Fill = Brush(keyOk ? "GreenBrush" : _engine.Keys.HasKey ? "RedBrush" : "GrayBrush");
        DeleteKeyButton.Visibility = _engine.Keys.HasKey ? Visibility.Visible : Visibility.Collapsed;
        if (!_confirmDelete)
            DeleteKeyButton.Content = "Удалить";
        PasteKeyButton.Content = _engine.Keys.HasKey ? "Заменить ключ" : "Вставить ключ";

        var (subOk, subText) = _engine.SubscriptionState();
        SubscriptionText.Text = subText;
        SubscriptionDot.Fill = Brush(subOk ? "GreenBrush" : _engine.Subscriptions.HasSubscription ? "RedBrush" : "GrayBrush");
        DeleteSubscriptionButton.Visibility = _engine.Subscriptions.HasSubscription ? Visibility.Visible : Visibility.Collapsed;
        if (!_confirmDeleteSubscription)
            DeleteSubscriptionButton.Content = "Удалить";
        PasteSubscriptionButton.Content = _engine.Subscriptions.HasSubscription ? "Заменить ссылку" : "Вставить ссылку";
    }

    private async void PasteKey_Click(object sender, RoutedEventArgs e) =>
        await PasteAsync(PasteKeyButton, CredentialKind.Amnezia, "Сначала скопируй ключ (vpn://…), потом нажми эту кнопку");

    private async void PasteSubscription_Click(object sender, RoutedEventArgs e) =>
        await PasteAsync(PasteSubscriptionButton, CredentialKind.Subscription, "Сначала скопируй ссылку на подписку (https://…), потом нажми эту кнопку");

    private async Task PasteAsync(Button button, CredentialKind kind, string empty)
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (string.IsNullOrWhiteSpace(text))
        {
            Toast?.Invoke(empty);
            return;
        }
        var label = button.Content;
        button.IsEnabled = false;
        button.Content = "Проверяю…";
        try
        {
            Toast?.Invoke(await _engine.SaveKeyAsync(text, kind));
            Clipboard.Clear();
        }
        catch (KeyParseException ex)
        {
            Toast?.Invoke(ex.Message);
        }
        finally
        {
            button.IsEnabled = true;
            button.Content = label;
        }
        _serversShown = null;
        Refresh();
    }

    /// <summary>Удаление — в два нажатия, чтобы не стереть подписку случайно.</summary>
    private void DeleteSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (!_confirmDeleteSubscription)
        {
            _confirmDeleteSubscription = true;
            DeleteSubscriptionButton.Content = "Точно удалить?";
            return;
        }
        _confirmDeleteSubscription = false;
        _engine.DeleteSubscription();
        Toast?.Invoke("Подписка удалена");
        Refresh();
    }

    /// <summary>Удаление — в два нажатия, чтобы не стереть ключ случайно.</summary>
    private void DeleteKey_Click(object sender, RoutedEventArgs e)
    {
        if (!_confirmDelete)
        {
            _confirmDelete = true;
            DeleteKeyButton.Content = "Точно удалить?";
            return;
        }
        _confirmDelete = false;
        _engine.DeleteKey();
        Toast?.Invoke("Ключ удалён");
        Refresh();
    }

    // ---------- Серверы подписки ----------

    private void RefreshServers()
    {
        var servers = _engine.Servers();
        ServersPanel.Visibility = servers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (servers.Count == 0)
            return;

        var settings = _engine.Settings;
        var auto = settings.ServerAutoSelect != false;
        ServerAutoSwitch.IsChecked = auto;
        var chosen = _engine.ChosenServers;
        var measuring = _measuringServers || _engine.MeasuringServers;
        ServersHint.Text = measuring
            ? "Замеряю: задержку, скорость скачивания и страну выхода каждого сервера…"
            : auto
                ? (chosen.Count > 0 ? $"Сейчас через: {string.Join(", ", chosen)}. " : "") +
                  "Перед подключением программа меряет все серверы и берёт самые быстрые. Серверы с выходом из России для нейросетей не берёт."
                : "Подключаюсь только к выбранному серверу. Нажми на другой, чтобы сменить.";
        MeasureServersButton.IsEnabled = !measuring;
        MeasureServersButton.Content = measuring ? "Замеряю…" : "Замерить серверы";

        var rows = servers.Select(x => new Row(
            x.Server.Key,
            x.Server.Name,
            $"{x.Server.Protocol}{(chosen.Contains(x.Server.Name) ? " · подключено" : "")}",
            ScoreText(x.Score),
            ScoreBrush(x.Score))).ToList();
        var signature = string.Join("|", rows.Select(r => $"{r.Key}{r.Note}{r.Right}"));
        if (signature != _serversShown)
        {
            _serversShown = signature;
            ServersList.ItemsSource = rows;
        }
        ServersList.SelectedItem = auto ? null : rows.FirstOrDefault(r => r.Key == settings.ManualServerKey);
    }

    private static string ScoreText(ServerScore? score) => score switch
    {
        null => "не замерен",
        { Works: false } => score.Problem ?? "не отвечает",
        _ => string.Join(" · ", new[]
        {
            $"{score.PingMs} мс",
            score.Mbps is { } m ? $"{m} Мбит/с" : null,
            score.Country,
        }.OfType<string>()),
    };

    private Brush ScoreBrush(ServerScore? score) => Brush(score switch
    {
        null => "MutedBrush",
        { Works: false } => "RedBrush",
        { BadCountry: true } => "AmberBrush",
        _ => "GreenBrush",
    });

    private void ServerAuto_Click(object sender, RoutedEventArgs e)
    {
        var auto = ServerAutoSwitch.IsChecked == true;
        var manual = _engine.Settings.ManualServerKey ?? _engine.Servers().FirstOrDefault(s => s.Score?.Works == true).Server?.Key;
        _engine.SetServerChoice(auto, auto ? null : manual);
        Toast?.Invoke(auto ? "Буду сам выбирать самый быстрый сервер" : "Выбери сервер в списке");
        Refresh();
    }

    private void ServersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || ServersList.SelectedItem is not Row row)
            return;
        _engine.SetServerChoice(false, row.Key);
        Toast?.Invoke($"Подключаюсь через «{row.Title}»");
        Refresh();
    }

    private async void MeasureServers_Click(object sender, RoutedEventArgs e)
    {
        _measuringServers = true;
        Refresh();
        try
        {
            await _engine.MeasureServersAsync();
            Toast?.Invoke("Замер серверов готов");
        }
        finally
        {
            _measuringServers = false;
            _serversShown = null;
            Refresh();
        }
    }

    // ---------- Альты zapret ----------

    private void RefreshStrategies()
    {
        var settings = _engine.Settings;
        var mode = settings.EffectiveStrategyMode;
        ModeFirst.IsChecked = mode == StrategyModes.FirstWorking;
        ModeFastest.IsChecked = mode == StrategyModes.Fastest;
        ModeManual.IsChecked = mode == StrategyModes.Manual;
        var status = _engine.Status;
        ModeHint.Text = mode switch
        {
            StrategyModes.Fastest => settings.StrategyBenchmarkAt is { } at
                ? $"Программа замерила все альты {at.ToLocalTime():dd.MM в HH:mm} и взяла самый быстрый. Замер повторяется раз в неделю и если альт перестал работать."
                : "При включении программа замерит все альты (пару минут) и возьмёт самый быстрый.",
            StrategyModes.Manual => "Работает только выбранный альт. Если с ним что-то не откроется — это пойдёт через VPN.",
            _ => "Программа пробует альты по очереди и оставляет первый, при котором всё открывается.",
        };

        var bench = (settings.StrategyBenchmark ?? []).ToDictionary(b => b.Id);
        // Самый быстрый — итог подбора (в таблице рядом быстрый отсев и точный замер, их числа не сравнить)
        var fastest = bench.Count > 0 ? settings.LastStrategyId : null;
        var rows = _engine.StrategyIds.Select(id =>
        {
            var notes = new List<string>();
            if (_engine.IsOn && status.StrategyId == id)
                notes.Add(status.Measuring ? "замеряю…" : "работает сейчас");
            if (id == fastest)
                notes.Add("самый быстрый");
            var b = bench.GetValueOrDefault(id);
            var right = b switch
            {
                null => "",
                { Milliseconds: -2 } => "не проверялся",
                { Milliseconds: < 0 } => "не запустился",
                _ => $"{b.Ok} из {b.Total} · {b.Milliseconds / 1000:0.0} с",
            };
            var brush = b is null || b.Milliseconds == -2 ? "MutedBrush" : b.Ok == b.Total ? "GreenBrush" : b.Ok > 0 ? "AmberBrush" : "RedBrush";
            return new Row(id, id, string.Join(" · ", notes), right, Brush(brush));
        }).ToList();
        var signature = string.Join("|", rows.Select(r => $"{r.Key}{r.Note}{r.Right}"));
        if (signature != _strategiesShown)
        {
            _strategiesShown = signature;
            StrategiesList.ItemsSource = rows;
        }
        var selected = mode == StrategyModes.Manual ? settings.ManualStrategyId : _engine.IsOn ? status.StrategyId : null;
        StrategiesList.SelectedItem = rows.FirstOrDefault(r => r.Key == selected);

        BenchmarkButton.IsEnabled = _engine.IsOn && !status.Measuring;
        BenchmarkButton.Content = status.Measuring ? $"Замеряю… {status.StrategyNumber} из {status.StrategyCount}"
            : _engine.IsOn ? "Замерить скорость альтов" : "Замерить скорость (сначала включи программу)";
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        var mode = sender == ModeFastest ? StrategyModes.Fastest : sender == ModeManual ? StrategyModes.Manual : StrategyModes.FirstWorking;
        var manual = mode == StrategyModes.Manual
            ? _engine.Settings.ManualStrategyId ?? _engine.Status.StrategyId ?? _engine.StrategyIds.FirstOrDefault()
            : null;
        _engine.SetStrategyMode(mode, manual);
        Toast?.Invoke(mode switch
        {
            StrategyModes.Fastest => "Буду брать самый быстрый альт",
            StrategyModes.Manual => "Выбери альт в списке",
            _ => "Буду брать первый рабочий альт",
        });
        Refresh();
    }

    /// <summary>Нажали на альт — значит, хотят именно его: включаем ручной режим с ним.</summary>
    private void StrategiesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || StrategiesList.SelectedItem is not Row row)
            return;
        var settings = _engine.Settings;
        if (settings.StrategyMode == StrategyModes.Manual && settings.ManualStrategyId == row.Key)
            return;
        _engine.SetStrategyMode(StrategyModes.Manual, row.Key);
        Toast?.Invoke($"Включаю альт «{row.Key}»");
        Refresh();
    }

    private async void Benchmark_Click(object sender, RoutedEventArgs e)
    {
        BenchmarkButton.IsEnabled = false;
        Toast?.Invoke("Замеряю все альты — пару минут YouTube и Discord могут подтормаживать");
        await _engine.BenchmarkStrategiesAsync();
        Refresh();
    }

    // ---------- Обновления ----------

    private void RefreshUpdates()
    {
        var update = _engine.Update;
        UpdateStatusText.Text = $"Версия {typeof(SettingsPage).Assembly.GetName().Version?.ToString(3)}. {_engine.UpdateStatus}";
        UpdateNotesText.Text = update?.Notes is { Length: > 0 } notes ? $"Что нового: {notes}" : "";
        UpdateNotesText.Visibility = string.IsNullOrEmpty(UpdateNotesText.Text) ? Visibility.Collapsed : Visibility.Visible;
        InstallUpdateButton.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
        InstallUpdateButton.Content = update is null ? "Обновить" : $"Обновить до {update.Version}";
        InstallUpdateButton.IsEnabled = !_engine.UpdateBusy;
        CheckUpdateButton.IsEnabled = !_engine.UpdateBusy;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        await _engine.CheckUpdatesAsync();
        Toast?.Invoke(_engine.UpdateStatus);
        Refresh();
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow main)
            await main.InstallUpdateAsync();
    }

    // ---------- VPN, Telegram, запуск ----------

    private void RefreshVpn()
    {
        var vpn = _engine.Status.Vpn;
        var (text, color) = !_engine.IsOn
            ? ("Выключено — включи программу на главной", "GrayBrush")
            : vpn.State switch
            {
                VpnState.Connected => ($"Подключено: {vpn.Profile}", "GreenBrush"),
                VpnState.Connecting => ($"Подключаюсь: {vpn.Profile}…", "AmberBrush"),
                VpnState.NoKey => ("Нет ключа", "AmberBrush"),
                VpnState.BadKey => ("Ключ не подходит", "RedBrush"),
                VpnState.ServerNotResponding => ("Сервер не отвечает", "RedBrush"),
                VpnState.Failed => ("Не подключается", "RedBrush"),
                _ => ("Подключаюсь…", "AmberBrush"),
            };
        VpnText.Text = text;
        VpnDot.Fill = Brush(color);
        var hint = _engine.IsOn && vpn.State is not (VpnState.Connected or VpnState.NoKey) ? vpn.Error : null;
        VpnHint.Text = hint;
        VpnHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        ReconnectButton.IsEnabled = _engine.IsOn;
    }

    private void RefreshTelegram()
    {
        var clients = _engine.Status.LastReport?.TelegramClients;
        TelegramText.Text = !_engine.IsOn
            ? "Программа выключена — прокси Telegram не работает"
            : _engine.Look(Service.Telegram) switch
            {
                ServiceLook.Working => $"Работает. Подключены: {string.Join(", ", clients ?? [])}",
                ServiceLook.NeedsConnect => "Прокси готов, но Telegram к нему не подключён — нажми «Подключить Telegram»",
                ServiceLook.AppClosed => "Прокси готов. Telegram сейчас закрыт — после запуска он подключится сам (если уже подключали)",
                ServiceLook.NotWorking => "Прокси не работает — нажми «Перезапустить прокси»",
                _ => "Проверяю…",
            };
        RestartProxyButton.IsEnabled = _engine.IsOn;
    }

    private void ConnectTelegram_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _engine.Telegram.OpenInTelegram();
            Toast?.Invoke("Telegram спросит «Подключить прокси?» — нажми «Подключить»");
            _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => _engine.CheckNowAsync());
        }
        catch (Exception ex)
        {
            Toast?.Invoke($"Не удалось открыть Telegram: {ex.Message}");
        }
    }

    private async void RestartProxy_Click(object sender, RoutedEventArgs e)
    {
        RestartProxyButton.IsEnabled = false;
        Toast?.Invoke("Перезапускаю прокси Telegram…");
        await _engine.RestartTelegramAsync();
        Toast?.Invoke(_engine.Status.TelegramRunning ? "Прокси перезапущен" : "Прокси не запустился — пришли отчёт из «Журнала»");
        Refresh();
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        ReconnectButton.IsEnabled = false;
        VpnText.Text = "Проверяю…";
        await _engine.CheckNowAsync();
        Refresh();
    }

    private void NewYear_Click(object sender, RoutedEventArgs e)
    {
        var on = NewYearSwitch.IsChecked == true;
        _engine.SetNewYearTheme(on);
        if (Window.GetWindow(this) is MainWindow main)
            main.ApplyNewYear(on);
        Toast?.Invoke(on ? "С наступающим! Снег пошёл" : "Новогоднее оформление выключено");
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        var enable = AutostartSwitch.IsChecked == true;
        var error = _engine.SetAutostart(enable);
        _autostart = _engine.AutostartEnabled;
        Toast?.Invoke(error is not null ? $"Не получилось: {error}"
            : enable ? "Готово: программа будет запускаться вместе с Windows" : "Больше не запускается вместе с Windows");
        Refresh();
    }

    private async void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        SaveReportButton.IsEnabled = false;
        SaveReportButton.Content = "Собираю отчёт…";
        try
        {
            var path = await _engine.SaveReportAsync();
            Toast?.Invoke($"Отчёт сохранён на рабочий стол: {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Toast?.Invoke($"Не удалось сохранить: {ex.Message}");
        }
        finally
        {
            SaveReportButton.IsEnabled = true;
            SaveReportButton.Content = "Сохранить отчёт на рабочий стол";
        }
    }

    private Brush Brush(string key) => (Brush)FindResource(key);
}
