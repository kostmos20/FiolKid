using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZapretPro.Core.Key;

/// <summary>
/// Разбор ключа Amnezia: «vpn://» + base64url от qCompress(JSON),
/// где qCompress — 4 байта длины (big-endian) и zlib-поток.
/// Как и сам клиент Amnezia, принимаем и несжатый JSON.
/// </summary>
public static class KeyParser
{
    public const string Prefix = "vpn://";

    // Конфиг Amnezia весит единицы килобайт; всё, что больше, — не ключ (или zip-бомба).
    private const int MaxConfigBytes = 1024 * 1024;

    public static ParsedKey Parse(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var raw = DecodeBase64Url(Payload(key));
        var json = TryDecompress(raw) ?? raw;
        return FromJson(ParseJson(json));
    }

    /// <summary>Ключ в каноническом виде: без пробелов и переносов, которые добавляют мессенджеры, и без «=» в конце.</summary>
    public static string Normalize(string key) => Prefix + Payload(key).TrimEnd('=');

    private static string Payload(string key)
    {
        var compact = string.Concat(key.Where(c => !char.IsWhiteSpace(c)));
        if (!compact.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new KeyParseException("Это не ключ: он должен начинаться с vpn://");

        var payload = compact[Prefix.Length..];
        if (payload.Length == 0)
            throw new KeyParseException("Ключ пустой: после vpn:// ничего нет.");
        return payload;
    }

    private static byte[] DecodeBase64Url(string payload)
    {
        var base64 = payload.TrimEnd('=').Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');

        var buffer = new byte[base64.Length * 3 / 4];
        if (!Convert.TryFromBase64String(base64, buffer, out var written))
            throw new KeyParseException("Ключ повреждён: в нём есть лишние символы. Скопируй его ещё раз целиком.");
        return buffer[..written];
    }

    private static byte[]? TryDecompress(byte[] data)
    {
        if (data.Length <= 4)
            return null;

        var expected = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (expected == 0 || expected > MaxConfigBytes)
            return null;

        try
        {
            using var zlib = new ZLibStream(new MemoryStream(data, 4, data.Length - 4), CompressionMode.Decompress);
            using var output = new MemoryStream((int)expected);
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = zlib.Read(chunk)) > 0)
            {
                if (output.Length + read > MaxConfigBytes)
                    return null;
                output.Write(chunk, 0, read);
            }
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static JsonObject ParseJson(byte[] json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject
                ?? throw new KeyParseException("Ключ повреждён: внутри нет настроек.");
        }
        catch (JsonException e)
        {
            throw new KeyParseException("Ключ повреждён или обрезан. Скопируй его ещё раз целиком.", e);
        }
    }

    private static ParsedKey FromJson(JsonObject root)
    {
        var containers = (root["containers"] as JsonArray)?
            .OfType<JsonObject>()
            .Select(c => (Name: StringOf(c["container"]), Config: c))
            .Where(c => !string.IsNullOrEmpty(c.Name))
            .ToList();

        if (containers is null || containers.Count == 0)
            throw new KeyParseException("В ключе нет настроек подключения. Попроси у знакомого другой ключ.");

        var defaultName = StringOf(root["defaultContainer"]);
        var chosen = string.IsNullOrEmpty(defaultName)
            ? containers[0]
            : containers.FirstOrDefault(c => string.Equals(c.Name, defaultName, StringComparison.OrdinalIgnoreCase));

        if (chosen.Config is null)
            throw new KeyParseException("Ключ повреждён: основное подключение в нём не найдено.");

        return new ParsedKey(
            chosen.Name!,
            StringOf(root["hostName"]),
            chosen.Config,
            containers.Select(c => c.Name!).ToList());
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}
