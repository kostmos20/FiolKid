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
        Inner.SizeChanged += (_, e) =>
        {
            // Сияние не вылезает за скруглённые углы; а правый нижний угол не скругляем — там карточка рассыпается
            var corner = new Rect(e.NewSize.Width / 2, e.NewSize.Height / 2, e.NewSize.Width / 2, e.NewSize.Height / 2);
            Inner.Clip = new CombinedGeometry(GeometryCombineMode.Union,
                new RectangleGeometry(new Rect(e.NewSize), 17, 17), new RectangleGeometry(corner));
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
                DriftHost.Content = NewYear.CreateDrift(e.NewSize.Width + 2, TitleText.Text.Sum(c => c), 17);
        };
        Body.SizeChanged += (_, e) => Body.OpacityMask = DissolveMask(e.NewSize);
        Loaded += (_, _) => BuildMosaic();

        // Подсветка при наведении — раньше её делал стиль рамки, теперь рамка отдельно от содержимого
        Root.MouseEnter += (_, _) =>
        {
            Body.BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x8B, 0x5C, 0xF6));
            Body.Background = (Brush)FindResource("CardHoverBrush");
        };
        Root.MouseLeave += (_, _) =>
        {
            Body.ClearValue(Border.BorderBrushProperty);
            Body.ClearValue(Border.BackgroundProperty);
        };
    }

    // Мозаика: квадратик 9 пикселей на сетке с шагом 11; базовая область — 180×110 в правом нижнем углу
    private const int PixelSize = 9;
    private const int PixelStep = 11;
    private double MosaicWidth => 180 * PixelDensity;
    private double MosaicHeight => 110 * PixelDensity;

    /// <summary>Насколько густо и далеко рассыпаются пиксели: 1 — обычно, больше — гуще и шире.</summary>
    public double PixelDensity { get; set; } = 1.2;

    /// <summary>
    /// Маска фона и рамки: всё на месте, а в правом нижнем углу — квадратиками на той же сетке, что и мозаика:
    /// у самого угла их почти нет, дальше от угла всё больше. Так края карточки там не видно — она «рассыпается».
    /// </summary>
    private DrawingBrush? DissolveMask(Size size)
    {
        if (size.Width < 1 || size.Height < 1)
            return null;
        var random = new Random(TitleText.Text.Sum(c => c) * 17);
        var width = Math.Min(MosaicWidth, size.Width - 2);
        var height = Math.Min(MosaicHeight, size.Height - 2);
        var cols = (int)(width / PixelStep);
        var rows = (int)(height / PixelStep);
        // Сетка выровнена по правому нижнему углу содержимого, как и мозаика
        var left = size.Width - 1 - cols * PixelStep;
        var top = size.Height - 1 - rows * PixelStep;

        var kept = new GeometryGroup { FillRule = FillRule.Nonzero };
        kept.Children.Add(new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(size)), new RectangleGeometry(new Rect(left, top, size.Width - left, size.Height - top))));
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var distance = Distance(col, row, cols, rows);
                if (random.NextDouble() < Math.Clamp(distance * 1.7 - 0.35, 0, 1))
                    kept.Children.Add(new RectangleGeometry(new Rect(left + col * PixelStep, top + row * PixelStep, PixelStep, PixelStep)));
            }
        }
        var mask = new DrawingBrush(new GeometryDrawing(Brushes.Black, null, kept))
        {
            Stretch = Stretch.None,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(size),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(size),
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
        mask.Freeze();
        return mask;
    }

    /// <summary>Расстояние клетки от правого нижнего угла: 0 — в самом углу, 1 — далеко.</summary>
    private static double Distance(int col, int row, int cols, int rows) =>
        Math.Sqrt(Math.Pow((cols - 1 - col) / (double)cols, 2) + Math.Pow((rows - 1 - row) / (double)rows, 2) * 1.6);

    /// <summary>Лежит ли снег на карточке (новогоднее оформление).</summary>
    public bool Snowy
    {
        set => DriftHost.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Пиксели цвета сервиса в правом нижнем углу: у угла плотно и ярко, дальше — реже и бледнее, как будто
    /// картинка рассыпается. Часть дальних пикселей медленно отлетает вверх-влево и тает, потом появляется снова.
    /// </summary>
    private void BuildMosaic()
    {
        if (Mosaic.Children.Count > 0)
            return;
        const int size = PixelSize;
        const int step = PixelStep;
        Mosaic.Width = MosaicWidth;
        Mosaic.Height = MosaicHeight;
        Canvas.SetLeft(Mosaic, -MosaicWidth);
        Canvas.SetTop(Mosaic, -MosaicHeight);
        var random = new Random(TitleText.Text.Sum(c => c) * 31);
        var cols = (int)(Mosaic.Width / step);
        var rows = (int)(Mosaic.Height / step);
        // Холст выровнен вправо-вниз: первая клетка — с остатка, чтобы сетка совпала с маской фона
        var offsetX = Mosaic.Width - cols * step;
        var offsetY = Mosaic.Height - rows * step;
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var distance = Distance(col, row, cols, rows);
                // Россыпь редеет к краю, но тянется далеко от угла; густота — своя у каждой карточки
                if (random.NextDouble() < distance * 0.95 / PixelDensity - 0.12)
                    continue;
                var opacity = Math.Max(0.12, 0.8 - distance * 0.55);
                var pixel = new System.Windows.Shapes.Rectangle
                {
                    Width = size,
                    Height = size,
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                    Fill = new SolidColorBrush(_accent),
                    Opacity = opacity,
                };
                Canvas.SetLeft(pixel, offsetX + col * step + 1);
                Canvas.SetTop(pixel, offsetY + row * step + 1);
                Mosaic.Children.Add(pixel);

                if (distance < 0.35 || random.NextDouble() > 0.5)
                    continue;
                var shift = new TranslateTransform();
                pixel.RenderTransform = shift;
                var period = TimeSpan.FromSeconds(2.5 + random.NextDouble() * 3);
                var start = TimeSpan.FromSeconds(-random.NextDouble() * 5);
                shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -(10 + random.NextDouble() * 22), period)
                {
                    RepeatBehavior = RepeatBehavior.Forever, BeginTime = start, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                });
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -(8 + random.NextDouble() * 18), period)
                {
                    RepeatBehavior = RepeatBehavior.Forever, BeginTime = start, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                });
                var fade = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, BeginTime = start, Duration = period };
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(opacity, KeyTime.FromPercent(0)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(opacity, KeyTime.FromPercent(0.6)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                pixel.BeginAnimation(OpacityProperty, fade);
            }
        }
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
        StatusRow.Visibility = action is null ? Visibility.Visible : Visibility.Collapsed;

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
        LogoGlow.Color = _accent;
        LogoGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, new DoubleAnimation(off ? 0 : 0.9, duration));
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
