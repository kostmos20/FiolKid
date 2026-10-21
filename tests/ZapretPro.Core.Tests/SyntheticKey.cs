using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace ZapretPro.Core.Tests;

/// <summary>
/// Собирает ключ vpn:// так же, как клиент Amnezia: qCompress(JSON) → base64url без «=».
/// Все значения внутри выдуманные, настоящий ключ в репозиторий не кладём.
/// </summary>
internal static class SyntheticKey
{
    public const string FakeSecret = "FAKE-PRIVATE-KEY-do-not-print";

    public static JsonObject Config(string defaultContainer, params string[] containers) => new()
    {
        ["containers"] = new JsonArray(containers.Select(Container).ToArray<JsonNode?>()),
        ["defaultContainer"] = defaultContainer,
        ["description"] = "Test server",
        ["dns1"] = "1.1.1.1",
        ["dns2"] = "1.0.0.1",
        ["hostName"] = "203.0.113.10",
    };

    private static JsonObject Container(string name)
    {
        var protocol = name.Replace("amnezia-", "");
        return new JsonObject
        {
            ["container"] = name,
            [protocol] = new JsonObject
            {
                ["port"] = "443",
                ["last_config"] = $"{{\"private_key\":\"{FakeSecret}\"}}",
            },
        };
    }

    public static string Build(JsonObject config, bool compress = true) => Encode(Bytes(config, compress));

    public static byte[] Bytes(JsonObject config, bool compress = true)
    {
        var json = Encoding.UTF8.GetBytes(config.ToJsonString());
        return compress ? QCompress(json) : json;
    }

    public static string Encode(byte[] data) =>
        "vpn://" + Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Формат Qt qCompress: 4 байта длины исходных данных (big-endian) + zlib-поток.</summary>
    public static byte[] QCompress(byte[] data)
    {
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        output.Write(header);
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(data);
        return output.ToArray();
    }
}
