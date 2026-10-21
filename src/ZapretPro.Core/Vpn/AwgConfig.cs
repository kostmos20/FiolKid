using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZapretPro.Core.Key;

namespace ZapretPro.Core.Vpn;

/// <summary>
/// Конфиг AmneziaWG из ключа vpn://. В ключе он лежит готовым текстом в awg.last_config → config
/// (тот же формат, что у awg-quick). Мы его не пересобираем по полям, а берём как есть и
/// меняем только то, что касается маршрутизации и безопасности.
/// </summary>
public sealed class AwgConfig
{
    // Убираем из конфига знакомого:
    //  DNS — иначе Windows начнёт резолвить всё через VPN-сервер, а российские сайты должны работать как обычно;
    //  AllowedIPs — подставляем свои (раздельная маршрутизация);
    //  Table — маршруты должны создаваться;
    //  Pre/PostUp/Down — это команды, которые служба туннеля выполнила бы от имени SYSTEM.
    private static readonly HashSet<string> DroppedInterfaceKeys =
        new(StringComparer.OrdinalIgnoreCase) { "DNS", "Table", "PreUp", "PostUp", "PreDown", "PostDown" };

    private static readonly HashSet<string> DroppedPeerKeys = new(StringComparer.OrdinalIgnoreCase) { "AllowedIPs" };

    private readonly List<KeyValuePair<string, string>> _interface;
    private readonly List<KeyValuePair<string, string>> _peer;

    private AwgConfig(List<KeyValuePair<string, string>> iface, List<KeyValuePair<string, string>> peer)
    {
        _interface = iface;
        _peer = peer;

        var endpoint = Get(_peer, "Endpoint")!;
        var colon = endpoint.LastIndexOf(':');
        EndpointHost = (colon > 0 ? endpoint[..colon] : endpoint).Trim('[', ']');
    }

    /// <summary>Адрес VPN-сервера (IP или имя) — его нельзя пускать в сам туннель.</summary>
    public string EndpointHost { get; }

    public static AwgConfig FromKey(ParsedKey key)
    {
        if (key.Protocol != VpnProtocol.AmneziaWg)
            throw new KeyParseException(key.UnsupportedReason ?? "Этот ключ не для AmneziaWG.");

        var awg = key.Container["awg"] as JsonObject;
        var lastConfig = awg?["last_config"] switch
        {
            JsonObject o => o,
            JsonValue v when v.TryGetValue<string>(out var s) => TryParseObject(s),
            _ => null,
        };
        var text = lastConfig?["config"] is JsonValue c && c.TryGetValue<string>(out var t) ? t : null;
        if (string.IsNullOrWhiteSpace(text))
            throw new KeyParseException("В ключе нет настроек подключения AmneziaWG. Попроси у знакомого ключ заново.");

        var mtu = lastConfig?["mtu"] is JsonValue m && m.TryGetValue<string>(out var ms) ? ms : null;
        return Parse(text, mtu);
    }

    /// <summary>Разбирает текст в формате awg-quick. <paramref name="defaultMtu"/> — если в тексте MTU не задан.</summary>
    public static AwgConfig Parse(string text, string? defaultMtu = null)
    {
        var iface = new List<KeyValuePair<string, string>>();
        var peer = new List<KeyValuePair<string, string>>();
        List<KeyValuePair<string, string>>? section = null;
        var peers = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (name.Equals("Interface", StringComparison.OrdinalIgnoreCase))
                    section = iface;
                else if (name.Equals("Peer", StringComparison.OrdinalIgnoreCase))
                {
                    peers++;
                    section = peer;
                }
                else
                    section = null;
                continue;
            }

            var eq = line.IndexOf('=');
            if (section is null || eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            // Пустые значения и неподставленные шаблоны Amnezia ($PRIMARY_DNS и т. п.) служба туннеля не примет
            if (value.Length == 0 || value.Contains('$'))
                continue;

            var dropped = section == iface ? DroppedInterfaceKeys : DroppedPeerKeys;
            if (!dropped.Contains(key))
                section.Add(new(key, value));
        }

        if (peers > 1)
            throw new KeyParseException("В ключе несколько серверов сразу — такой ключ пока не поддерживается.");

        foreach (var (list, key) in new[] { (iface, "PrivateKey"), (iface, "Address"), (peer, "PublicKey"), (peer, "Endpoint") })
        {
            if (Get(list, key) is null)
                throw new KeyParseException("Ключ повреждён: в настройках AmneziaWG чего-то не хватает. Попроси у знакомого ключ заново.");
        }

        if (Get(iface, "MTU") is null && int.TryParse(defaultMtu, out var mtu) && mtu is >= 576 and <= 9000)
            iface.Add(new("MTU", mtu.ToString()));

        return new AwgConfig(iface, peer);
    }

    /// <summary>
    /// Текст .conf для службы AmneziaWG. <paramref name="tableOff"/> — служба не создаёт маршруты
    /// (и не включает блокировку остального трафика): маршрутами управляет программа (<see cref="TunnelRoutes"/>).
    /// </summary>
    public string ToConf(IEnumerable<string> allowedIps, bool tableOff = false)
    {
        var routes = string.Join(", ", allowedIps);
        if (routes.Length == 0)
            throw new ArgumentException("Нужен хотя бы один маршрут.", nameof(allowedIps));

        var sb = new StringBuilder();
        sb.AppendLine("[Interface]");
        foreach (var (key, value) in _interface)
            sb.Append(key).Append(" = ").AppendLine(value);
        if (tableOff)
            sb.AppendLine("Table = off");
        sb.AppendLine();
        sb.AppendLine("[Peer]");
        foreach (var (key, value) in _peer)
            sb.Append(key).Append(" = ").AppendLine(value);
        sb.Append("AllowedIPs = ").AppendLine(routes);
        return sb.ToString();
    }

    /// <summary>Без секретов: ключи и адрес сервера в логи не попадают.</summary>
    public override string ToString() => "AwgConfig";

    private static string? Get(List<KeyValuePair<string, string>> list, string key) =>
        list.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static JsonObject? TryParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
