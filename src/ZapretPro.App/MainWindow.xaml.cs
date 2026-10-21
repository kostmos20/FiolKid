using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using ZapretPro.Core.Health;
using ZapretPro.Core.Infrastructure;
using ZapretPro.Core.Zapret;

namespace ZapretPro.App;

/// <summary>Временное окно проверки этапа 2: запуск zapret и прокси Telegram, перебор стратегий, журнал.</summary>
public partial class MainWindow : Window
{
    private const int MaxLogLines = 300;

    private readonly ZapretManager _zapret = new();
    private readonly TelegramProxy _telegram;
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

        _watchdog = new Watchdog(_zapret, _telegram, new HealthChecker(), settings);
        _watchdog.StatusChanged += s => Dispatcher.BeginInvoke(() => Show(s));
        _watchdog.Log += m => Dispatcher.BeginInvoke(() => AddLog(m));
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

    private async void Check_Click(object sender, RoutedEventArgs e)
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
        finally
        {
            CheckButton.IsEnabled = StopButton.IsEnabled;
        }
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

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_run is null)
            return;

        // Дожидаемся остановки, чтобы winws не остался работать без присмотра
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
        _watchdog.Stop();
    }

    private void Show(WatchdogStatus s)
    {
        StateText.Text = s.State switch
        {
            ZapretState.Stopped => "остановлено",
            ZapretState.Searching => "подбираю лучший способ…",
            ZapretState.Working => "всё работает",
            ZapretState.Partial => "работает частично",
            ZapretState.Failed => "обход не помог (запасной канал — этап 3)",
            ZapretState.NoInternet => "нет интернета",
            _ => s.State.ToString(),
        };
        StrategyText.Text = s.StrategyId is null ? "—" : $"{s.StrategyId} ({s.StrategyNumber} из {s.StrategyCount})";

        SetService(YouTubeText, s.LastReport, Service.YouTube);
        SetService(DiscordText, s.LastReport, Service.Discord);
        SetService(TelegramText, s.LastReport, Service.Telegram, s.TelegramRunning ? null : "прокси не запущен");
        InternetText.Text = s.LastReport is null ? "—" : s.LastReport.InternetOk ? "есть" : "нет";

        ErrorText.Text = s.LastError ?? "";
        ErrorText.Visibility = s.LastError is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void SetService(System.Windows.Controls.TextBlock text, HealthReport? report, Service service, string? note = null)
    {
        if (report is null || !report.Services.ContainsKey(service))
        {
            text.Text = note ?? "—";
            text.Foreground = Brushes.Gray;
            return;
        }
        var ok = report[service];
        text.Text = (ok ? "● работает" : "● не работает") + (note is null ? "" : $" ({note})");
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
