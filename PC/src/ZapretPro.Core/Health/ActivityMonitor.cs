using System.Net.NetworkInformation;

namespace ZapretPro.Core.Health;

/// <summary>Занят ли сейчас человек компьютером так, что перебор стратегий ему помешает.</summary>
public interface IActivityMonitor
{
    /// <summary>Почему сейчас нельзя устраивать перебор (идёт игра, полноэкранное приложение, качается файл), или null.</summary>
    string? BusyReason();
}

/// <summary>
/// Признаки, что компьютер занят: идёт игра или полноэкранное приложение (это видит
/// <see cref="Games.GameMonitor"/>) или по сети идёт большой поток. Перебор стратегий перезапускает zapret
/// и качает тестовые файлы — в такие минуты из игры выкидывает, а загрузка обрывается.
/// </summary>
public sealed class WindowsActivityMonitor : IActivityMonitor
{
    /// <summary>Больше этого в среднем между проверками — канал занят (смотрят видео в 4К, качают игру).</summary>
    private const double BusyBytesPerSecond = 1_000_000;

    private readonly Func<string?> _game;
    private long _lastBytes = -1;
    private DateTime _lastAt;

    /// <param name="game">Что сейчас идёт: «игра (dota2.exe)», «полноэкранное приложение» или null.</param>
    public WindowsActivityMonitor(Func<string?> game)
    {
        _game = game;
    }

    public string? BusyReason()
    {
        // Скорость считаем при каждом вызове, чтобы замер шёл от прошлой проверки
        var rate = NetworkRate();
        if (_game() is { } game)
            return $"идёт {game}";
        if (rate is > BusyBytesPerSecond)
            return $"сеть занята ({rate / 1_000_000:0.0} МБ/с)";
        return null;
    }

    /// <summary>Средняя скорость сети (байт/с) с прошлого вызова; при первом вызове — null.</summary>
    private double? NetworkRate()
    {
        long bytes = 0;
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                var stats = n.GetIPStatistics();
                bytes += stats.BytesReceived + stats.BytesSent;
            }
        }
        catch (NetworkInformationException)
        {
            return null;
        }
        var now = DateTime.UtcNow;
        var previous = _lastBytes;
        var seconds = (now - _lastAt).TotalSeconds;
        _lastBytes = bytes;
        _lastAt = now;
        if (previous < 0 || seconds < 1 || bytes < previous)
            return null;
        return (bytes - previous) / seconds;
    }
}
