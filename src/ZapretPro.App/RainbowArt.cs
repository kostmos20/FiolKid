using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZapretPro.App;

/// <summary>
/// Радужные картинки для главного экрана. Конического градиента («цвет по кругу») в WPF нет, поэтому
/// рисуем его один раз попиксельно при запуске, а вращает уже видеокарта — процессор не нагружается.
/// </summary>
public static class RainbowArt
{
    // Фиолетовый → синий → бирюзовый → розовый и снова фиолетовый: цвета FiolKid
    private static readonly Color[] Deep =
    [
        Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0x3B, 0x82, 0xF6), Color.FromRgb(0x14, 0xB8, 0xA6),
        Color.FromRgb(0xEC, 0x48, 0x99), Color.FromRgb(0x7C, 0x3A, 0xED),
    ];

    private static readonly Color[] Light =
    [
        Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0x60, 0xA5, 0xFA), Color.FromRgb(0x2D, 0xD4, 0xBF),
        Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0xA7, 0x8B, 0xFA),
    ];

    /// <summary>Воронка: цвета по кругу, у центра яркие, к краю плавно уходят в прозрачность.</summary>
    public static BitmapSource Funnel(int size) => Draw(size, Deep, r => Math.Pow(Math.Max(0, 1 - r), 1.15));

    /// <summary>Для обводки кнопки: цвета по кругу, без затухания.</summary>
    public static ImageBrush Ring(int size) => new(Draw(size, Light, _ => 1)) { Stretch = Stretch.Fill };

    private static BitmapSource Draw(int size, Color[] palette, Func<double, double> alphaByRadius)
    {
        var pixels = new byte[size * size * 4];
        var center = (size - 1) / 2.0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var radius = Math.Sqrt(dx * dx + dy * dy) / center;
                var alpha = alphaByRadius(radius);
                // Угол 0…1 по часовой стрелке от верха
                var turn = (Math.Atan2(dx, -dy) / (2 * Math.PI) + 1) % 1;
                var position = turn * (palette.Length - 1);
                var index = Math.Min((int)position, palette.Length - 2);
                var t = position - index;
                // Плавный переход между соседними цветами, без заметных «шов» на стыках
                t = t * t * (3 - 2 * t);
                var a = palette[index];
                var b = palette[index + 1];
                var offset = (y * size + x) * 4;
                // Pbgra32: каналы заранее умножены на прозрачность
                pixels[offset] = (byte)((a.B + (b.B - a.B) * t) * alpha);
                pixels[offset + 1] = (byte)((a.G + (b.G - a.G) * t) * alpha);
                pixels[offset + 2] = (byte)((a.R + (b.R - a.R) * t) * alpha);
                pixels[offset + 3] = (byte)(255 * alpha);
            }
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
