using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace ZapretPro.App;

/// <summary>
/// Карточка сервиса на главном экране: плитка-картинка, логотип, название, описание, точка с состоянием
/// и кнопка, если нужна. Когда сервис работает, плитка и логотип загораются его цветом.
/// </summary>
public partial class ServiceCard : UserControl
{
    private static readonly Color Gray = Color.FromRgb(0xB2, 0xB5, 0xC1);
    private static readonly BitmapImage OffPlate = Load("card-off");

    private readonly SolidColorBrush _logoBrush = new(Gray);
    private IReadOnlyList<UIElement> _stars = [];
    private BitmapImage? _onPlate;
    private ServiceLook? _shown;

    public ServiceCard()
    {
        InitializeComponent();
        // При наведении плитка чуть светлеет и приподнимается
        var lift = new TranslateTransform();
        Root.RenderTransform = lift;
        Root.MouseEnter += (_, _) =>
        {
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-2, TimeSpan.FromMilliseconds(180)));
            PlateImage.Opacity = 0.93;
        };
        Root.MouseLeave += (_, _) =>
        {
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
            PlateImage.Opacity = 1;
        };
    }

    private static BitmapImage Load(string name)
    {
        var image = new BitmapImage(new Uri($"pack://application:,,,/FiolKid;component/Assets/winter/{name}.png"));
        image.Freeze();
        return image;
    }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public string Description
    {
        get => DescriptionText.Text;
        set => DescriptionText.Text = value;
    }

    /// <summary>Какая плитка у работающего сервиса: youtube, discord, telegram или vpn.</summary>
    public string Plate
    {
        set => _onPlate = Load($"card-{value}");
    }

    /// <summary>Какой логотип: YouTube, Discord, Telegram или Stars (звёздочки).</summary>
    public string Logo
    {
        set => LogoBox.Child = ServiceLogos.Create(value, _logoBrush, out _stars);
    }

    /// <summary>Цвет сервиса: логотип и его свечение.</summary>
    public Color Accent { get; set; } = Color.FromRgb(0xEB, 0xD2, 0xAD);

    public event RoutedEventHandler? ActionClick;

    public void Show(ServiceLook look, string? action = null)
    {
        var (text, color) = look switch
        {
            ServiceLook.Off => ("Выключено", Color.FromRgb(0x92, 0x96, 0xA0)),
            ServiceLook.Checking => ("Проверяю…", Color.FromRgb(0xD9, 0xC2, 0x8E)),
            ServiceLook.Working => ("Работает", Color.FromRgb(0xB6, 0xCF, 0xB9)),
            ServiceLook.NotWorking => ("Не работает", Color.FromRgb(0xE3, 0x9A, 0x8E)),
            ServiceLook.NeedsConnect => ("Нужно подключить", Color.FromRgb(0xD9, 0xC2, 0x8E)),
            ServiceLook.AppClosed => ("Telegram закрыт", Color.FromRgb(0xC8, 0xC8, 0xD0)),
            ServiceLook.NeedsKey => ("Нужен ключ", Color.FromRgb(0xD9, 0xC2, 0x8E)),
            _ => ("—", Color.FromRgb(0x92, 0x96, 0xA0)),
        };
        StatusText.Text = text;
        Dot.Fill = new SolidColorBrush(color);
        StatusText.Foreground = new SolidColorBrush(look is ServiceLook.Off or ServiceLook.AppClosed ? Color.FromRgb(0xC8, 0xC8, 0xD0) : color);
        ActionText.Text = action;
        ActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        StatusRow.Visibility = action is null ? Visibility.Visible : Visibility.Collapsed;

        if (look == _shown)
            return;
        _shown = look;
        Animate(look);
    }

    private void Animate(ServiceLook look)
    {
        var duration = TimeSpan.FromMilliseconds(500);
        var working = look == ServiceLook.Working;
        PlateImage.Source = working && _onPlate is not null ? _onPlate : OffPlate;

        // Логотип цветной, когда сервис включён; светится — когда работает
        var lit = look != ServiceLook.Off;
        _logoBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(lit ? Accent : Gray, duration));
        LogoGlow.Color = working ? Accent : Colors.Black;
        LogoGlow.ShadowDepth = working ? 0 : 1;
        LogoGlow.BlurRadius = working ? 7 : 2;
        LogoGlow.Opacity = working ? 0.6 : 0.55;
        ServiceLogos.Twinkle(_stars, lit);

        // Пока проверяю — точка мигает
        if (look == ServiceLook.Checking)
            Dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.7))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            });
        else
            Dot.BeginAnimation(OpacityProperty, null);
    }

    private void Action_Click(object sender, RoutedEventArgs e) => ActionClick?.Invoke(this, e);
}
