using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using ZapretPro.Core.Health;
using ZapretPro.Core.Key;

namespace ZapretPro.App;

/// <summary>Главный экран: большая кнопка, общее состояние и четыре карточки сервисов. Без технических слов.</summary>
public partial class MainWindow : Window
{
    private readonly AppEngine _engine;
    private readonly Storyboard _pulse;
    private bool _busy;

    public MainWindow(AppEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        _pulse = BuildPulse();

        _engine.StatusChanged += () => Dispatcher.BeginInvoke(Refresh);
        TelegramCard.ActionClick += (_, _) => ConnectTelegram();
        AiCard.ActionClick += (_, _) => PasteKey();

        LogTab.Init(engine);
        SettingsTab.Init(engine);
        LogTab.Toast += ShowToast;
        SettingsTab.Toast += ShowToast;

        Loaded += (_, _) =>
        {
            SelectTab(HomePage);
            Refresh();
        };
    }

    private void HomeTab_Click(object sender, RoutedEventArgs e) => SelectTab(HomePage);
    private void LogTab_Click(object sender, RoutedEventArgs e) => SelectTab(LogTab);
    private void SettingsTab_Click(object sender, RoutedEventArgs e) => SelectTab(SettingsTab);

    /// <summary>Показать одну из трёх вкладок и подсветить её кнопку.</summary>
    private void SelectTab(FrameworkElement page)
    {
        foreach (var (tab, button) in new (FrameworkElement, System.Windows.Controls.Button)[]
                 { (HomePage, HomeTabButton), (LogTab, LogTabButton), (SettingsTab, SettingsTabButton) })
        {
            var selected = tab == page;
            tab.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            button.Foreground = (Brush)FindResource(selected ? "BlueBrush" : "MutedBrush");
        }
        if (page == LogTab)
            LogTab.Refresh();
        if (page == SettingsTab)
            SettingsTab.Refresh();
    }

    /// <summary>Перерисовать всё по текущему состоянию.</summary>
    public void Refresh()
    {
        var on = _engine.IsOn;
        var looks = Enum.GetValues<Service>().ToDictionary(s => s, _engine.Look);

        YouTubeCard.Show(looks[Service.YouTube]);
        DiscordCard.Show(looks[Service.Discord]);
        TelegramCard.Show(looks[Service.Telegram], looks[Service.Telegram] == ServiceLook.NeedsConnect ? "Подключить" : null);
        AiCard.Show(looks[Service.Ai], looks[Service.Ai] == ServiceLook.NeedsKey ? "Вставить ключ" : null);

        var state = _engine.Status.State;
        var searching = on && state == ZapretState.Searching;
        var problems = looks.Values.Count(l => l is ServiceLook.NotWorking or ServiceLook.NeedsConnect);
        var checking = looks.Values.Any(l => l == ServiceLook.Checking);

        (Headline.Text, Subline.Text) = (on, state) switch
        {
            (false, _) => ("Выключено", "Нажми на кнопку, чтобы включить"),
            _ when _engine.Status.NeedsReboot => ("Перезагрузи компьютер", "Драйвер обхода блокировок завис — после перезагрузки всё заработает"),
            (true, ZapretState.Searching) => ("Подбираю лучший способ…", "Это может занять пару минут"),
            (true, ZapretState.NoInternet) => ("Нет интернета", "Проверь, подключён ли компьютер к интернету"),
            _ when checking => ("Проверяю…", "Сейчас всё проверю"),
            _ when problems == 0 => ("Всё работает", "Можно пользоваться"),
            _ when looks[Service.Telegram] == ServiceLook.NeedsConnect && problems == 1
                => ("Почти всё работает", "Осталось подключить Telegram — нажми «Подключить»"),
            _ => ("Почти всё работает", "Если не наладится само — открой «Журнал» и пришли отчёт"),
        };

        var color = !on ? (Color)FindResource("BgBottomColor")
            : searching || checking ? ((SolidColorBrush)FindResource("AmberBrush")).Color
            : problems == 0 ? ((SolidColorBrush)FindResource("GreenBrush")).Color
            : ((SolidColorBrush)FindResource("AmberBrush")).Color;
        PaintPower(on, color);
        TitleDot.Fill = !on ? (Brush)FindResource("GrayBrush") : new SolidColorBrush(color);

        if (searching)
            _pulse.Begin(this, true);
        else
            _pulse.Stop(this);
    }

    private async void Power_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        _busy = true;
        PowerButton.IsEnabled = false;
        try
        {
            if (_engine.IsOn)
            {
                Headline.Text = "Выключаю…";
                Subline.Text = "";
                await _engine.TurnOffAsync();
            }
            else
                _engine.TurnOn();
        }
        finally
        {
            _busy = false;
            PowerButton.IsEnabled = true;
            Refresh();
        }
    }

    private void ConnectTelegram()
    {
        try
        {
            _engine.Telegram.OpenInTelegram();
            ShowToast("Telegram спросит «Подключить прокси?» — нажми «Подключить»");
            // Через пару секунд Telegram подключится — проверим, чтобы карточка позеленела
            _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => _engine.CheckNowAsync());
        }
        catch (Exception ex)
        {
            ShowToast($"Не удалось открыть Telegram: {ex.Message}");
        }
    }

    private void PasteKey()
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast("Сначала скопируй ключ, который прислал знакомый (он начинается с vpn://)");
            return;
        }
        try
        {
            _engine.SaveKey(text);
            Clipboard.Clear();
            ShowToast("Ключ вставлен");
            Refresh();
        }
        catch (KeyParseException ex)
        {
            ShowToast(ex.Message);
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Крестик не выключает программу, а прячет окно в трей.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (((App)Application.Current).IsExiting)
            return;
        e.Cancel = true;
        Hide();
        ((App)Application.Current).Tray.ShowHiddenHint();
    }

    public void ShowToast(string text)
    {
        ToastText.Text = text;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.5))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(5))));
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Цвет кнопки: серая — выключено, зелёная — всё хорошо, янтарная — подбираю или не всё работает.</summary>
    private void PaintPower(bool on, Color color)
    {
        PowerButton.ApplyTemplate();
        // Эффект из шаблона WPF может «заморозить» (сделать неизменяемым) — тогда работаем с его копией
        var glowEllipse = (System.Windows.Shapes.Ellipse)PowerButton.Template.FindName("Glow", PowerButton);
        if (glowEllipse.Effect.IsFrozen)
            glowEllipse.Effect = glowEllipse.Effect.Clone();
        var glow = (DropShadowEffect)glowEllipse.Effect;
        var top = (GradientStop)PowerButton.Template.FindName("FaceTop", PowerButton);
        var bottom = (GradientStop)PowerButton.Template.FindName("FaceBottom", PowerButton);

        var duration = TimeSpan.FromMilliseconds(400);
        Animate(top, on ? Lighten(color, 0.15) : Color.FromRgb(0x33, 0x41, 0x55), duration);
        Animate(bottom, on ? Darken(color, 0.35) : Color.FromRgb(0x1E, 0x29, 0x3B), duration);
        glow.Color = color;
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(on ? 0.75 : 0, duration));

        PowerLabel.Text = on ? "ВЫКЛЮЧИТЬ" : "ВКЛЮЧИТЬ";
        PowerLabel.Opacity = on ? 0.85 : 1;
    }

    private static void Animate(GradientStop stop, Color to, TimeSpan duration) =>
        stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(to, duration));

    private static Color Lighten(Color c, double k) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * k), (byte)(c.G + (255 - c.G) * k), (byte)(c.B + (255 - c.B) * k));

    private static Color Darken(Color c, double k) =>
        Color.FromRgb((byte)(c.R * (1 - k)), (byte)(c.G * (1 - k)), (byte)(c.B * (1 - k)));

    /// <summary>Кольцо вокруг кнопки расходится и гаснет — «идёт работа».</summary>
    private Storyboard BuildPulse()
    {
        var board = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var duration = new Duration(TimeSpan.FromSeconds(1.6));

        foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            var scale = new DoubleAnimation(0.84, 1.0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(scale, PulseRing);
            Storyboard.SetTargetProperty(scale, new PropertyPath($"RenderTransform.{(property == ScaleTransform.ScaleXProperty ? "ScaleX" : "ScaleY")}"));
            board.Children.Add(scale);
        }

        var fade = new DoubleAnimation(0.9, 0, duration);
        Storyboard.SetTarget(fade, PulseRing);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        board.Children.Add(fade);
        return board;
    }
}
