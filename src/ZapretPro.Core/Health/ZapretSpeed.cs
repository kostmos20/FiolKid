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
    /// <summary>Скорость обхода с уже запущенным winws: что открылось и сколько это заняло.</summary>
    Task<(int Ok, int Total, double Milliseconds)> MeasureZapretAsync(CancellationToken ct);
}

/// <summary>
/// Насколько быстро работает текущий способ обхода. Проверка «открылось или нет» здесь мало что даёт:
/// почти все альты открывают YouTube, но одни — быстро, другие — с задержками и медленной загрузкой.
/// Поэтому качаем то, что качает браузер: главную YouTube целиком, картинку превью и главную Discord,
/// плюс рукопожатие с видеосервером. Каждый адрес — два раза, берём лучшее время (первый раз
/// бывает медленнее из-за DNS).
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

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);
    private const int MaxBytes = 2_000_000;

    public static async Task<(int Ok, int Total, double Milliseconds)> MeasureAsync(CancellationToken ct)
    {
        var results = await Task.WhenAll(Urls.Select(async url =>
        {
            double? best = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var time = await FetchAsync(url, ct);
                if (time is not null && (best is null || time < best))
                    best = time;
            }
            return best;
        }));
        // Не открылся адрес — штраф как за полный тайм-аут: медленный, но рабочий альт лучше неработающего
        return (results.Count(r => r is not null), Urls.Length, results.Sum(r => r ?? Timeout.TotalMilliseconds * 2));
    }

    private static async Task<double?> FetchAsync(string url, CancellationToken ct)
    {
        // Каждый раз новое соединение: иначе второй замер шёл бы по уже открытому
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectTimeout = Timeout,
            AutomaticDecompression = DecompressionMethods.All,
        };
        using var http = new HttpClient(handler) { Timeout = Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[64 * 1024];
            var total = 0;
            int read;
            while (total < MaxBytes && (read = await body.ReadAsync(buffer, ct)) > 0)
                total += read;
            return sw.Elapsed.TotalMilliseconds;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
