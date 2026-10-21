using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZapretPro.Core.Health;
using ZapretPro.Core.Infrastructure;
using ZapretPro.Core.Key;
using ZapretPro.Core.Vpn;
using ZapretPro.Core.Zapret;

namespace ZapretPro.App;

/// <summary>Временное окно проверки этапов 2–3: zapret, прокси Telegram, VPN, перебор стратегий, журнал.</summary>
public partial class MainWindow : Window
{
    private const int MaxLogLines = 300;

    private readonly ZapretManager _zapret = new();
    private readonly TelegramProxy _telegram;
    private readonly KeyStore _keys = new();
    private readonly VpnController _vpn;
    private readonly Watchdog _watchdog;
    private CancellationTokenSource? _cts;
    private Task? _run;

    public MainWindow()
    {
        InitializeComponent();

        var settings = new SettingsStore();
        var secret = settings.Load().TelegramSecret;
        if (secret is null)
        {
            secret = TelegramProxy.NewSecret();
            settings.Update(s => s.TelegramSecret = secret);
        }
        _telegram = new TelegramProxy(secret);

        var listsDir = System.IO.Path.Combine(AppPaths.InstallDir, "vpn");
        _vpn = new VpnController(_keys, () => new Router(RouteLists.Load(listsDir)), new VpnManager());
        _vpn.Log += m => Dispatcher.BeginInvoke(() => AddLog(m));

        _watchdog = new Watchdog(_zapret, _telegram, _vpn, new HealthChecker(secret), settings);
        _watchdog.StatusChanged += s => Dispatcher.BeginInvoke(() => Show(s));
        _watchdog.Log += m => Dispatcher.BeginInvoke(() => AddLog(m));

        ShowKey();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = CheckButton.IsEnabled = TelegramButton.IsEnabled = true;
        AddLog("Запуск");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _run = Task.Run(async () =>
        {
            try
            {
                await _watchdog.RunAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await Dispatcher.BeginInvoke(() => AddLog($"ОШИБКА: {ex}"));
            }
        }, ct);
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = CheckButton.IsEnabled = TelegramButton.IsEnabled = false;
        await StopAsync();
        StartButton.IsEnabled = true;
        AddLog("Остановлено");
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await CheckNowAsync();

    private async Task CheckNowAsync()
    {
        CheckButton.IsEnabled = false;
        try
        {
            if (_cts is { IsCancellationRequested: false } cts)
                await Task.Run(() => _watchdog.CheckAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AddLog($"ОШИБКА: {ex.Message}");
        }
        finally
        {
            CheckButton.IsEnabled = StopButton.IsEnabled;
        }
    }

    private async void PasteKey_Click(object sender, RoutedEventArgs e)
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        try
        {
            // Сначала полная проверка (в том числе что это AmneziaWG), и только потом замена прежнего ключа
            AwgConfig.FromKey(KeyParser.Parse(text));
            var parsed = _keys.Save(text);
            AddLog($"Ключ сохранён: {VpnProtocolInfo.DisplayName(parsed.Protocol)}");
            // Ключ с буфера обмена убираем, чтобы не остался там случайно
            Clipboard.Clear();
        }
        catch (KeyParseException ex)
        {
            AddLog($"Ключ не принят: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Ключ не подходит", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        ShowKey();
        if (_run is not null)
            await CheckNowAsync();
    }

    private async void DeleteKey_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Удалить ключ VPN? Нейросети перестанут работать, пока не вставишь новый.",
                "Удалить ключ", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _keys.Delete();
        AddLog("Ключ удалён");
        ShowKey();
        if (_run is not null)
            await CheckNowAsync();
    }

    private void Telegram_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _telegram.OpenInTelegram();
            AddLog($"Открыта ссылка для Telegram: tg://proxy?server={TelegramProxy.Host}&port={TelegramProxy.Port}&secret=…");
        }
        catch (Exception ex)
        {
            AddLog($"Не удалось открыть Telegram: {ex.Message}");
        }
    }

    /// <summary>Журнал и состояние текстом — чтобы прислать. Ключа и секретов в журнале нет.</summary>
    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Одна кнопка {typeof(MainWindow).Assembly.GetName().Version}, {DateTime.Now:yyyy-MM-dd HH:mm}, {Environment.OSVersion}");
        sb.AppendLine($"Состояние: {StateText.Text}; стратегия: {StrategyText.Text}; VPN: {VpnText.Text}");
        sb.AppendLine($"YouTube: {YouTubeText.Text}; Discord: {DiscordText.Text}; Telegram: {TelegramText.Text}; нейросети: {AiText.Text}; интернет: {InternetText.Text}");
        if (ErrorText.Visibility == Visibility.Visible)
            sb.AppendLine($"Ошибка: {ErrorText.Text}");
        sb.AppendLine("--- журнал ---");
        foreach (var item in LogList.Items)
            sb.AppendLine(item?.ToString());
        if (_zapret.RecentOutput.Count > 0)
        {
            sb.AppendLine("--- вывод winws ---");
            foreach (var line in _zapret.RecentOutput)
                sb.AppendLine(line);
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            AddLog("Журнал скопирован — вставь его в сообщение (Ctrl+V)");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            AddLog($"Не удалось скопировать: {ex.Message}");
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_run is null)
            return;

        // Дожидаемся остановки, чтобы winws и VPN не остались работать без присмотра
        e.Cancel = true;
        Hide();
        await StopAsync();
        _run = null;
        Close();
    }

    private async Task StopAsync()
    {
        _cts?.Cancel();
        if (_run is not null)
            await _run;
        _run = null;
        try
        {
            await _watchdog.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            AddLog($"Ошибка при остановке: {ex.Message}");
        }
    }

    private void ShowKey()
    {
        var key = _keys.Load();
        if (key is null)
        {
            KeyText.Text = _keys.HasKey ? "Сохранённый ключ не читается — вставь заново" : "Ключа нет — нейросети работать не будут";
            DeleteKeyButton.IsEnabled = _keys.HasKey;
            return;
        }

        DeleteKeyButton.IsEnabled = true;
        try
        {
            var parsed = KeyParser.Parse(key);
            KeyText.Text = parsed.Protocol == VpnProtocol.AmneziaWg
                ? $"Ключ подходит: {VpnProtocolInfo.DisplayName(parsed.Protocol)}"
                : $"Ключ не подходит: {parsed.UnsupportedReason ?? "нужен ключ AmneziaWG"}";
        }
        catch (KeyParseException ex)
        {
            KeyText.Text = $"Ключ не подходит: {ex.Message}";
        }
    }

    private void Show(WatchdogStatus s)
    {
        StateText.Text = s.State switch
        {
            ZapretState.Stopped => "остановлено",
            ZapretState.Searching => "подбираю лучший способ…",
            ZapretState.Working => "всё работает",
            ZapretState.Partial => "работает, часть — через запасной канал",
            ZapretState.Failed => "обход не помог, YouTube и Discord — через запасной канал",
            ZapretState.NoInternet => "нет интернета",
            _ => s.State.ToString(),
        };
        StrategyText.Text = s.StrategyId is null ? "—" : $"{s.StrategyId} ({s.StrategyNumber} из {s.StrategyCount})";

        VpnText.Text = s.Vpn.State switch
        {
            VpnState.NoKey => "нет ключа",
            VpnState.BadKey => "ключ не подходит",
            VpnState.Stopped => "выключен",
            VpnState.Connecting => "подключаюсь…",
            VpnState.Connected => $"подключён, маршрутов: {s.Vpn.RouteCount}"
                                  + (s.Vpn.Fallback.Count > 0 ? $", запасной канал: {string.Join(", ", s.Vpn.Fallback)}" : ""),
            VpnState.ServerNotResponding => "сервер не отвечает",
            VpnState.Failed => "ошибка",
            _ => s.Vpn.State.ToString(),
        };

        SetService(YouTubeText, s, Service.YouTube);
        SetService(DiscordText, s, Service.Discord);
        SetService(TelegramText, s, Service.Telegram, s.TelegramRunning ? null : "прокси не запущен");
        SetService(AiText, s, Service.Ai,
            s.LastReport?.AiCountry is { } country ? $"видят из страны {country}" : s.Vpn.State == VpnState.NoKey ? "нет ключа" : null);
        InternetText.Text = s.LastReport is null ? "—" : s.LastReport.InternetOk ? "есть" : "нет";

        var error = s.LastError ?? (s.Vpn.State is VpnState.NoKey ? null : s.Vpn.Error);
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void SetService(TextBlock text, WatchdogStatus s, Service service, string? note = null)
    {
        var report = s.LastReport;
        if (report is null || !report.Services.ContainsKey(service))
        {
            text.Text = note ?? "—";
            text.Foreground = Brushes.Gray;
            return;
        }

        var ok = report[service];
        var notes = new List<string>();
        if (s.ViaVpn.Contains(service))
            notes.Add("через VPN");
        if (note is not null)
            notes.Add(note);
        text.Text = (ok ? "● работает" : "● не работает") + (notes.Count > 0 ? $" ({string.Join(", ", notes)})" : "");
        text.Foreground = ok ? Brushes.ForestGreen : Brushes.Firebrick;
    }

    private void AddLog(string message)
    {
        LogList.Items.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        while (LogList.Items.Count > MaxLogLines)
            LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
