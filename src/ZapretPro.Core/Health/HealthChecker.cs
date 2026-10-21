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
    /// <summary>Страна, из которой нейросети видят запрос (код Cloudflare, например NL). Null — не проверяли или не ответили.</summary>
    public string? AiCountry { get; init; }

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

    public static bool IsAiCountryOk(string? country) => country is not null && !AiBlockedCountries.Contains(country);

    // Сайт из списка исключений zapret, не заблокирован в России
    private const string ControlUrl = "https://ya.ru/";

    // tg-ws-proxy ходит к Telegram через WebSocket на *.web.telegram.org: если он недоступен, прокси бесполезен
    private const string TelegramUpstreamUrl = "https://web.telegram.org/";

    // Служебная страница Cloudflare: отвечает текстом с полем loc= — страной, из которой виден запрос
    private const string AiTraceUrl = "https://chatgpt.com/cdn-cgi/trace";

    // Страны, откуда ChatGPT и Claude не работают. Если видят оттуда — значит, запрос шёл мимо VPN.
    private static readonly HashSet<string> AiBlockedCountries = ["RU", "BY", "CN", "HK", "MO", "IR", "KP", "CU", "SY"];

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
        var ai = services.Contains(Service.Ai) ? AiCountryAsync(http, ct) : Task.FromResult<string?>(null);
        var checks = services.ToDictionary(s => s, s => CheckServiceAsync(http, s, ai, ct));

        await Task.WhenAll(checks.Values.Append(control));
        return new HealthReport(checks.ToDictionary(c => c.Key, c => c.Value.Result), control.Result)
        {
            AiCountry = await ai,
        };
    }

    private static async Task<bool> CheckServiceAsync(HttpClient http, Service service, Task<string?> aiCountry, CancellationToken ct)
    {
        switch (service)
        {
            case Service.Telegram:
                return await TelegramProxy.IsPortOpenAsync(ct) && await OpensAsync(http, TelegramUpstreamUrl, ct);
            case Service.Ai:
                return IsAiCountryOk(await aiCountry);
        }

        if (!Targets.TryGetValue(service, out var urls))
            return false;

        var results = await Task.WhenAll(urls.Select(u => OpensAsync(http, u, ct)));
        return results.All(ok => ok);
    }

    private static async Task<string?> AiCountryAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            var text = await http.GetStringAsync(AiTraceUrl, ct);
            return ParseTraceCountry(text);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or SocketException or IOException
                                  && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static string? ParseTraceCountry(string trace) => trace
        .Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.StartsWith("loc=", StringComparison.Ordinal))
        .Select(l => l[4..].ToUpperInvariant())
        .FirstOrDefault(c => c.Length == 2);

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
