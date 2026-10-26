using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using ZapretPro.Core.Health;
using ZapretPro.Core.Key;
using ZapretPro.Core.Vpn;

namespace ZapretPro.App;

/// <summary>Главный экран: большая кнопка, общее состояние и четыре карточки сервисов. Без технических слов.</summary>
public partial class MainWindow : Window
{
    private readonly AppEngine _engine;
    private readonly Storyboard _pulse;
    private readonly Storyboard _spin;
    private readonly Storyboard _aurora;
    private bool _busy;
    private bool _spinning;
    private (bool On, Color Color)? _painted;
    private FrameworkElement? _page;

    public MainWindow(AppEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        _pulse = BuildPulse();
        _spin = BuildSpin();
        _aurora = BuildAurora();
        VersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";

        _engine.StatusChanged += () => Dispatcher.BeginInvoke(Refresh);
        TelegramCard.ActionClick += async (_, _) => await TelegramActionAsync();
        AiCard.ActionClick += (_, _) => PasteKey();

        LogTab.Init(engine);
        SettingsTab.Init(engine);
        LogTab.Toast += ShowToast;
        SettingsTab.Toast += ShowToast;

        Loaded += (_, _) =>
        {
            SelectTab(HomePage);
            Refresh();
            AnimateCardsIn();
            _aurora.Begin(this, true);
        };
        // Окно спрятано в трей или свёрнуто — анимации стоят и не тратят процессор
        IsVisibleChanged += (_, _) => UpdateAnimationsRunning();
        StateChanged += (_, _) => UpdateAnimationsRunning();
    }

    private bool Showing => IsVisible && WindowState != WindowState.Minimized;

    private void UpdateAnimationsRunning()
    {
        if (Showing)
        {
            _aurora.Resume(this);
            if (_spinning)
                _spin.Resume(this);
        }
        else
        {
            _aurora.Pause(this);
            _spin.Pause(this);
        }
    }

    private void HomeTab_Click(object sender, RoutedEventArgs e) => SelectTab(HomePage);
    private void LogTab_Click(object sender, RoutedEventArgs e) => SelectTab(LogTab);
    private void SettingsTab_Click(object sender, RoutedEventArgs e) => SelectTab(SettingsTab);

    /// <summary>Показать одну из трёх вкладок: она плавно выезжает, полоска над вкладкой переезжает к ней.</summary>
    private void SelectTab(FrameworkElement page)
    {
        var tabs = new (FrameworkElement Page, Button Button)[] { (HomePage, HomeTabButton), (LogTab, LogTabButton), (SettingsTab, SettingsTabButton) };
        for (var i = 0; i < tabs.Length; i++)
        {
            var (tab, button) = tabs[i];
            var selected = tab == page;
            tab.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            button.Foreground = (Brush)FindResource(selected ? "LavenderBrush" : "MutedBrush");
            if (selected)
                MoveTabIndicator(i);
        }
        if (page == LogTab)
            LogTab.Refresh();
        if (page == SettingsTab)
            SettingsTab.Refresh();

        if (_page != page)
        {
            _page = page;
            var shift = new TranslateTransform(0, 14);
            page.RenderTransform = shift;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }
    }

    private void MoveTabIndicator(int index)
    {
        TabBar.UpdateLayout();
        var column = TabBar.ActualWidth / 3;
        if (column <= 0)
        {
            // Окно ещё не разложено — подвинем, когда разложится
            TabBar.Loaded += (_, _) => MoveTabIndicator(index);
            return;
        }
        var x = column * index + (column - TabIndicator.Width) / 2;
        TabIndicatorShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(x, TimeSpan.FromMilliseconds(280)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 } });
    }

    /// <summary>Перерисовать всё по текущему состоянию.</summary>
    public void Refresh()
    {
        var on = _engine.IsOn;
        var status = _engine.Status;
        var looks = Enum.GetValues<Service>().ToDictionary(s => s, _engine.Look);

        YouTubeCard.Show(looks[Service.YouTube]);
        DiscordCard.Show(looks[Service.Discord]);
        TelegramCard.Show(looks[Service.Telegram], looks[Service.Telegram] switch
        {
            ServiceLook.NeedsConnect or ServiceLook.AppClosed => "Подключить",
            ServiceLook.NotWorking => "Перезапустить",
            _ => null,
        });
        AiCard.Show(looks[Service.Ai], looks[Service.Ai] == ServiceLook.NeedsKey ? "Вставить ключ" : null);

        var state = status.State;
        var searching = on && state == ZapretState.Searching;
        var problems = looks.Values.Count(l => l is ServiceLook.NotWorking or ServiceLook.NeedsConnect);
        var checking = looks.Values.Any(l => l == ServiceLook.Checking);
        var connecting = on && status.Vpn.State == VpnState.Connecting;

        (Headline.Text, Subline.Text) = (on, state) switch
        {
            (false, _) => ("Выключено", "Нажми на кнопку, чтобы включить"),
            _ when status.NeedsReboot => ("Перезагрузи компьютер", "Драйвер обхода блокировок завис — после перезагрузки всё заработает"),
            (true, ZapretState.Searching) when status.Measuring
                => ("Замеряю скорость…", $"Пробую способ {status.StrategyNumber} из {status.StrategyCount}, пару минут видео может подтормаживать"),
            (true, ZapretState.Searching) => ("Подбираю лучший способ…", "Это может занять пару минут"),
            (true, ZapretState.NoInternet) => ("Нет интернета", "Проверь, подключён ли компьютер к интернету"),
            _ when checking => ("Проверяю…", "Сейчас всё проверю"),
            _ when problems == 0 => ("Всё работает", "Можно пользоваться"),
            _ when looks[Service.Telegram] == ServiceLook.NeedsConnect && problems == 1
                => ("Почти всё работает", "Осталось подключить Telegram — нажми «Подключить»"),
            _ => ("Почти всё работает", "Если не наладится само — открой «Журнал» и пришли отчёт"),
        };

        var busy = searching || checking || connecting;
        var color = !on ? (Color)FindResource("BgBottomColor")
            : busy ? (Color)FindResource("AccentColor")
            : problems == 0 ? ((SolidColorBrush)FindResource("GreenBrush")).Color
            : ((SolidColorBrush)FindResource("AmberBrush")).Color;
        PaintPower(on, color);
        TitleDot.Fill = !on ? (Brush)FindResource("GrayBrush") : new SolidColorBrush(color);

        if (_engine.Update is { } update)
        {
            UpdateBanner.Visibility = Visibility.Visible;
            UpdateText.Text = _engine.UpdateBusy ? _engine.UpdateStatus : $"Вышла версия {update.Version}";
            UpdateButton.IsEnabled = !_engine.UpdateBusy;
        }
        else
            UpdateBanner.Visibility = Visibility.Collapsed;

        SetSpinning(busy);
        if (searching)
            _pulse.Begin(this, true);
        else
            _pulse.Stop(this);
    }

    private void SetSpinning(bool spinning)
    {
        if (spinning == _spinning)
            return;
        _spinning = spinning;
        SpinRing.BeginAnimation(OpacityProperty, new DoubleAnimation(spinning ? 1 : 0, TimeSpan.FromMilliseconds(350)));
        if (spinning)
        {
            _spin.Begin(this, true);
            if (!Showing)
                _spin.Pause(this);
        }
        else
            _spin.Stop(this);
    }

    private async void Power_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        _busy = true;
        PowerButton.IsEnabled = false;
        Press(PowerButton);
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

    private async void Update_Click(object sender, RoutedEventArgs e) => await InstallUpdateAsync();

    /// <summary>
    /// Скачать, проверить подпись и установить обновление. На минуту всё выключится: программа закроется,
    /// новая версия заменит файлы и запустится сама.
    /// </summary>
    public async Task InstallUpdateAsync()
    {
        if (_engine.Update is not { } update || _engine.UpdateBusy)
            return;
        ShowToast($"Скачиваю версию {update.Version}…");
        var dir = await _engine.DownloadUpdateAsync();
        if (dir is null)
        {
            ShowToast(_engine.UpdateStatus);
            return;
        }
        ShowToast("Устанавливаю — программа перезапустится сама через минуту");
        await Task.Delay(1500);
        await ((App)Application.Current).InstallUpdateAsync(dir);
    }

    /// <summary>Кнопка «проседает» и пружинит обратно.</summary>
    private static void Press(UIElement element)
    {
        if (element.RenderTransform is not ScaleTransform scale)
            return;
        var bounce = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0.93, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)),
            new ElasticEase { Oscillations = 1, Springiness = 5 }));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    }

    /// <summary>Telegram: подключить к прокси, а если прокси сломан — перезапустить его и подключить.</summary>
    private async Task TelegramActionAsync()
    {
        if (_engine.Look(Service.Telegram) == ServiceLook.NotWorking)
        {
            ShowToast("Перезапускаю прокси Telegram…");
            await _engine.RestartTelegramAsync();
        }
        ConnectTelegram();
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

    private async void PasteKey()
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowToast("Сначала скопируй ключ (vpn://…) или ссылку на подписку (https://…)");
            return;
        }
        ShowToast("Проверяю…");
        try
        {
            var saved = await _engine.SaveKeyAsync(text);
            Clipboard.Clear();
            ShowToast(saved);
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
        ToastShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } });
    }

    /// <summary>
    /// Цвет кнопки: серая — выключено, фиолетовая — подбираю или проверяю, зелёная — всё хорошо, янтарная —
    /// не всё работает. Включённая кнопка «дышит» мягким свечением.
    /// </summary>
    private void PaintPower(bool on, Color color)
    {
        if (_painted == (on, color))
            return;
        _painted = (on, color);

        PowerButton.ApplyTemplate();
        // Эффект из шаблона WPF может «заморозить» (сделать неизменяемым) — тогда работаем с его копией
        var glowEllipse = (System.Windows.Shapes.Ellipse)PowerButton.Template.FindName("Glow", PowerButton);
        if (glowEllipse.Effect.IsFrozen)
            glowEllipse.Effect = glowEllipse.Effect.Clone();
        var glow = (DropShadowEffect)glowEllipse.Effect;
        var top = (GradientStop)PowerButton.Template.FindName("FaceTop", PowerButton);
        var bottom = (GradientStop)PowerButton.Template.FindName("FaceBottom", PowerButton);

        var duration = TimeSpan.FromMilliseconds(450);
        Animate(top, on ? Lighten(color, 0.15) : Color.FromRgb(0x33, 0x41, 0x55), duration);
        Animate(bottom, on ? Darken(color, 0.35) : Color.FromRgb(0x1E, 0x29, 0x3B), duration);
        glow.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(color, duration));
        glow.BeginAnimation(DropShadowEffect.OpacityProperty, on
            ? new DoubleAnimation(0.45, 0.85, TimeSpan.FromSeconds(2.4))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            }
            : new DoubleAnimation(0, duration));

        PowerLabel.Text = on ? "ВЫКЛЮЧИТЬ" : "ВКЛЮЧИТЬ";
        PowerLabel.Opacity = on ? 0.85 : 1;
    }

    private static void Animate(GradientStop stop, Color to, TimeSpan duration) =>
        stop.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(to, duration));

    private static Color Lighten(Color c, double k) =>
        Color.FromRgb((byte)(c.R + (255 - c.R) * k), (byte)(c.G + (255 - c.G) * k), (byte)(c.B + (255 - c.B) * k));

    private static Color Darken(Color c, double k) =>
        Color.FromRgb((byte)(c.R * (1 - k)), (byte)(c.G * (1 - k)), (byte)(c.B * (1 - k)));

    /// <summary>Карточки сервисов появляются по очереди, снизу вверх.</summary>
    private void AnimateCardsIn()
    {
        var cards = new FrameworkElement[] { YouTubeCard, DiscordCard, TelegramCard, AiCard };
        for (var i = 0; i < cards.Length; i++)
        {
            var card = cards[i];
            var shift = new TranslateTransform(0, 18);
            card.RenderTransform = shift;
            card.Opacity = 0;
            var delay = TimeSpan.FromMilliseconds(120 + i * 70);
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = delay });
            shift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(380)) { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    /// <summary>Кольцо вокруг кнопки расходится и гаснет — «идёт работа».</summary>
    private Storyboard BuildPulse()
    {
        var board = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var duration = new Duration(TimeSpan.FromSeconds(1.6));

        foreach (var axis in new[] { "ScaleX", "ScaleY" })
        {
            var scale = new DoubleAnimation(0.84, 1.0, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(scale, PulseRing);
            Storyboard.SetTargetProperty(scale, new PropertyPath($"RenderTransform.{axis}"));
            board.Children.Add(scale);
        }

        var fade = new DoubleAnimation(0.9, 0, duration);
        Storyboard.SetTarget(fade, PulseRing);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        board.Children.Add(fade);
        return board;
    }

    /// <summary>Дуга бежит по кругу вокруг кнопки.</summary>
    private Storyboard BuildSpin()
    {
        var board = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var rotate = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2.2));
        Storyboard.SetTarget(rotate, SpinRing);
        Storyboard.SetTargetProperty(rotate, new PropertyPath("RenderTransform.Angle"));
        board.Children.Add(rotate);
        return board;
    }

    /// <summary>Цветные пятна фона медленно плавают — каждое по своей траектории и со своей скоростью.</summary>
    private Storyboard BuildAurora()
    {
        var board = new Storyboard();
        void Drift(UIElement blob, double dx, double dy, double seconds)
        {
            foreach (var (axis, distance) in new[] { ("X", dx), ("Y", dy) })
            {
                var move = new DoubleAnimation(0, distance, TimeSpan.FromSeconds(axis == "X" ? seconds : seconds * 1.37))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                Storyboard.SetTarget(move, blob);
                Storyboard.SetTargetProperty(move, new PropertyPath($"RenderTransform.{axis}"));
                board.Children.Add(move);
            }
        }
        Drift(BlobA, 140, 90, 17);
        Drift(BlobB, -160, -120, 21);
        Drift(BlobC, 180, -100, 25);

        // Цвета перетекают по кругу: фиолетовый → синий → бирюзовый → розовый. У каждого элемента — свой
        // сдвиг по времени, поэтому цвета не меняются хором, а «переливаются»
        Color[] palette = [Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x14, 0xB8, 0xA6), Color.FromRgb(0xEC, 0x48, 0x99)];
        void Cycle(UIElement target, string brush, int stop, byte alpha, double seconds, double phase)
        {
            var animation = new ColorAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(-phase) };
            for (var i = 0; i <= palette.Length; i++)
            {
                var c = palette[i % palette.Length];
                animation.KeyFrames.Add(new EasingColorKeyFrame(Color.FromArgb(alpha, c.R, c.G, c.B),
                    KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds * i / palette.Length)), new SineEase { EasingMode = EasingMode.EaseInOut }));
            }
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, new PropertyPath($"({brush}).(GradientBrush.GradientStops)[{stop}].(GradientStop.Color)"));
            board.Children.Add(animation);
        }
        Cycle(HaloGlow, "Shape.Fill", 0, 0xAA, 16, 0);
        Cycle(HaloRing, "Shape.Stroke", 0, 0xFF, 12, 0);
        Cycle(HaloRing, "Shape.Stroke", 1, 0xFF, 12, 4);
        Cycle(HaloRing, "Shape.Stroke", 2, 0xFF, 12, 8);
        Cycle(BlobA, "Shape.Fill", 0, 0x55, 30, 0);
        Cycle(BlobB, "Shape.Fill", 0, 0x44, 30, 10);
        Cycle(BlobC, "Shape.Fill", 0, 0x44, 30, 20);

        // Кольцо вращается, свечение «дышит»
        var turn = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(18)) { RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(turn, HaloRing);
        Storyboard.SetTargetProperty(turn, new PropertyPath("RenderTransform.Angle"));
        board.Children.Add(turn);
        var breathe = new DoubleAnimation(0.4, 0.75, TimeSpan.FromSeconds(4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(breathe, HaloGlow);
        Storyboard.SetTargetProperty(breathe, new PropertyPath(OpacityProperty));
        board.Children.Add(breathe);
        return board;
    }
}
