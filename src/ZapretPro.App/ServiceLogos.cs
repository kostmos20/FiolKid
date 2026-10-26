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
    public static Canvas Create(string name, Brush brush, out IReadOnlyList<UIElement> twinkles)
    {
        var canvas = new Canvas { Width = 48, Height = 48 };
        var stars = new List<UIElement>();
        // Контурные логотипы — из Tabler Icons (MIT, © Paweł Kuna, https://tabler.io/icons): нарисованы на
        // сетке 24×24 линией толщиной 2, как неоновые трубки; на холсте 48×48 — вдвое крупнее
        string[]? outline = name switch
        {
            "YouTube" =>
            [
                "M2 8a4 4 0 0 1 4 -4h12a4 4 0 0 1 4 4v8a4 4 0 0 1 -4 4h-12a4 4 0 0 1 -4 -4v-8",
                "M10 9l5 3l-5 3l0 -6",
            ],
            "Discord" =>
            [
                "M8 12a1 1 0 1 0 2 0a1 1 0 0 0 -2 0",
                "M14 12a1 1 0 1 0 2 0a1 1 0 0 0 -2 0",
                "M15.5 17c0 1 1.5 3 2 3c1.5 0 2.833 -1.667 3.5 -3c.667 -1.667 .5 -5.833 -1.5 -11.5c-1.457 -1.015 -3 -1.34 -4.5 -1.5" +
                "l-.972 1.923a11.913 11.913 0 0 0 -4.053 0l-.975 -1.923c-1.5 .16 -3.043 .485 -4.5 1.5c-2 5.667 -2.167 9.833 -1.5 11.5" +
                "c.667 1.333 2 3 3.5 3c.5 0 2 -2 2 -3",
                "M7 16.5c3.5 1 6.5 1 10 0",
            ],
            "Telegram" => ["M15 10l-4 4l6 6l4 -16l-18 7l4 2l2 6l3 -4"],
            _ => null,
        };
        if (outline is not null)
        {
            foreach (var data in outline)
            {
                canvas.Children.Add(new Path
                {
                    Data = Geometry.Parse(data),
                    Stroke = brush,
                    StrokeThickness = 2,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    RenderTransform = new ScaleTransform(2, 2),
                });
            }
            twinkles = stars;
            return canvas;
        }

        switch (name)
        {

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
}
