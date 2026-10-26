using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ZapretPro.App;

/// <summary>
/// Карточка сервиса на главном экране: логотип, название, точка с состоянием и кнопка, если нужна.
/// За логотипом — мягкое сияние цвета сервиса: пока подключаемся, оно «дышит», когда заработало — короткая
/// вспышка, выключено — карточка серая.
/// </summary>
public partial class ServiceCard : UserControl
{
    private static readonly Color Gray = Color.FromRgb(0x64, 0x74, 0x8B);

    private readonly SolidColorBrush _logoBrush = new(Gray);
    private IReadOnlyList<UIElement> _stars = [];
    private Color _accent = Color.FromRgb(0x8B, 0x5C, 0xF6);
    private ServiceLook? _shown;

    public ServiceCard()
    {
        InitializeComponent();
        // Сияние не должно вылезать за скруглённые углы карточки
        Inner.SizeChanged += (_, e) =>
            Inner.Clip = new RectangleGeometry(new Rect(e.NewSize), 17, 17);
    }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string Subtitle
    {
        get => SubtitleText.Text;
        set
        {
            SubtitleText.Text = value;
            SubtitleText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Какой логотип: YouTube, Discord, Telegram или Stars (звёздочки).</summary>
    public string Logo
    {
        set => LogoBox.Child = ServiceLogos.Create(value, _logoBrush, out _stars);
    }

    /// <summary>Цвет сервиса: логотип и сияние за ним.</summary>
    public Color Accent
    {
        get => _accent;
        set => _accent = value;
    }

    public event RoutedEventHandler? ActionClick;

    public void Show(ServiceLook look, string? action = null)
    {
        var (text, brush) = look switch
        {
            ServiceLook.Off => ("Выключено", "GrayBrush"),
            ServiceLook.Checking => ("Проверяю…", "AmberBrush"),
            ServiceLook.Working => ("Работает", "GreenBrush"),
            ServiceLook.NotWorking => ("Не работает", "RedBrush"),
            ServiceLook.NeedsConnect => ("Нужно подключить", "AmberBrush"),
            ServiceLook.AppClosed => ("Telegram закрыт", "LavenderBrush"),
            ServiceLook.NeedsKey => ("Нужен ключ", "AmberBrush"),
            _ => ("—", "GrayBrush"),
        };
        StatusText.Text = text;
        Dot.Fill = (Brush)FindResource(brush);
        ActionButton.Content = action;
        ActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;

        if (look == _shown)
            return;
        var previous = _shown;
        _shown = look;
        Animate(look, previous);
    }

    private void Animate(ServiceLook look, ServiceLook? previous)
    {
        var duration = TimeSpan.FromMilliseconds(500);
        var off = look == ServiceLook.Off;
        GlowColor.Color = Color.FromArgb(0xD0, _accent.R, _accent.G, _accent.B);
        _logoBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(off ? Gray : _accent, duration));
        ServiceLogos.Twinkle(_stars, !off);

        // Пока проверяю — точка мигает, а сияние «дышит»; в остальное время горят ровно
        if (look == ServiceLook.Checking)
        {
            Dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.7))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            });
            Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(1.2))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
            return;
        }

        Dot.BeginAnimation(OpacityProperty, null);
        var steady = look switch
        {
            ServiceLook.Off => 0,
            ServiceLook.Working => 1,
            ServiceLook.NotWorking => 0.35,
            _ => 0.65,
        };
        Glow.BeginAnimation(OpacityProperty, new DoubleAnimation(steady, duration));

        // Заработало — короткая вспышка: сияние раздувается и возвращается
        if (look == ServiceLook.Working && previous is not null and not ServiceLook.Working)
        {
            var flash = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(700) };
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(1.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            flash.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(700)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
            var scale = (ScaleTransform)Glow.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, flash);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, flash);
        }
    }

    private void Action_Click(object sender, RoutedEventArgs e) => ActionClick?.Invoke(this, e);
}
