using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ZapretPro.App;

/// <summary>Карточка сервиса на главном экране: значок, название, цветная точка с состоянием и кнопка, если нужна.</summary>
public partial class ServiceCard : UserControl
{
    public ServiceCard()
    {
        InitializeComponent();
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

    public string Glyph
    {
        get => GlyphText.Text;
        set => GlyphText.Text = value;
    }

    public Brush BadgeBrush
    {
        get => Badge.Background;
        set => Badge.Background = value;
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
            ServiceLook.NeedsKey => ("Нужен ключ", "AmberBrush"),
            _ => ("—", "GrayBrush"),
        };
        StatusText.Text = text;
        Dot.Fill = (Brush)FindResource(brush);

        ActionButton.Content = action;
        ActionButton.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Action_Click(object sender, RoutedEventArgs e) => ActionClick?.Invoke(this, e);
}
