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
