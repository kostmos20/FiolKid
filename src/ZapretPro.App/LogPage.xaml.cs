
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZapretPro.Core.Health;

namespace ZapretPro.App;

/// <summary>Вкладка «Журнал»: подробное состояние, журнал и «Скопировать отчёт».</summary>
public partial class LogPage : UserControl
{
    private AppEngine _engine = null!;

    public LogPage()
    {
        InitializeComponent();
    }

    /// <summary>Сообщение для всплывающей подсказки главного окна.</summary>
    public event Action<string>? Toast;

    /// <summary>Строка журнала с цветом: ошибки — красные, предупреждения — янтарные, успехи — зелёные.</summary>
    public sealed record LogLine(string Text, Brush Brush);

    // Слова, по которым видно, что строка про беду, про «внимание» или про успех. Проверяются в этом порядке
    private static readonly string[] Bad =
    [
        "ОШИБКА", "Ошибка", "не запустил", "Не удалось", "не удалось", "завис", "перезагрузка", "Не прошло", "не работает",
        "не помогла", "не получился", "не скачалась", "не включился", "Нет интернета", "не отвечает", "Не открывается",
    ];
    private static readonly string[] Warn =
    [
        "Проверка не прошла", "запасной", "перебоями", "не ответил", "Прошёл час", "Включены другие VPN", "оставляю",
        "Перезапускаю", "Замеряю", "пробую",
    ];
    private static readonly string[] Good =
    [
        "Все проверки прошли", "Самый быстрый", "Выбран VPN", "Свой DNS включён", "сохранён", "сохранена", "обновлён",
        "Включено", "DNS возвращён", "Замер серверов: отвечают", "DNS переключён",
    ];

    private static readonly System.Text.RegularExpressions.Regex Opened = new(@"открылось (\d+) из (\d+)");

    public void Init(AppEngine engine)
    {
        _engine = engine;
        foreach (var line in _engine.Log)
            LogList.Items.Add(Colorize(line));
        ScrollToEnd();

        _engine.StatusChanged += () => Dispatcher.BeginInvoke(Refresh);
        _engine.LogAdded += line => Dispatcher.BeginInvoke(() =>
        {
            LogList.Items.Add(Colorize(line));
            while (LogList.Items.Count > 500)
                LogList.Items.RemoveAt(0);
            ScrollToEnd();
        });
        Refresh();
    }

    private LogLine Colorize(string line) => new(line, (Brush)FindResource(ColorOf(line)));

    /// <summary>Красный — беда, янтарный — внимание, зелёный — всё хорошо, серый — просто сведения.</summary>
    internal static string ColorOf(string line)
    {
        // «открылось 2 из 2» — успех, «0 из 2» — провал, остальное — частично
        if (Opened.Match(line) is { Success: true } m)
        {
            var (ok, total) = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            return ok == total && total > 0 ? "GreenBrush" : ok == 0 ? "RedBrush" : "AmberBrush";
        }
        return Bad.Any(line.Contains) ? "RedBrush"
            : Warn.Any(line.Contains) ? "AmberBrush"
            : Good.Any(line.Contains) ? "GreenBrush"
            : "MutedBrush";
    }

    /// <summary>Сводка сверху: каждая строка своим цветом, чтобы с одного взгляда было видно, что не так.</summary>
    public void Refresh()
    {
        var s = _engine.Status;
        DetailsText.Inlines.Clear();
        Add($"Включено: {(_engine.IsOn ? "да" : "нет")}   zapret: {s.State}   альт: {s.StrategyId ?? "—"} ({s.StrategyNumber} из {s.StrategyCount})",
            !_engine.IsOn ? "MutedBrush" : s.State switch
            {
                ZapretState.Working => "GreenBrush",
                ZapretState.Failed or ZapretState.NoInternet => "RedBrush",
                _ => "AmberBrush",
            });
        Add($"VPN: {s.Vpn.State}{(s.Vpn.Profile is null ? "" : $" ({s.Vpn.Profile})")}, маршрутов {s.Vpn.RouteCount}, свой DNS: {(s.Vpn.SmartDns ? "да" : "нет")}" +
            $"{(s.Vpn.Fallback.Count == 0 ? "" : $", запасной канал: {string.Join(", ", s.Vpn.Fallback)}")}",
            !_engine.IsOn ? "MutedBrush" : s.Vpn.State switch
            {
                Core.Vpn.VpnState.Connected => "GreenBrush",
                Core.Vpn.VpnState.Connecting or Core.Vpn.VpnState.NoKey => "AmberBrush",
                _ => "RedBrush",
            });
        foreach (var service in Enum.GetValues<Service>())
        {
            var problem = s.LastReport?.Problems.GetValueOrDefault(service);
            var via = s.ViaVpn.Contains(service) ? " [VPN]" : "";
            var look = _engine.Look(service);
            Add($"{service,-9} {look}{via}{(problem is null ? "" : $" — {problem}")}", look switch
            {
                ServiceLook.Working => "GreenBrush",
                ServiceLook.NotWorking => "RedBrush",
                ServiceLook.NeedsConnect or ServiceLook.NeedsKey or ServiceLook.Checking => "AmberBrush",
                _ => "MutedBrush",
            });
        }
        if (s.LastReport is { } r)
            Add($"Интернет: {(r.InternetOk ? "есть" : "нет")}   ChatGPT видит: {r.AiCountry ?? "—"} ({r.AiExitIp ?? "—"})   " +
                $"к прокси Telegram: {(r.TelegramClients is null ? "?" : r.TelegramClients.Count == 0 ? (r.TelegramAppRunning ? "никто" : "Telegram закрыт") : string.Join(", ", r.TelegramClients))}",
                r.InternetOk ? "TextBrush" : "RedBrush");
        if (s.LastError is not null)
            Add($"Ошибка: {s.LastError}", "RedBrush");
        if (s.Vpn.Error is not null)
            Add($"VPN: {s.Vpn.Error}", "RedBrush");
    }

    private void Add(string text, string brush)
    {
        if (DetailsText.Inlines.Count > 0)
            DetailsText.Inlines.Add(new System.Windows.Documents.LineBreak());
        DetailsText.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = (Brush)FindResource(brush) });
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        CopyButton.IsEnabled = false;
        CopyButton.Content = "Собираю отчёт…";
        try
        {
            var report = await _engine.BuildFullReportAsync();
            Clipboard.SetText(report);
            Toast?.Invoke("Отчёт скопирован — вставь его в сообщение (Ctrl+V)");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Toast?.Invoke("Не получилось скопировать — нажми «Сохранить файлом»");
        }
        finally
        {
            CopyButton.Content = "Скопировать отчёт";
            CopyButton.IsEnabled = true;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            var path = await _engine.SaveReportAsync();
            Toast?.Invoke($"Отчёт на рабочем столе: {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Toast?.Invoke($"Не удалось сохранить: {ex.Message}");
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        await _engine.CheckNowAsync();
        CheckButton.IsEnabled = true;
    }

    private void ScrollToEnd()
    {
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }
}
