
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ZapretPro.Core.Health;

namespace ZapretPro.App;

/// <summary>
/// Вкладка «Журнал»: кнопки отчёта, итог подключения (подробная сводка — под раскрытием) и лента событий
/// с фильтрами и поиском. Фильтр только прячет строки: журнал и отчёт остаются полными.
/// </summary>
public partial class LogPage : UserControl
{
    private const int MaxEvents = 500;

    private AppEngine _engine = null!;
    private readonly List<LogEvent> _events = [];
    private readonly ObservableCollection<LogEvent> _shown = [];
    private HealthReport? _lastReport;
    private DateTime? _lastCheck;

    public LogPage()
    {
        InitializeComponent();
    }

    /// <summary>Сообщение для всплывающей подсказки главного окна.</summary>
    public event Action<string>? Toast;

    internal enum Kind { Info, Good, Warning, Error }

    /// <summary>
    /// Событие ленты: время, цветная точка, заголовок и подробности. Длинную строку журнала делим на
    /// заголовок (до первого «:» или «.») и подробности, сам текст не сокращаем.
    /// </summary>
    public sealed record LogEvent(string Line, string Time, string Title, string? Detail, Brush Mark, Brush TitleBrush)
    {
        internal Kind Kind { get; init; }
        public Visibility DetailVisibility => Detail is null ? Visibility.Collapsed : Visibility.Visible;
    }

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
        LogList.ItemsSource = _shown;
        foreach (var line in _engine.Log)
            _events.Add(ToEvent(line));
        ApplyFilter();

        _engine.StatusChanged += () => Dispatcher.BeginInvoke(Refresh);
        _engine.LogAdded += line => Dispatcher.BeginInvoke(() =>
        {
            var e = ToEvent(line);
            _events.Add(e);
            if (_events.Count > MaxEvents)
            {
                _shown.Remove(_events[0]);
                _events.RemoveAt(0);
            }
            if (Matches(e))
                _shown.Add(e);
            UpdateCount();
            ScrollToEnd();
        });
        Refresh();
    }

    private LogEvent ToEvent(string line)
    {
        // Строка журнала: «12:19:35  Текст»
        var time = line.Length > 10 && line[2] == ':' && line[5] == ':' ? line[..8] : "";
        var message = time.Length > 0 ? line[8..].TrimStart() : line;
        var (title, detail) = Split(message);
        var kind = ColorOf(line) switch
        {
            "RedBrush" => Kind.Error,
            "AmberBrush" => Kind.Warning,
            "GreenBrush" => Kind.Good,
            _ => Kind.Info,
        };
        var (mark, titleBrush) = kind switch
        {
            Kind.Error => ("RedBrush", "RedTextBrush"),
            Kind.Warning => ("AmberBrush", "AmberTextBrush"),
            Kind.Good => ("GreenBrush", "TextBrush"),
            _ => ("GrayBrush", "TextBrush"),
        };
        return new LogEvent(line, time, title, detail, (Brush)FindResource(mark), (Brush)FindResource(titleBrush)) { Kind = kind };
    }

    /// <summary>Короткая строка — целиком заголовок; длинная — заголовок до первого «: » или «. », остальное ниже.</summary>
    private static (string Title, string? Detail) Split(string message)
    {
        if (message.Length <= 56)
            return (message, null);
        var colon = message.IndexOf(": ", StringComparison.Ordinal);
        var dot = message.IndexOf(". ", StringComparison.Ordinal);
        var cut = new[] { colon, dot }.Where(x => x is >= 3 and <= 60).DefaultIfEmpty(-1).Min();
        if (cut < 0)
            return (message, null);
        var title = message[..cut];
        return (title, message[(cut + 2)..]);
    }

    private bool Matches(LogEvent e)
    {
        if (FilterErrors.IsChecked == true && e.Kind != Kind.Error)
            return false;
        if (FilterImportant.IsChecked == true && e.Kind is not (Kind.Error or Kind.Warning))
            return false;
        var query = SearchBox.Text.Trim();
        return query.Length == 0 || e.Line.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyFilter()
    {
        _shown.Clear();
        foreach (var e in _events.Where(Matches))
            _shown.Add(e);
        UpdateCount();
        ScrollToEnd();
    }

    private void UpdateCount()
    {
        EventCount.Text = $"{_shown.Count} / {_events.Count}";
        var empty = _shown.Count == 0;
        EmptyEvents.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        LogList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Filter_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_engine is not null)
            ApplyFilter();
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        FilterAll.IsChecked = true;
        SearchBox.Text = "";
        ApplyFilter();
    }

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

    /// <summary>Итог подключения сверху и подробная сводка под раскрытием (каждая строка своим цветом).</summary>
    public void Refresh()
    {
        var s = _engine.Status;
        RefreshSummary(s);
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

    private void RefreshSummary(WatchdogStatus s)
    {
        var looks = Enum.GetValues<Service>().ToDictionary(x => x, _engine.Look);
        var broken = looks.Where(x => x.Value == ServiceLook.NotWorking).Select(x => x.Key).ToList();
        var waiting = looks.Values.Count(l => l is ServiceLook.NeedsConnect or ServiceLook.NeedsKey);
        var checking = looks.Values.Any(l => l == ServiceLook.Checking);

        ConnectionTitle.Text = !_engine.IsOn ? "FiolKid выключен"
            : s.NeedsReboot ? "Нужна перезагрузка"
            : s.State == ZapretState.NoInternet ? "Нет интернета"
            : s.State == ZapretState.Searching ? "Подбираю способ…"
            : checking ? "Проверяю…"
            : broken.Count + waiting == 0 ? "FiolKid включён"
            : "Нужна проверка";

        var (badge, kind) = !_engine.IsOn ? ("Ожидание", Kind.Info)
            : s.NeedsReboot || s.State == ZapretState.NoInternet ? ("Сбой", Kind.Error)
            : checking || s.State == ZapretState.Searching ? ("Проверяю", Kind.Warning)
            : broken.Count == 1 ? ($"Сбой {Name(broken[0])}", Kind.Error)
            : broken.Count > 1 ? ("Есть сбои", Kind.Error)
            : waiting > 0 ? ("Нужно действие", Kind.Warning)
            : ("Всё в порядке", Kind.Good);
        BadgeText.Text = badge;
        var (dot, text, back) = kind switch
        {
            Kind.Good => ("GreenBrush", "GreenTextBrush", "#0CA4C7A4"),
            Kind.Warning => ("AmberBrush", "AmberTextBrush", "#09E3C398"),
            Kind.Error => ("RedBrush", "RedTextBrush", "#08E8ADA8"),
            _ => ("GrayBrush", "MutedBrush", "#07FFFFFF"),
        };
        BadgeDot.Fill = (Brush)FindResource(dot);
        BadgeText.Foreground = (Brush)FindResource(text);
        Badge.Background = new BrushConverter().ConvertFromString(back) as Brush;

        ServicePills.Children.Clear();
        foreach (var (service, look) in looks)
        {
            var pill = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            pill.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 4, Height = 4, VerticalAlignment = VerticalAlignment.Center,
                Fill = (Brush)FindResource(look switch
                {
                    ServiceLook.Working => "GreenBrush",
                    ServiceLook.NotWorking => "RedBrush",
                    ServiceLook.NeedsConnect or ServiceLook.NeedsKey or ServiceLook.Checking => "AmberBrush",
                    _ => "GrayBrush",
                }),
            });
            pill.Children.Add(new TextBlock
            {
                Text = Name(service), FontSize = 11, Margin = new Thickness(5, 0, 0, 1),
                Foreground = new SolidColorBrush(Color.FromRgb(0xB6, 0xB9, 0xC5)),
            });
            ServicePills.Children.Add(new Border
            {
                Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(4, 7, 4, 7), CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
                Background = new SolidColorBrush(Color.FromArgb(0x2B, 0x11, 0x16, 0x20)),
                Child = pill,
                ToolTip = s.LastReport?.Problems.GetValueOrDefault(service),
            });
        }

        // Время последней проверки — когда пришёл новый отчёт о проверке
        if (s.LastReport is not null && !ReferenceEquals(s.LastReport, _lastReport))
        {
            _lastReport = s.LastReport;
            _lastCheck = DateTime.Now;
        }
        LastCheckText.Text = _lastCheck is { } at ? $"Последняя проверка · {at:HH:mm:ss}" : "Проверки ещё не было";
    }

    private static string Name(Service service) => service == Service.Ai ? "VPN" : service.ToString();

    private void Add(string text, string brush)
    {
        if (DetailsText.Inlines.Count > 0)
            DetailsText.Inlines.Add(new System.Windows.Documents.LineBreak());
        DetailsText.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = (Brush)FindResource(brush) });
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        CopyButton.IsEnabled = false;
        CopyText.Text = "Собираю отчёт…";
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
            CopyText.Text = "Скопировать отчёт";
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
        if (_shown.Count > 0)
            LogList.ScrollIntoView(_shown[^1]);
    }
}
