using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace ZapretPro.Core.Vpn;

/// <summary>
/// Туннель, который умеет держаться за один сервер без переподключения: на время игры. Без этого sing-box
/// сам перескакивал бы на сервер побыстрее, и игра теряла бы соединение.
/// </summary>
public interface IServerPin
{
    /// <summary>Закрепить сервер, который сейчас самый быстрый. Возвращает его имя или null, если не вышло.</summary>
    Task<string?> PinAsync(CancellationToken ct);

    /// <summary>Снова выбирать сервер автоматически.</summary>
    Task UnpinAsync(CancellationToken ct);

    /// <summary>Закреплённый сервер умер — перейти на следующий по прошлым замерам. Имя нового или null.</summary>
    Task<string?> SwitchToSpareAsync(CancellationToken ct);
}

/// <summary>
/// Управление запущенным sing-box через его локальный API (clash_api, только 127.0.0.1, со случайным
/// паролем на каждый запуск). В конфиге есть переключатель <c>proxy</c>: «auto» — автовыбор по задержке,
/// или конкретный сервер.
/// </summary>
public sealed class SingBoxApi : IServerPin
{
    public const string SelectorTag = "proxy";
    public const string AutoTag = "auto";

    private readonly Func<(int Port, string Secret)?> _endpoint;
    private readonly Func<string, string> _displayName;
    private string? _pinned;

    public SingBoxApi(Func<(int Port, string Secret)?> endpoint, Func<string, string>? displayName = null)
    {
        _endpoint = endpoint;
        _displayName = displayName ?? (t => t);
    }

    /// <summary>Вписать в конфиг доступ к API: свободный порт и случайный пароль. Конфиг для сравнения не меняется.</summary>
    public static (string Conf, int Port, string Secret) WithApi(string conf)
    {
        var json = JsonNode.Parse(conf)!.AsObject();
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        json["experimental"] = new JsonObject
        {
            ["clash_api"] = new JsonObject { ["external_controller"] = $"127.0.0.1:{port}", ["secret"] = secret },
        };
        return (json.ToJsonString(), port, secret);
    }

    public async Task<string?> PinAsync(CancellationToken ct)
    {
        var auto = await GetAsync(AutoTag, ct);
        if (auto?["now"]?.GetValue<string>() is not { Length: > 0 } now)
            return null;
        if (!await SelectAsync(now, ct))
            return null;
        _pinned = now;
        return _displayName(now);
    }

    public async Task UnpinAsync(CancellationToken ct)
    {
        _pinned = null;
        await SelectAsync(AutoTag, ct);
    }

    public async Task<string?> SwitchToSpareAsync(CancellationToken ct)
    {
        var auto = await GetAsync(AutoTag, ct);
        if (auto?["all"] is not JsonArray all)
            return null;
        string? best = null;
        var bestDelay = int.MaxValue;
        foreach (var tag in all.Select(t => t?.GetValue<string>()).OfType<string>())
        {
            if (tag == _pinned)
                continue;
            var node = await GetAsync(tag, ct);
            var delay = node?["history"] is JsonArray { Count: > 0 } history ? history[^1]?["delay"]?.GetValue<int>() ?? 0 : 0;
            if (delay > 0 && delay < bestDelay)
            {
                best = tag;
                bestDelay = delay;
            }
        }
        if (best is null || !await SelectAsync(best, ct))
            return null;
        _pinned = best;
        return _displayName(best);
    }

    private async Task<JsonNode?> GetAsync(string tag, CancellationToken ct)
    {
        if (Client() is not { } http)
            return null;
        using (http)
        {
            try
            {
                return JsonNode.Parse(await http.GetStringAsync($"proxies/{Uri.EscapeDataString(tag)}", ct));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                        && !ct.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    private async Task<bool> SelectAsync(string tag, CancellationToken ct)
    {
        if (Client() is not { } http)
            return false;
        using (http)
        {
            try
            {
                var body = new StringContent(new JsonObject { ["name"] = tag }.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await http.PutAsync($"proxies/{SelectorTag}", body, ct);
                return response.IsSuccessStatusCode;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return false;
            }
        }
    }

    private HttpClient? Client()
    {
        if (_endpoint() is not { } api)
            return null;
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{api.Port}/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", api.Secret);
        return http;
    }
}
