using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ZapretPro.App;

/// <summary>
/// Новогоднее оформление: падающий снег по всему окну, мигающая гирлянда и таймер до Нового года.
/// Анимации собраны в один Storyboard — окно ставит его на паузу, когда спрятано в трей.
/// </summary>
public static class NewYear
{
    private static readonly Color[] BulbColors =
    [
        Color.FromRgb(0xEF, 0x44, 0x44), Color.FromRgb(0xFA, 0xCC, 0x15), Color.FromRgb(0x22, 0xC5, 0x5E),
        Color.FromRgb(0x38, 0xBD, 0xF8), Color.FromRgb(0xF4, 0x72, 0xB6),
    ];

    /// <summary>Снежинки: разного размера и скорости, падают с лёгким покачиванием.</summary>
    public static void BuildSnow(Canvas canvas, Storyboard board, double width, double height, int count = 45)
    {
        var random = new Random(2026);
        for (var i = 0; i < count; i++)
        {
            var size = 2 + random.NextDouble() * 4;
            var flake = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = Brushes.White,
                Opacity = 0.35 + random.NextDouble() * 0.5,
                IsHitTestVisible = false,
            };
            var shift = new TranslateTransform();
            flake.RenderTransform = shift;
            Canvas.SetLeft(flake, random.NextDouble() * width);
            Canvas.SetTop(flake, -10);
            canvas.Children.Add(flake);

            // Крупные снежинки «ближе» — падают быстрее; начало сдвинуто, чтобы снег шёл сразу по всему окну
            var fall = TimeSpan.FromSeconds(9 + (6 - size) * 2.2 + random.NextDouble() * 4);
            var down = new DoubleAnimation(0, height + 20, fall)
            {
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = -TimeSpan.FromSeconds(random.NextDouble() * fall.TotalSeconds),
            };
            Storyboard.SetTarget(down, flake);
            Storyboard.SetTargetProperty(down, new PropertyPath("RenderTransform.Y"));
            board.Children.Add(down);

            var sway = new DoubleAnimation(-12 - random.NextDouble() * 10, 12 + random.NextDouble() * 10, TimeSpan.FromSeconds(2.5 + random.NextDouble() * 2.5))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = -TimeSpan.FromSeconds(random.NextDouble() * 3),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(sway, flake);
            Storyboard.SetTargetProperty(sway, new PropertyPath("RenderTransform.X"));
            board.Children.Add(sway);
        }
    }

    /// <summary>Гирлянда: провисающий провод и лампочки, которые по очереди загораются и гаснут.</summary>
    public static void BuildGarland(Canvas canvas, Storyboard board, double width, int bulbs = 13)
    {
        const double sag = 12;
        var wire = new Path
        {
            Data = Geometry.Parse(FormattableString.Invariant($"M0,4 Q{width / 4},{4 + sag * 2} {width / 2},4 T{width},4")),
            Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0x33, 0x41, 0x55)),
            StrokeThickness = 1.5,
        };
        canvas.Children.Add(wire);

        for (var i = 0; i < bulbs; i++)
        {
            var x = (i + 0.5) * width / bulbs;
            // Та же кривая, что у провода: две «волны» по ширине
            var phase = x / (width / 2) % 1;
            var y = 4 + sag * 2 * 2 * phase * (1 - phase);
            var color = BulbColors[i % BulbColors.Length];
            var bulb = new Ellipse
            {
                Width = 9,
                Height = 12,
                Fill = new SolidColorBrush(color),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = color, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.9 },
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(bulb, x - 4.5);
            Canvas.SetTop(bulb, y + 1);
            canvas.Children.Add(bulb);

            var blink = new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.9))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(i % 3 * 0.6),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(blink, bulb);
            Storyboard.SetTargetProperty(blink, new PropertyPath(UIElement.OpacityProperty));
            board.Children.Add(blink);
        }
    }

    /// <summary>
    /// Лежащий снег: полоса во всю ширину с неровным нижним краем — бугорки разной высоты и пара
    /// «сосулек». Одинаковое <paramref name="seed"/> даёт одинаковую форму, у каждого блока — своя.
    /// </summary>
    public static Geometry SnowCap(double width, int seed, double depth = 9)
    {
        var random = new Random(seed);
        var points = new List<Point> { new(width, depth * 0.6) };
        // Идём справа налево по нижнему краю: бугорки шириной 14–30 пикселей
        var x = width;
        while (x > 0)
        {
            x = Math.Max(0, x - (14 + random.NextDouble() * 16));
            var y = depth * (0.35 + random.NextDouble() * 0.65);
            // Изредка — капля-сосулька
            if (x > 10 && random.NextDouble() < 0.12)
                y = depth + 3 + random.NextDouble() * 3;
            points.Add(new Point(x, y));
        }

        var figure = new PathFigure { StartPoint = new Point(0, -1), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment(new Point(width, -1), true));
        figure.Segments.Add(new LineSegment(points[0], true));
        for (var i = 1; i < points.Count; i++)
        {
            // Плавная волна между точками: изгиб вниз, как у настоящего сугроба
            var a = points[i - 1];
            var b = points[i];
            var control = new Point((a.X + b.X) / 2, Math.Max(a.Y, b.Y) + depth * 0.35);
            figure.Segments.Add(new QuadraticBezierSegment(control, b, true));
        }
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Объёмная снежная шапка, лежащая на верхнем крае блока: бугры поднимаются над краем на
    /// <paramref name="height"/>, снизу снег чуть свисает через край, на углах сходит на нет. Слои: тень на
    /// блок, тело с переходом от белого к голубовато-серому (низ сугроба в тени), светлая кромка сверху,
    /// тёмная кромка снизу и поблёскивающие искорки. Край блока — на высоте <c>height + 2</c> от верха холста.
    /// </summary>
    public static Canvas CreateDrift(double width, int seed, double height)
    {
        var random = new Random(seed);
        var edge = height + 2;

        // Верх: бугры разной высоты и ширины, на концах — у самого края блока
        // Снег не выходит за бока блока: начинается и кончается у скруглённых углов
        const double inset = 5;
        var top = new List<Point> { new(inset, edge + 2) };
        var x = inset + 4;
        while (x < width - inset - 4)
        {
            top.Add(new Point(x, edge - height * (0.55 + random.NextDouble() * 0.45)));
            x += 24 + random.NextDouble() * 22;
        }
        top.Add(new Point(width - inset, edge + 2));
        // Ближе к углам бугры ниже — снег «стекает» с закруглений
        for (var i = 1; i < top.Count - 1; i++)
        {
            var near = Math.Min(top[i].X - inset, width - inset - top[i].X);
            if (near < 22)
                top[i] = new Point(top[i].X, edge - (edge - top[i].Y) * (0.35 + near / 22 * 0.65));
        }

        // Низ: снег чуть свисает через край; изредка — капля побольше
        var bottom = new List<Point>();
        x = width - inset;
        while (x > inset)
        {
            var drop = random.NextDouble() < 0.22 ? 6 + random.NextDouble() * 4 : 3 + random.NextDouble() * 2.5;
            bottom.Add(new Point(x, edge + drop));
            x -= 10 + random.NextDouble() * 14;
        }
        bottom.Add(new Point(inset, edge + 2));

        var body = new PathFigure { StartPoint = top[0], IsClosed = true, IsFilled = true };
        AddSmooth(body, top);
        AddSmooth(body, bottom, connectFirst: true);
        var shape = new PathGeometry([body]);
        shape.Freeze();

        var topLine = new PathFigure { StartPoint = top[0] };
        AddSmooth(topLine, top);
        var bottomLine = new PathFigure { StartPoint = bottom[0] };
        AddSmooth(bottomLine, bottom);

        var canvas = new Canvas { Width = width, Height = edge + 12, IsHitTestVisible = false };
        // Тень на блок под снегом
        canvas.Children.Add(new Path
        {
            Data = shape,
            Fill = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)),
            RenderTransform = new TranslateTransform(0, 3),
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 5 },
        });
        canvas.Children.Add(new Path { Data = shape, Fill = BodyBrush });
        canvas.Children.Add(new Path { Data = new PathGeometry([bottomLine]), Stroke = RimShade, StrokeThickness = 1.2 });
        canvas.Children.Add(new Path { Data = new PathGeometry([topLine]), Stroke = Brushes.White, StrokeThickness = 1.6, Opacity = 0.95 });

        // Искорки на снегу
        for (var i = 0; i < Math.Max(2, (int)(width / 70)); i++)
        {
            var spark = new Ellipse { Width = 2.4, Height = 2.4, Fill = Brushes.White, Opacity = 0 };
            Canvas.SetLeft(spark, 10 + random.NextDouble() * (width - 20));
            Canvas.SetTop(spark, edge - height * 0.3 + random.NextDouble() * 3);
            canvas.Children.Add(spark);
            spark.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.8 + random.NextDouble()))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(random.NextDouble() * 3),
            });
        }
        return canvas;
    }

    /// <summary>Плавная линия через точки (сплайн Катмулла — Рома, переведённый в кривые Безье).</summary>
    private static void AddSmooth(PathFigure figure, IReadOnlyList<Point> points, bool connectFirst = false)
    {
        if (connectFirst)
            figure.Segments.Add(new LineSegment(points[0], true));
        for (var i = 0; i < points.Count - 1; i++)
        {
            var p0 = points[Math.Max(0, i - 1)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(points.Count - 1, i + 2)];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
            figure.Segments.Add(new BezierSegment(c1, c2, p2, true));
        }
    }

    // Белый сверху, голубовато-серый к низу: так сугроб выглядит выпуклым, а не плоской заливкой
    private static readonly Brush BodyBrush = Freeze(new LinearGradientBrush(
        [
            new GradientStop(Color.FromRgb(0xFF, 0xFF, 0xFF), 0),
            new GradientStop(Color.FromRgb(0xF3, 0xF7, 0xFD), 0.35),
            new GradientStop(Color.FromRgb(0xD5, 0xE1, 0xF2), 0.7),
            new GradientStop(Color.FromRgb(0xA9, 0xBE, 0xDD), 1),
        ], 90));

    private static readonly Brush RimShade = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0x95, 0xAA, 0xCB)));

    /// <summary>Цвет снега: белый сверху, чуть голубоватый внизу — объём.</summary>
    public static Brush SnowBrush { get; } = Freeze(new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xD6, 0xE4, 0xF5), 90));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Текст таймера. С 1 по 14 января — поздравление, остальное время — сколько осталось до 1 января.
    /// </summary>
    public static string Countdown(DateTime now)
    {
        if (now.Month == 1 && now.Day <= 14)
            return "С Новым годом!";
        var left = new DateTime(now.Year + 1, 1, 1) - now;
        var days = left.Days;
        return $"До Нового года: {days} {DaysWord(days)} {left.Hours:00}:{left.Minutes:00}:{left.Seconds:00}";
    }

    internal static string DaysWord(int days) => (days % 100, days % 10) switch
    {
        (>= 11 and <= 14, _) => "дней",
        (_, 1) => "день",
        (_, >= 2 and <= 4) => "дня",
        _ => "дней",
    };
}
