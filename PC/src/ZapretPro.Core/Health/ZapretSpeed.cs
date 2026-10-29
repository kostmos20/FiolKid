using System.Diagnostics;
using System.Net;

namespace ZapretPro.Core.Health;

/// <summary>Замер одного способа обхода (альта): сколько адресов открылось и за сколько.</summary>
public sealed record StrategyScore(string Id, int Ok, int Total, double Milliseconds)
{
    public bool AllOk => Ok == Total;
}

public interface IZapretSpeedProbe
{
    /// <summary>Быстрый отсев: открываются ли адреса вообще (одна попытка, короткое ожидание, без скачивания).</summary>
    Task<(int Ok, int Total, double Milliseconds)> QuickCheckZapretAsync(CancellationToken ct);

    /// <summary>Точный замер скорости с уже запущенным winws: что открылось и сколько заняла загрузка.</summary>
    Task<(int Ok, int Total, double Milliseconds)> MeasureZapretAsync(CancellationToken ct);
}

/// <summary>
/// Насколько быстро работает текущий способ обхода. Проверка «открылось или нет» здесь мало что даёт:
/// почти все альты открывают YouTube, но одни — быстро, другие — с задержками и медленной загрузкой.
/// Поэтому для точного замера качаем то, что качает браузер: главную YouTube целиком, картинку превью
/// и главную Discord, плюс рукопожатие с видеосервером.
///
/// Подбор идёт в два прохода: быстрый отсев всех альтов (<see cref="QuickCheckAsync"/>, пара секунд на альт)
/// и точный замер только у лучших (<see cref="MeasureAsync"/>). Время в основном уходило на неработающие
/// альты: они ждали тайм-аута по два раза — теперь один раз и коротко.
/// </summary>
public static class ZapretSpeed
{
    private static readonly string[] Urls =
    [
        "https://www.youtube.com/",
        "https://i.ytimg.com/vi/jNQXAC9IVRw/hqdefault.jpg",
        "https://redirector.googlevideo.com/",
        "https://discord.com/",
    ];

    // Рабочий альт отвечает за доли секунды; не ответил за 2,5 с — для видео он всё равно не годится
    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);
    private const int MaxBytes = 2_000_000;

    public static async Task<(int Ok, int Total, double Milliseconds)> QuickCheckAsync(CancellationToken ct)
    {
        var results = await Task.WhenAll(Urls.Select(url => FetchAsync(url, QuickTimeout, download: false, ct)));
        return Sum(results, QuickTimeout);
    }

    public static async Task<(int Ok, int Total, double Milliseconds)> MeasureAsync(CancellationToken ct)
    {
        var results = await Task.WhenAll(Urls.Select(async url =>
        {
            // Второй раз — только если первый удался: он точнее (DNS и TLS уже прогреты), а провал
            // повторять незачем — это лишние секунды ожидания
            var first = await FetchAsync(url, Timeout, download: true, ct);
            if (first is null)
                return null;
            var second = await FetchAsync(url, Timeout, download: true, ct);
            return second is null ? first : Math.Min(first.Value, second.Value);
        }));
        return Sum(results, Timeout);
    }

    /// <summary>Не открылся адрес — штраф как за двойной тайм-аут: медленный, но рабочий альт лучше неработающего.</summary>
    private static (int Ok, int Total, double Milliseconds) Sum(double?[] results, TimeSpan timeout) =>
        (results.Count(r => r is not null), Urls.Length, results.Sum(r => r ?? timeout.TotalMilliseconds * 2));

    private static async Task<double?> FetchAsync(string url, TimeSpan timeout, bool download, CancellationToken ct)
    {
        // Каждый раз новое соединение: иначе второй замер шёл бы по уже открытому
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = timeout,
            AutomaticDecompression = DecompressionMethods.All,
        };
        using var http = new HttpClient(handler) { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (download)
            {
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[64 * 1024];
                var total = 0;
                int read;
                while (total < MaxBytes && (read = await body.ReadAsync(buffer, ct)) > 0)
                    total += read;
            }
            return sw.Elapsed.TotalMilliseconds;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
