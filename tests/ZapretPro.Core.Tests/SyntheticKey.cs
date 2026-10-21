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

    /// <summary>Конфиг AmneziaWG в том виде, в каком его кладёт клиент Amnezia (шаблон awg/template.conf).</summary>
    public const string AwgText = $"""
        [Interface]
        Address = 10.8.1.5/32
        DNS = $PRIMARY_DNS, $SECONDARY_DNS
        PrivateKey = {FakeSecret}=
        Jc = 4
        Jmin = 10
        Jmax = 50
        S1 = 72
        S2 = 56
        H1 = 1376979037
        H2 = 1179155530
        H3 = 1949565853
        H4 = 1520812435
        I1 =
        I2 = <b 0x00>
        PostUp = calc.exe

        [Peer]
        PublicKey = FAKE-SERVER-PUBLIC-KEY=
        PresharedKey = FAKE-PSK=
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = 203.0.113.10:51820
        PersistentKeepalive = 25
        """;

    private static JsonObject Container(string name)
    {
        var protocol = name.Replace("amnezia-", "");
        var lastConfig = name == "amnezia-awg"
            ? new JsonObject { ["config"] = AwgText, ["mtu"] = "1376", ["hostName"] = "203.0.113.10", ["port"] = 51820 }
            : new JsonObject { ["private_key"] = FakeSecret };
        return new JsonObject
        {
            ["container"] = name,
            [protocol] = new JsonObject
            {
                ["port"] = "443",
                // Amnezia хранит last_config строкой с JSON внутри
                ["last_config"] = lastConfig.ToJsonString(),
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
