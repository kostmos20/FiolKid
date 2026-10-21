using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ZapretPro.Core.Health;

/// <summary>
/// Разбор «VPN подключён, а сайт не открывается». Браузер открывает сайт не так, как наша проверка:
/// может пойти по IPv6 (мимо туннеля, он только IPv4), получить от провайдера подменённый адрес
/// или узнать адрес сам через «безопасный DNS». Здесь проверяем каждую из этих причин для нескольких сайтов.
/// </summary>
public static class SiteProbe
{
    public static readonly string[] Sites = ["chatgpt.com", "claude.ai", "www.instagram.com"];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public static async Task<string> RunAsync(IReadOnlyCollection<string> routes, CancellationToken ct)
    {
        var networks = routes
            .Select(r => IPNetwork.TryParse(r, out var n) ? n : (IPNetwork?)null)
            .OfType<IPNetwork>()
            .ToList();

        var sb = new StringBuilder();
        var ipv6 = await HasIpv6Async(ct);
        sb.AppendLine($"IPv6 в интернет: {(ipv6 ? "ЕСТЬ — браузер может ходить мимо VPN" : "нет")}");

        foreach (var site in Sites)
        {
            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(site, ct);
            }
            catch (SocketException e)
            {
                sb.AppendLine($"{site}: DNS не отвечает ({e.SocketErrorCode})");
                continue;
            }

            var v4 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
            var v6 = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6).ToList();
            var routed = v4.Count(ip => networks.Any(n => n.Contains(ip)));
            sb.Append($"{site}: IPv4 {string.Join(",", v4)} (в VPN {routed} из {v4.Count})");
            if (v6.Count > 0)
                sb.Append($"; IPv6 {v6.Count} шт.");
            sb.Append($"; по IPv4: {await OpenOverIpv4Async(site, ct)}");
            if (ipv6 && v6.Count > 0)
                sb.Append($"; по IPv6: {await OpenAsync(site, AddressFamily.InterNetworkV6, ct)}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static Task<string> OpenOverIpv4Async(string site, CancellationToken ct) => OpenAsync(site, AddressFamily.InterNetwork, ct);

    /// <summary>Открывает https://site/ строго по указанному протоколу: «код ответа» или что пошло не так.</summary>
    private static async Task<string> OpenAsync(string site, AddressFamily family, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.Zero,
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, family, token);
                if (addresses.Length == 0)
                    throw new SocketException((int)SocketError.HostNotFound);
                var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses[0], context.DnsEndPoint.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var http = new HttpClient(handler) { Timeout = Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        try
        {
            using var response = await http.GetAsync($"https://{site}/", HttpCompletionOption.ResponseHeadersRead, ct);
            return $"ответ {(int)response.StatusCode}";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return $"нет ответа за {Timeout.TotalSeconds:0} с";
        }
        catch (HttpRequestException e)
        {
            return e.InnerException switch
            {
                SocketException s => $"не соединиться ({s.SocketErrorCode})",
                System.Security.Authentication.AuthenticationException => "сбой TLS",
                IOException => "соединение оборвалось",
                _ => e.Message,
            };
        }
    }

    /// <summary>Есть ли у компьютера рабочий IPv6 в интернет (пробуем DNS Cloudflare по IPv6).</summary>
    private static async Task<bool> HasIpv6Async(CancellationToken ct)
    {
        if (!Socket.OSSupportsIPv6)
            return false;
        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await socket.ConnectAsync(IPAddress.Parse("2606:4700:4700::1111"), 443, timeout.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
