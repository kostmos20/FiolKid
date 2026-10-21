using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZapretPro.Core.Vpn;

/// <summary>Сервер из подписки — уже в виде outbound для sing-box. Ссылки и ключи наружу не выводить.</summary>
public sealed record ProxyServer(string Protocol, string Host, int Port, string Name, JsonObject Outbound)
{
    /// <summary>Насколько сервер хорош «по умолчанию»: Reality обходит блокировки лучше всего.</summary>
    public int Preference => Protocol switch
    {
        "vless" when Outbound["tls"]?["reality"] is not null => 0,
        "hysteria2" => 1,
        "trojan" or "vless" => 2,
        _ => 3,
    };

    /// <summary>Без адреса и ключей — для журнала.</summary>
    public override string ToString() => $"{Protocol} «{Name}»";
}

/// <summary>Что пришло по ссылке-подписке.</summary>
public sealed record ParsedSubscription(
    string? Title,
    DateTimeOffset? Expires,
    TimeSpan UpdateInterval,
    IReadOnlyList<ProxyServer> Servers,
    int Skipped);

/// <summary>
/// Разбор подписки в формате, который понимают v2rayN, Happ, Hiddify: base64 (или просто текст)
/// со ссылками vless://, hysteria2://, trojan://, ss:// по одной в строке. Сервисная информация —
/// в заголовках ответа (название, срок, как часто обновлять).
/// </summary>
public static class SubscriptionParser
{
    private static readonly TimeSpan DefaultUpdateInterval = TimeSpan.FromHours(3);

    // Отпечатки TLS, которые понимает sing-box (utls)
    private static readonly HashSet<string> Fingerprints =
        ["chrome", "firefox", "edge", "safari", "360", "qq", "ios", "android", "random", "randomized"];

    public static ParsedSubscription Parse(string body, IReadOnlyDictionary<string, string>? headers = null)
    {
        var text = DecodeBody(body);
        var servers = new List<ProxyServer>();
        var skipped = 0;
        foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("://")))
        {
            var server = ParseLink(line);
            if (server is null)
                skipped++;
            else
                servers.Add(server);
        }

        string? title = null;
        DateTimeOffset? expires = null;
        var interval = DefaultUpdateInterval;
        if (headers is not null)
        {
            if (headers.TryGetValue("profile-title", out var t))
                title = DecodeTitle(t);
            if (headers.TryGetValue("subscription-userinfo", out var info)
                && Regex.Match(info, @"expire=(\d+)") is { Success: true } m
                && long.TryParse(m.Groups[1].Value, out var unix) && unix > 0)
                expires = DateTimeOffset.FromUnixTimeSeconds(unix);
            if (headers.TryGetValue("profile-update-interval", out var hours)
                && double.TryParse(hours, System.Globalization.CultureInfo.InvariantCulture, out var h) && h is > 0 and < 24 * 30)
                interval = TimeSpan.FromHours(h);
        }

        return new ParsedSubscription(title, expires, interval, servers, skipped);
    }

    /// <summary>Подписка обычно — base64 со ссылками; бывает и просто текст со ссылками.</summary>
    internal static string DecodeBody(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Contains("://"))
            return trimmed;
        var compact = Regex.Replace(trimmed, @"\s+", "");
        foreach (var candidate in new[] { compact, compact.Replace('-', '+').Replace('_', '/') })
        {
            try
            {
                var padded = candidate + new string('=', (4 - candidate.Length % 4) % 4);
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
                if (decoded.Contains("://"))
                    return decoded;
            }
            catch (FormatException)
            {
            }
        }
        return trimmed;
    }

    private static string? DecodeTitle(string value)
    {
        if (!value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
            return value;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value[7..]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Одна ссылка → сервер. Null — протокол или транспорт, который sing-box не поддерживает, или битая ссылка.</summary>
    public static ProxyServer? ParseLink(string link)
    {
        try
        {
            var scheme = link[..link.IndexOf("://", StringComparison.Ordinal)].ToLowerInvariant();
            return scheme switch
            {
                "vless" => ParseVless(link),
                "trojan" => ParseTrojan(link),
                "hysteria2" or "hy2" => ParseHysteria2(link),
                "ss" => ParseShadowsocks(link),
                _ => null,
            };
        }
        catch (Exception e) when (e is UriFormatException or FormatException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static ProxyServer? ParseVless(string link)
    {
        var (user, host, port, query, name) = Split(link);
        if (!Guid.TryParse(user, out _))
            return null;

        var outbound = new JsonObject
        {
            ["type"] = "vless",
            ["server"] = host,
            ["server_port"] = port,
            ["uuid"] = user,
            ["packet_encoding"] = "xudp",
        };
        var transport = query.GetValueOrDefault("type", "tcp");
        if (query.GetValueOrDefault("flow") is { Length: > 0 } flow && transport is "tcp" or "raw")
            outbound["flow"] = flow;
        if (!AddTls(outbound, query, host) || !AddTransport(outbound, query, transport))
            return null;
        return new ProxyServer("vless", host, port, name, outbound);
    }

    private static ProxyServer? ParseTrojan(string link)
    {
        var (user, host, port, query, name) = Split(link);
        if (user.Length == 0)
            return null;
        var outbound = new JsonObject { ["type"] = "trojan", ["server"] = host, ["server_port"] = port, ["password"] = user };
        // У trojan TLS включён по умолчанию
        if (!query.ContainsKey("security"))
            query["security"] = "tls";
        if (!AddTls(outbound, query, host) || !AddTransport(outbound, query, query.GetValueOrDefault("type", "tcp")))
            return null;
        return new ProxyServer("trojan", host, port, name, outbound);
    }

    private static ProxyServer? ParseHysteria2(string link)
    {
        var (user, host, port, query, name) = Split(link);
        if (user.Length == 0 || query.ContainsKey("mport"))
            return null; // «прыжки по портам» — без них тоже обойдёмся, остальные серверы есть
        var tls = new JsonObject
        {
            ["enabled"] = true,
            ["server_name"] = query.GetValueOrDefault("sni", host),
            ["alpn"] = new JsonArray("h3"),
        };
        if (query.GetValueOrDefault("insecure") == "1")
            tls["insecure"] = true;
        var outbound = new JsonObject
        {
            ["type"] = "hysteria2",
            ["server"] = host,
            ["server_port"] = port,
            ["password"] = user,
            ["tls"] = tls,
        };
        if (query.GetValueOrDefault("obfs") is { Length: > 0 } obfs)
        {
            if (obfs != "salamander")
                return null;
            outbound["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = query.GetValueOrDefault("obfs-password", "") };
        }
        return new ProxyServer("hysteria2", host, port, name, outbound);
    }

    private static ProxyServer? ParseShadowsocks(string link)
    {
        // ss://BASE64(метод:пароль)@хост:порт#имя или ss://BASE64(метод:пароль@хост:порт)#имя
        var body = link[5..];
        var name = "";
        var hash = body.IndexOf('#');
        if (hash >= 0)
        {
            name = Uri.UnescapeDataString(body[(hash + 1)..]);
            body = body[..hash];
        }
        var queryStart = body.IndexOf('?');
        if (queryStart >= 0)
        {
            if (body[queryStart..].Contains("plugin", StringComparison.OrdinalIgnoreCase))
                return null; // плагины (obfs, v2ray-plugin) sing-box не запускает
            body = body[..queryStart];
        }
        body = body.TrimEnd('/');

        string userInfo, hostPort;
        var at = body.LastIndexOf('@');
        if (at >= 0)
        {
            userInfo = DecodeBase64Loose(Uri.UnescapeDataString(body[..at])) ?? Uri.UnescapeDataString(body[..at]);
            hostPort = body[(at + 1)..];
        }
        else
        {
            var decoded = DecodeBase64Loose(body) ?? "";
            at = decoded.LastIndexOf('@');
            if (at < 0)
                return null;
            userInfo = decoded[..at];
            hostPort = decoded[(at + 1)..];
        }

        var colon = userInfo.IndexOf(':');
        var portColon = hostPort.LastIndexOf(':');
        if (colon <= 0 || portColon <= 0 || !int.TryParse(hostPort[(portColon + 1)..], out var port))
            return null;
        var host = hostPort[..portColon].Trim('[', ']');
        var outbound = new JsonObject
        {
            ["type"] = "shadowsocks",
            ["server"] = host,
            ["server_port"] = port,
            ["method"] = userInfo[..colon],
            ["password"] = userInfo[(colon + 1)..],
        };
        return new ProxyServer("shadowsocks", host, port, name, outbound);
    }

    /// <summary>TLS и Reality. False — режим, который мы не поддерживаем.</summary>
    private static bool AddTls(JsonObject outbound, Dictionary<string, string> query, string host)
    {
        var security = query.GetValueOrDefault("security", "none");
        if (security is "none" or "")
            return true;
        if (security is not ("tls" or "reality"))
            return false;

        var tls = new JsonObject
        {
            ["enabled"] = true,
            ["server_name"] = query.GetValueOrDefault("sni") is { Length: > 0 } sni ? sni : query.GetValueOrDefault("host", host),
        };
        if (query.GetValueOrDefault("alpn") is { Length: > 0 } alpn)
            tls["alpn"] = new JsonArray(alpn.Split(',').Select(a => (JsonNode)a.Trim()).ToArray());
        if (query.GetValueOrDefault("allowInsecure") is "1" or "true")
            tls["insecure"] = true;

        var fp = query.GetValueOrDefault("fp", "").ToLowerInvariant();
        if (security == "reality")
        {
            if (query.GetValueOrDefault("pbk") is not { Length: > 0 } publicKey)
                return false;
            tls["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = publicKey, ["short_id"] = query.GetValueOrDefault("sid", "") };
            // Reality без uTLS не работает
            if (!Fingerprints.Contains(fp))
                fp = "chrome";
        }
        if (Fingerprints.Contains(fp))
            tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };

        outbound["tls"] = tls;
        return true;
    }

    /// <summary>Транспорт поверх TCP. False — такой sing-box не умеет (xhttp, kcp и т. п.).</summary>
    private static bool AddTransport(JsonObject outbound, Dictionary<string, string> query, string transport)
    {
        switch (transport)
        {
            case "tcp" or "raw" or "":
                // «Маскировка под HTTP» у TCP — редкость, sing-box её не поддерживает
                return query.GetValueOrDefault("headerType", "none") is "none" or "";
            case "grpc":
                outbound["transport"] = new JsonObject { ["type"] = "grpc", ["service_name"] = query.GetValueOrDefault("serviceName", "") };
                return true;
            case "ws":
                var ws = new JsonObject { ["type"] = "ws", ["path"] = query.GetValueOrDefault("path", "/") };
                if (query.GetValueOrDefault("host") is { Length: > 0 } wsHost)
                    ws["headers"] = new JsonObject { ["Host"] = wsHost };
                outbound["transport"] = ws;
                return true;
            case "httpupgrade":
                var upgrade = new JsonObject { ["type"] = "httpupgrade", ["path"] = query.GetValueOrDefault("path", "/") };
                if (query.GetValueOrDefault("host") is { Length: > 0 } upHost)
                    upgrade["host"] = upHost;
                outbound["transport"] = upgrade;
                return true;
            case "http" or "h2":
                var http = new JsonObject { ["type"] = "http", ["path"] = query.GetValueOrDefault("path", "/") };
                if (query.GetValueOrDefault("host") is { Length: > 0 } httpHost)
                    http["host"] = new JsonArray(httpHost.Split(',').Select(h => (JsonNode)h.Trim()).ToArray());
                outbound["transport"] = http;
                return true;
            default:
                return false;
        }
    }

    /// <summary>scheme://пользователь@хост:порт?параметры#имя</summary>
    private static (string User, string Host, int Port, Dictionary<string, string> Query, string Name) Split(string link)
    {
        var uri = new Uri(link);
        if (uri.Port <= 0)
            throw new FormatException("нет порта");
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            query.TryAdd(key, value);
        }
        return (Uri.UnescapeDataString(uri.UserInfo), uri.Host.Trim('[', ']'), uri.Port, query,
            Uri.UnescapeDataString(uri.Fragment.TrimStart('#')));
    }

    private static string? DecodeBase64Loose(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        s += new string('=', (4 - s.Length % 4) % 4);
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
