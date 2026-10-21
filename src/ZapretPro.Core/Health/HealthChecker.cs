using System.Net;
using System.Net.Sockets;
using ZapretPro.Core.Zapret;

namespace ZapretPro.Core.Health;

public enum Service
{
    YouTube,
    Discord,
    Telegram,
    Ai,
}

/// <summary>
/// Результат проверки. <see cref="InternetOk"/> — открывается ли контрольный российский сайт:
/// если нет, проблема не в обходе блокировок и менять стратегию бессмысленно.
/// </summary>
public sealed record HealthReport(IReadOnlyDictionary<Service, bool> Services, bool InternetOk)
{
    public bool this[Service service] => Services.TryGetValue(service, out var ok) && ok;

    public int CountOk(IEnumerable<Service> services) => services.Count(s => this[s]);
}

public interface IHealthProbe
{
    Task<HealthReport> CheckAsync(IReadOnlyCollection<Service> services, CancellationToken ct);
}

/// <summary>
/// Проверяет именно целевые сервисы, а не «есть ли интернет»: HTTPS-запрос,
/// успех = прошло TLS-рукопожатие и пришли заголовки ответа (любой код).
/// Каждый раз новые соединения: старое, установленное до блокировки, показало бы ложный успех.
/// </summary>
public sealed class HealthChecker : IHealthProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(7);

    private static readonly Dictionary<Service, string[]> Targets = new()
    {
        // Страница и видеосервер: без googlevideo страница откроется, а видео — нет
        [Service.YouTube] = ["https://www.youtube.com/", "https://redirector.googlevideo.com/"],
        [Service.Discord] = ["https://discord.com/"],
    };

    // Сайт из списка исключений zapret, не заблокирован в России
    private const string ControlUrl = "https://ya.ru/";

    public async Task<HealthReport> CheckAsync(IReadOnlyCollection<Service> services, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = Timeout,
            PooledConnectionLifetime = TimeSpan.Zero,
        };
        using var http = new HttpClient(handler) { Timeout = Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        var control = OpensAsync(http, ControlUrl, ct);
        var checks = services.ToDictionary(s => s, s => CheckServiceAsync(http, s, ct));

        await Task.WhenAll(checks.Values.Append(control));
        return new HealthReport(checks.ToDictionary(c => c.Key, c => c.Value.Result), control.Result);
    }

    private static async Task<bool> CheckServiceAsync(HttpClient http, Service service, CancellationToken ct)
    {
        if (service == Service.Telegram)
            return await TelegramProxy.IsPortOpenAsync(ct);

        if (!Targets.TryGetValue(service, out var urls))
            return false;

        var results = await Task.WhenAll(urls.Select(u => OpensAsync(http, u, ct)));
        return results.All(ok => ok);
    }

    private static async Task<bool> OpensAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url) { Version = HttpVersion.Version11 };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or SocketException or IOException
                                  && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
