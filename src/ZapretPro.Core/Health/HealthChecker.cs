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

    /// <summary>
    /// Какие программы (Telegram, AyuGram…) сейчас подключены к нашему прокси. Пусто — Telegram ещё не
    /// подключили к прокси, и он не работает, даже если сам прокси исправен. Null — не проверяли.
    /// </summary>
    public IReadOnlyList<string>? TelegramClients { get; init; }

    /// <summary>Почему не прошла проверка сервиса — для журнала и отчёта.</summary>
    public IReadOnlyDictionary<Service, string> Problems { get; init; } = new Dictionary<Service, string>();

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
/// Не прошедшую проверку повторяем один раз: единичный сбой (например, в момент
/// подключения VPN) не должен красить индикатор на всё время до следующей проверки.
/// </summary>
public sealed class HealthChecker : IHealthProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private static readonly Dictionary<Service, string[]> Targets = new()
    {
        // Страница и видеосервер: без googlevideo страница откроется, а видео — нет
        [Service.YouTube] = ["https://www.youtube.com/", "https://redirector.googlevideo.com/"],
        [Service.Discord] = ["https://discord.com/"],
    };

    // Сайт из списка исключений zapret, не заблокирован в России
    private const string ControlUrl = "https://ya.ru/";

    // Служебная страница Cloudflare: отвечает текстом с полем loc= — страной, из которой виден запрос
    private const string AiTraceUrl = "https://chatgpt.com/cdn-cgi/trace";

    // Страны, откуда ChatGPT и Claude не работают. Если видят оттуда — значит, запрос шёл мимо VPN.
    private static readonly HashSet<string> AiBlockedCountries = ["RU", "BY", "CN", "HK", "MO", "IR", "KP", "CU", "SY"];

    // Основной дата-центр Telegram (в конфиге tg-ws-proxy он и DC4 указаны явно)
    private const short TelegramDc = 2;

    private readonly string? _telegramSecret;

    /// <param name="telegramSecret">Секрет локального прокси: без него до Telegram через прокси не достучаться.</param>
    public HealthChecker(string? telegramSecret = null)
    {
        _telegramSecret = telegramSecret;
    }

    public static bool IsAiCountryOk(string? country) => country is not null && !AiBlockedCountries.Contains(country);

    public async Task<HealthReport> CheckAsync(IReadOnlyCollection<Service> services, CancellationToken ct)
    {
        var report = await CheckOnceAsync(services, ct);
        if (services.Contains(Service.Telegram))
            report = report with { TelegramClients = FindTelegramClients() };

        var failed = services.Where(s => !report[s]).ToList();
        if (failed.Count == 0 || !report.InternetOk)
            return report;

        await Task.Delay(RetryDelay, ct);
        var retry = await CheckOnceAsync(failed, ct);
        var merged = report.Services.ToDictionary(kv => kv.Key, kv => kv.Value || retry[kv.Key]);
        var problems = report.Problems
            .Where(p => !merged[p.Key])
            .ToDictionary(p => p.Key, p => retry.Problems.GetValueOrDefault(p.Key, p.Value));
        return report with
        {
            Services = merged,
            Problems = problems,
            AiCountry = retry.AiCountry ?? report.AiCountry,
        };
    }

    private async Task<HealthReport> CheckOnceAsync(IReadOnlyCollection<Service> services, CancellationToken ct)
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

        var control = OpenAsync(http, ControlUrl, ct);
        var checks = services.ToDictionary(s => s, s => CheckServiceAsync(http, s, ct));

        await Task.WhenAll(checks.Values.Cast<Task>().Append(control));
        var problems = checks
            .Where(c => c.Value.Result.Problem is not null)
            .ToDictionary(c => c.Key, c => c.Value.Result.Problem!);
        return new HealthReport(checks.ToDictionary(c => c.Key, c => c.Value.Result.Problem is null), control.Result is null)
        {
            AiCountry = checks.TryGetValue(Service.Ai, out var ai) ? ai.Result.Country : null,
            Problems = problems,
        };
    }

    private async Task<(string? Problem, string? Country)> CheckServiceAsync(HttpClient http, Service service, CancellationToken ct)
    {
        switch (service)
        {
            case Service.Telegram:
                if (_telegramSecret is null)
                    return (await TelegramProxy.IsPortOpenAsync(ct) ? null : "прокси не запущен", null);
                return (await TelegramProbe.CheckAsync(TelegramProxy.Host, TelegramProxy.Port, _telegramSecret,
                    TelegramDc, Timeout, ct), null);

            case Service.Ai:
                var (trace, problem) = await GetStringAsync(http, AiTraceUrl, ct);
                var country = trace is null ? null : ParseTraceCountry(trace);
                if (problem is not null)
                    return ($"chatgpt.com: {problem}", null);
                return IsAiCountryOk(country)
                    ? (null, country)
                    : ($"ChatGPT видит запрос из страны {country ?? "?"} — значит, мимо VPN", country);
        }

        if (!Targets.TryGetValue(service, out var urls))
            return ("неизвестный сервис", null);

        var results = await Task.WhenAll(urls.Select(async u => (Url: u, Problem: await OpenAsync(http, u, ct))));
        var failed = results.Where(r => r.Problem is not null).ToList();
        return (failed.Count == 0 ? null : string.Join("; ", failed.Select(f => $"{new Uri(f.Url).Host}: {f.Problem}")), null);
    }

    /// <summary>Null — открылся, иначе — коротко, что пошло не так.</summary>
    private static async Task<string?> OpenAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url) { Version = HttpVersion.Version11 };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return null;
        }
        catch (Exception e) when (IsNetworkError(e, ct))
        {
            return Describe(e);
        }
    }

    private static async Task<(string? Text, string? Problem)> GetStringAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            return (await http.GetStringAsync(url, ct), null);
        }
        catch (Exception e) when (IsNetworkError(e, ct))
        {
            return (null, Describe(e));
        }
    }

    private static bool IsNetworkError(Exception e, CancellationToken ct) =>
        e is HttpRequestException or TaskCanceledException or SocketException or IOException && !ct.IsCancellationRequested;

    private static string Describe(Exception e) => e switch
    {
        TaskCanceledException => $"нет ответа за {Timeout.TotalSeconds:0} с",
        HttpRequestException { InnerException: SocketException s } => $"не соединиться ({s.SocketErrorCode})",
        HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } => "сбой TLS (похоже на блокировку)",
        HttpRequestException { InnerException: IOException } => "соединение оборвалось (похоже на блокировку)",
        _ => e.GetType().Name,
    };

    private static IReadOnlyList<string>? FindTelegramClients()
    {
        try
        {
            return Zapret.TelegramClients.ConnectedTo(TelegramProxy.Port);
        }
        catch (System.ComponentModel.Win32Exception)
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
}
