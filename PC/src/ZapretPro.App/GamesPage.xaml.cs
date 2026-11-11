using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ZapretPro.Core.Games;

namespace ZapretPro.App;

/// <summary>
/// Вкладка «Игры»: игровой режим (пока идёт любая игра — ничего не переключать) и список игр, которые идут
/// через VPN. Запускать игры через FiolKid не нужно — программа замечает их сама.
/// </summary>
public partial class GamesPage : UserControl
{
    private AppEngine _engine = null!;
    private bool _updating;
    private string? _shown;
    private string? _confirmRemove;

    public GamesPage()
    {
        InitializeComponent();
    }

    /// <summary>Сообщение для всплывающей подсказки главного окна.</summary>
    public event Action<string>? Toast;

    public void Init(AppEngine engine)
    {
        _engine = engine;
        _engine.StatusChanged += () => Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    public void Refresh()
    {
        if (_engine is null)
            return;
        _updating = true;
        try
        {
            GameModeSwitch.IsChecked = _engine.GameMode;
            if (_engine.GameModeText is { } text)
                SetMode(text, "GreenBrush");
            else if (!_engine.GameMode)
                SetMode("Выключен — во время игр программа может переключать стратегии и серверы", "GrayBrush");
            else
                SetMode("Включён. Сейчас игры нет — работаю как обычно", "AmberBrush");

            VpnWarning.Visibility = _engine.Games.Count > 0 && !_engine.HasVpnCredentials ? Visibility.Visible : Visibility.Collapsed;
            RefreshList();
        }
        finally
        {
            _updating = false;
        }
    }

    private void SetMode(string text, string brush)
    {
        ModeText.Text = text;
        ModeDot.Fill = (Brush)FindResource(brush);
    }

    private void RefreshList()
    {
        var games = _engine.Games;
        var running = _engine.RunningVpnGames.Select(g => g.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ping = _engine.GamePing;
        var key = string.Join("|", games.Select(g =>
            $"{g.Path}:{running.Contains(g.Path)}:{g.LearnedAddresses.Count}:{g.LearnedDomains.Count}")) + $"|{ping?.TotalMilliseconds:0}|{_engine.VpnTunnelUp}|{_confirmRemove}";
        if (key == _shown)
            return;
        _shown = key;

        GameList.Children.Clear();
        EmptyText.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var game in games)
            GameList.Children.Add(Card(game, running.Contains(game.Path), ping));
    }

    private UIElement Card(GameEntry game, bool isRunning, TimeSpan? ping)
    {
        var status = !isRunning ? "Не запущена"
            : !_engine.VpnTunnelUp ? "Запущена, VPN поднимается…"
            : ping is { } p ? $"Запущена — через VPN, пинг {p.TotalMilliseconds:0} мс"
            : "Запущена — через VPN";
        var learned = game.LearnedAddresses.Count + game.LearnedDomains.Count == 0
            ? "Пока ничего не запомнено — запомню при первой игре"
            : $"Запомнено: адресов {game.LearnedAddresses.Count}, доменов {game.LearnedDomains.Count} — со следующего запуска сразу через VPN";

        var panel = new StackPanel();
        var head = new DockPanel();
        var dot = new Ellipse
        {
            Width = 6, Height = 6, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)FindResource(isRunning ? "GreenBrush" : "GrayBrush"),
        };
        head.Children.Add(dot);
        head.Children.Add(new TextBlock { Text = game.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(head);
        panel.Children.Add(new TextBlock
        {
            Text = status, FontSize = 12, Margin = new Thickness(14, 4, 0, 0),
            Foreground = (Brush)FindResource(isRunning ? "GreenTextBrush" : "MutedBrush"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = learned, FontSize = 11, Margin = new Thickness(14, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("FaintBrush"),
        });
        panel.Children.Add(new TextBlock
        {
            Text = game.Path, FontSize = 10.5, Margin = new Thickness(14, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("FaintBrush"), ToolTip = game.Path,
        });

        var buttons = new WrapPanel { Margin = new Thickness(14, 10, 0, 0) };
        var reset = new Button { Content = "Сбросить", Style = (Style)FindResource("SmallButton"), ToolTip = "Забыть адреса и домены этой игры" };
        reset.Click += (_, _) =>
        {
            _engine.ResetGame(game.Path);
            Toast?.Invoke($"{game.Name}: запомненное сброшено");
        };
        var confirming = _confirmRemove == game.Path;
        var remove = new Button { Content = confirming ? "Точно убрать?" : "Убрать", Style = (Style)FindResource("DangerButton") };
        remove.Click += (_, _) =>
        {
            if (_confirmRemove != game.Path)
            {
                _confirmRemove = game.Path;
                Refresh();
                return;
            }
            _confirmRemove = null;
            _engine.RemoveGame(game.Path);
            Toast?.Invoke($"{game.Name} убрана из списка");
        };
        buttons.Children.Add(reset);
        buttons.Children.Add(remove);
        panel.Children.Add(buttons);

        return new Border
        {
            Child = panel,
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            CornerRadius = new CornerRadius(12),
            Background = (Brush)FindResource("WellBrush"),
            BorderBrush = (Brush)FindResource("DividerBrush"),
            BorderThickness = new Thickness(1),
        };
    }

    private void GameMode_Click(object sender, RoutedEventArgs e)
    {
        if (_updating)
            return;
        var on = GameModeSwitch.IsChecked == true;
        _engine.SetGameMode(on);
        Toast?.Invoke(on ? "Игровой режим включён" : "Игровой режим выключен");
    }

    private void AddExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбери .exe игры",
            Filter = "Программы (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            Add(dialog.FileName);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Выбери папку игры" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            Add(dialog.FolderName);
    }

    private void Add(string path)
    {
        var error = _engine.AddGame(path);
        Toast?.Invoke(error ?? "Игра добавлена — при следующем запуске пойдёт через VPN");
        Refresh();
    }
}
