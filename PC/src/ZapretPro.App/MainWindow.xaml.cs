using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using ZapretPro.Core.Health;
using ZapretPro.Core.Key;
using ZapretPro.Core.Vpn;

namespace ZapretPro.App;

/// <summary>Главный экран: большая кнопка, общее состояние и четыре карточки сервисов. Без технических слов.</summary>
public partial class MainWindow : Window
{
    private readonly AppEngine _engine;
    private readonly Storyboard _blink;
    private bool _busy;
    private bool _blinking;
    private bool? _painted;
    private FrameworkElement? _page;

    public MainWindow(AppEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        _blink = BuildBlink();
        VersionText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";

        _engine.StatusChanged += () => Dispatcher.BeginInvoke(Refresh);
        TelegramCard.ActionClick += async (_, _) => await TelegramActionAsync();
        AiCard.ActionClick += (_, _) => PasteKey();

        LogTab.Init(engine);
        SettingsTab.Init(engine);
        LogTab.Toast += ShowToast;
        SettingsTab.Toast += ShowToast;

        _countdown.Tick += (_, _) => CountdownText.Text = NewYear.Countdown(DateTime.Now);

        Loaded += (_, _) =>
        {
            SelectTab(HomePage);
            Refresh();
            AnimateCardsIn();
            NewYear.BuildSnow(Snow, _newYear, ActualWidth, ActualHeight);
            NewYear.BuildGarland(Garland, _newYear, ActualWidth - 32);
            ApplyNewYear(_engine.Settings.NewYearTheme != false);
        };
        // Окно спрятано в трей или свёрнуто — анимации стоят и не тратят процессор
        IsVisibleChanged += (_, _) => UpdateAnimationsRunning();
        StateChanged += (_, _) => UpdateAnimationsRunning();
    }

    private bool Showing => IsVisible && WindowState != WindowState.Minimized;

    private readonly Storyboard _newYear = new();
    private readonly System.Windows.Threading.DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _newYearOn;

    /// <summary>Включить или выключить новогоднее оформление (переключатель в настройках).</summary>
    public void ApplyNewYear(bool on)
    {
        _newYearOn = on;
        // Снежные шапки на плитках нарисованы в самих картинках, поэтому здесь только падающий снег,
        // гирлянда, таймер и сугроб над вкладками
        Garland.Visibility = NewYearPanel.Visibility = TabSnow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateSnow();
        if (on)
        {
            CountdownText.Text = NewYear.Countdown(DateTime.Now);
            _newYear.Begin(this, true);
        }
        else
            _newYear.Stop(this);
        UpdateAnimationsRunning();
    }

    private void UpdateSnow() =>
        Snow.Visibility = _newYearOn && HomePage.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateAnimationsRunning()
    {
        if (Showing)
        {
            if (_blinking)
                _blink.Resume(this);
            if (_newYearOn)
            {
                _newYear.Resume(this);
                _countdown.Start();
            }
        }
        else
        {
            _blink.Pause(this);
            _newYear.Pause(this);
            _countdown.Stop();
        }
    }

    private void HomeTab_Click(object sender, RoutedEventArgs e) => SelectTab(HomePage);
    private void LogTab_Click(object sender, RoutedEventArgs e) => SelectTab(LogTab);
    private void SettingsTab_Click(object sender, RoutedEventArgs e) => SelectTab(SettingsTab);

    /// <summary>Показать одну из трёх вкладок: она плавно выезжает, под значком вкладки — тёплая чёрточка.</summary>
    private void SelectTab(FrameworkElement page)
    {
        var tabs = new (FrameworkElement Page, Button Button, FrameworkElement Mark)[]
        {
            (HomePage, HomeTabButton, HomeMark), (LogTab, LogTabButton, LogMark), (SettingsTab, SettingsTabButton, SettingsMark),
        };
        foreach (var (tab, button, mark) in tabs)
        {
            var selected = tab == page;
            tab.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            button.Foreground = (Brush)FindResource(selected ? "WinterWarm" : "WinterNav");
            mark.Visibility = selected ? Visibility.Visible : Visibility.Hidden;
        }
        // Не на главной улица уходит в тень и размывается, гирлянда тускнеет, а снег не идёт — читаемость важнее
        var home = page == HomePage;
        var fade = TimeSpan.FromMilliseconds(300);
        Scene.BeginAnimation(OpacityProperty, new DoubleAnimation(home ? 1 : 0.42, fade));
        SceneBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new DoubleAnimation(home ? 0 : 3, fade));
        SubShade.BeginAnimation(OpacityProperty, new DoubleAnimation(home ? 0 : 1, fade));
        Garland.BeginAnimation(OpacityProperty, new DoubleAnimation(home ? 1 : 0.4, fade));
        UpdateSnow();

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
            (false, _) => ("Выключено", "Нажми на кнопку, чтобы включить FiolKid."),
            _ when status.NeedsReboot => ("Перезагрузи компьютер", "Драйвер обхода блокировок завис — после перезагрузки всё заработает"),
            (true, ZapretState.Searching) when status.Measuring
                => ("Замеряю скорость…", $"Пробую способ {status.StrategyNumber} из {status.StrategyCount}, пару минут видео может подтормаживать"),
            (true, ZapretState.Searching) => ("Подбираю лучший способ…", "Это может занять пару минут"),
            (true, ZapretState.NoInternet) => ("Нет интернета", "Проверь, подключён ли компьютер к интернету"),
            _ when checking => ("Проверяю…", "Сейчас всё проверю"),
            _ when connecting => ("Подключаем…", "Обычно это занимает несколько секунд."),
            _ when problems == 0 => ("Всё работает", "Можно пользоваться привычными сервисами."),
            _ when looks[Service.Telegram] == ServiceLook.NeedsConnect && problems == 1
                => ("Почти всё работает", "Осталось подключить Telegram — нажми «Подключить»"),
            _ => ("Почти всё работает", "Если не наладится само — открой «Журнал» и пришли отчёт"),
        };

        var busy = searching || checking || connecting;
        PaintPower(on);
        PowerLabel.Text = !on ? "Включить" : searching || connecting ? "Подключаем…" : "Выключить";
        ServicesSummary.Text = !on ? "Всё в одном месте"
            : busy ? "Проверяем доступ"
            : problems == 0 ? "Все сервисы доступны"
            : "Не всё доступно";

        // Вышло обновление — вместо тихой строки внизу ссылка на него
        if (_engine.Update is { } update)
        {
            FooterQuiet.Visibility = Visibility.Hidden;
            UpdateButton.Visibility = Visibility.Visible;
            UpdateText.Text = _engine.UpdateBusy ? _engine.UpdateStatus : $"Вышла версия {update.Version} — обновить";
            UpdateButton.IsEnabled = !_engine.UpdateBusy;
        }
        else
        {
            FooterQuiet.Visibility = Visibility.Visible;
            UpdateButton.Visibility = Visibility.Collapsed;
        }

        SetBlinking(busy);
    }

    /// <summary>Огонёк на кнопке мигает, пока подключаемся и проверяем.</summary>
    private void SetBlinking(bool blinking)
    {
        if (blinking == _blinking)
            return;
        _blinking = blinking;
        if (blinking)
        {
            _blink.Begin(this, true);
            if (!Showing)
                _blink.Pause(this);
        }
        else
        {
            _blink.Stop(this);
            ButtonLight.Opacity = 0;
        }
    }

    private async void Power_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        _busy = true;
        PowerButton.IsEnabled = false;
        Press(PowerShift);
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

    /// <summary>Кнопка чуть «вдавливается» вниз и возвращается.</summary>
    private static void Press(TranslateTransform shift)
    {
        var bounce = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        shift.BeginAnimation(TranslateTransform.YProperty, bounce);
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

    /// <summary>Кнопка-картинка: включено — цветная, выключено — приглушённая копия.</summary>
    private void PaintPower(bool on)
    {
        if (_painted == on)
            return;
        _painted = on;
        PowerButton.ApplyTemplate();
        if (PowerButton.Template.FindName("Plate", PowerButton) is Image plate)
            plate.Source = new BitmapImage(new Uri($"pack://application:,,,/FiolKid;component/Assets/winter/button-{(on ? "on" : "off")}.png"));
    }

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

    /// <summary>Огонёк справа на кнопке мягко загорается и гаснет.</summary>
    private Storyboard BuildBlink()
    {
        ButtonLight.Fill = new SolidColorBrush(Color.FromRgb(0xB3, 0xC8, 0xAC));
        var board = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var blink = new DoubleAnimation(0.2, 1, TimeSpan.FromSeconds(0.8))
        {
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(blink, ButtonLight);
        Storyboard.SetTargetProperty(blink, new PropertyPath(OpacityProperty));
        board.Children.Add(blink);
        return board;
    }
}
