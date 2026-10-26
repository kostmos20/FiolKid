using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ZapretPro.App;

/// <summary>
/// Логотипы сервисов для карточек — векторами, на холсте 48×48: чёткие на любом масштабе экрана и одинаковые
/// на всех компьютерах (в отличие от символов шрифта). Цвет — через общую кисть, чтобы карточка могла
/// перекрасить логотип в серый, когда программа выключена.
/// </summary>
public static class ServiceLogos
{
    // Цвет «вырезов» (глаза Discord, треугольник YouTube) — как фон карточки
    private static readonly Brush Cutout = new SolidColorBrush(Color.FromRgb(0x1A, 0x22, 0x33));

    public static Canvas Create(string name, Brush brush, out IReadOnlyList<UIElement> twinkles)
    {
        var canvas = new Canvas { Width = 48, Height = 48 };
        var stars = new List<UIElement>();
        switch (name)
        {
            case "YouTube":
                canvas.Children.Add(new Rectangle { Width = 42, Height = 30, RadiusX = 9, RadiusY = 9, Fill = brush, Margin = new Thickness(3, 9, 0, 0) });
                canvas.Children.Add(Shape("M20,17 L33,24 L20,31 Z", Brushes.White));
                break;

            case "Discord":
                canvas.Children.Add(Shape(
                    "M39.5,10.6 C36.6,9.3 33.5,8.3 30.3,7.8 C29.9,8.5 29.4,9.5 29.1,10.3 C25.7,9.8 22.3,9.8 19,10.3 " +
                    "C18.6,9.5 18.2,8.5 17.7,7.8 C14.5,8.3 11.4,9.3 8.5,10.6 C2.7,19.3 1.1,27.8 1.9,36.2 " +
                    "C5.7,39 9.5,40.7 13.1,41.8 C14,40.6 14.8,39.3 15.4,37.9 C14.1,37.4 12.8,36.8 11.6,36.1 " +
                    "C11.9,35.9 12.2,35.6 12.5,35.4 C20,38.8 28,38.8 35.4,35.4 C35.7,35.6 36,35.9 36.3,36.1 " +
                    "C35.1,36.8 33.8,37.4 32.5,37.9 C33.2,39.3 33.9,40.6 34.8,41.8 C38.4,40.7 42.2,39 46,36.2 " +
                    "C47,26.5 44.4,18.1 39.5,10.6 Z", brush));
                canvas.Children.Add(Eye(17.3, 27));
                canvas.Children.Add(Eye(30.7, 27));
                break;

            case "Telegram":
                canvas.Children.Add(new Ellipse { Width = 44, Height = 44, Fill = brush, Margin = new Thickness(2, 2, 0, 0) });
                canvas.Children.Add(Shape(
                    "M10.8,23.6 L34.6,14.4 C35.7,14 36.7,14.7 36.3,16.3 L32.3,35.2 C32,36.5 31.2,36.8 30.1,36.2 " +
                    "L24.1,31.8 L21.2,34.6 C20.9,34.9 20.6,35.2 20,35.2 L20.4,29 L31.6,18.9 C32.1,18.5 31.5,18.2 30.8,18.7 " +
                    "L17,27.4 L11.1,25.5 C9.8,25.1 9.8,24.2 10.8,23.6 Z", Brushes.White));
                break;

            default:
                // «Сайты и ИИ»: три звёздочки-искорки, мерцают вразнобой
                foreach (var (x, y, r) in new[] { (19.0, 27.0, 15.0), (36.5, 11.5, 8.0), (37.0, 35.0, 6.0) })
                {
                    var star = Shape(Sparkle(x, y, r), brush);
                    star.Width = 48;
                    star.Height = 48;
                    star.RenderTransform = new ScaleTransform(1, 1, x, y);
                    canvas.Children.Add(star);
                    stars.Add(star);
                }
                break;
        }
        twinkles = stars;
        return canvas;
    }

    /// <summary>Мерцание звёзд: каждая то чуть сжимается и тускнеет, то вспыхивает — со своим ритмом.</summary>
    public static void Twinkle(IReadOnlyList<UIElement> stars, bool on)
    {
        for (var i = 0; i < stars.Count; i++)
        {
            var star = stars[i];
            var scale = (ScaleTransform)star.RenderTransform;
            if (!on)
            {
                star.BeginAnimation(UIElement.OpacityProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                continue;
            }
            var period = TimeSpan.FromSeconds(1.6 + i * 0.55);
            var delay = TimeSpan.FromSeconds(i * 0.45);
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var size = new DoubleAnimation(1, i == 0 ? 0.8 : 0.55, period)
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = delay, EasingFunction = ease,
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, size);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, size);
            star.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.45, period)
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = delay, EasingFunction = ease,
            });
        }
    }

    /// <summary>Четырёхлучевая искорка с вогнутыми сторонами.</summary>
    private static string Sparkle(double x, double y, double r) => FormattableString.Invariant(
        $"M{x},{y - r} Q{x},{y} {x + r},{y} Q{x},{y} {x},{y + r} Q{x},{y} {x - r},{y} Q{x},{y} {x},{y - r} Z");

    private static Path Shape(string data, Brush fill) => new() { Data = Geometry.Parse(data), Fill = fill };

    private static Ellipse Eye(double x, double y) =>
        new() { Width = 7.4, Height = 8.2, Fill = Cutout, Margin = new Thickness(x - 3.7, y - 4.1, 0, 0) };
}
