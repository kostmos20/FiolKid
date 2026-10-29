using System.Text.Json.Nodes;
using ZapretPro.Core.Key;

namespace ZapretPro.Core.Tests;

public class KeyParserTests
{
    [Theory]
    [InlineData("amnezia-awg", VpnProtocol.AmneziaWg, true)]
    [InlineData("amnezia-xray", VpnProtocol.AmneziaXray, true)]
    [InlineData("amnezia-wireguard", VpnProtocol.WireGuard, false)]
    [InlineData("amnezia-openvpn", VpnProtocol.OpenVpn, false)]
    [InlineData("amnezia-openvpn-cloak", VpnProtocol.OpenVpnCloak, false)]
    [InlineData("amnezia-shadowsocks", VpnProtocol.Shadowsocks, false)]
    [InlineData("amnezia-ikev2", VpnProtocol.Ikev2, false)]
    public void Detects_protocol_from_default_container(string container, VpnProtocol expected, bool supported)
    {
        var key = SyntheticKey.Build(SyntheticKey.Config(container, container));

        var parsed = KeyParser.Parse(key);

        Assert.Equal(expected, parsed.Protocol);
        Assert.Equal(container, parsed.ContainerName);
        Assert.Equal(supported, parsed.IsSupported);
        Assert.Equal(supported, parsed.UnsupportedReason is null);
        Assert.Equal("203.0.113.10", parsed.HostName);
    }

    [Fact]
    public void Picks_default_container_among_several()
    {
        var key = SyntheticKey.Build(SyntheticKey.Config("amnezia-xray", "amnezia-awg", "amnezia-xray"));

        var parsed = KeyParser.Parse(key);

        Assert.Equal(VpnProtocol.AmneziaXray, parsed.Protocol);
        Assert.Equal(["amnezia-awg", "amnezia-xray"], parsed.AllContainers);
        Assert.NotNull(parsed.Container["xray"]);
    }

    [Fact]
    public void Uses_first_container_when_default_is_missing()
    {
        var config = SyntheticKey.Config("amnezia-awg", "amnezia-awg", "amnezia-xray");
        config.Remove("defaultContainer");

        Assert.Equal(VpnProtocol.AmneziaWg, KeyParser.Parse(SyntheticKey.Build(config)).Protocol);
    }

    [Fact]
    public void Unknown_container_is_reported_as_unsupported_with_its_name()
    {
        var parsed = KeyParser.Parse(SyntheticKey.Build(SyntheticKey.Config("amnezia-future", "amnezia-future")));

        Assert.Equal(VpnProtocol.Unknown, parsed.Protocol);
        Assert.False(parsed.IsSupported);
        Assert.Contains("amnezia-future", parsed.UnsupportedReason);
    }

    [Fact]
    public void Accepts_uncompressed_json_like_amnezia_client()
    {
        var key = SyntheticKey.Build(SyntheticKey.Config("amnezia-awg", "amnezia-awg"), compress: false);

        Assert.Equal(VpnProtocol.AmneziaWg, KeyParser.Parse(key).Protocol);
    }

    [Fact]
    public void Tolerates_whitespace_line_breaks_padding_and_prefix_case()
    {
        var key = SyntheticKey.Build(SyntheticKey.Config("amnezia-awg", "amnezia-awg"));
        var payload = key["vpn://".Length..];
        var mangled = "  VPN://" + payload[..20] + "\r\n" + payload[20..40] + " \t" + payload[40..] + "==\n";

        Assert.Equal(VpnProtocol.AmneziaWg, KeyParser.Parse(mangled).Protocol);
        Assert.Equal(key, KeyParser.Normalize(mangled));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com")]
    [InlineData("vpn://")]
    [InlineData("vpn://   ")]
    [InlineData("vpn://не-base64!*")]
    public void Rejects_malformed_input(string key)
    {
        var e = Assert.Throws<KeyParseException>(() => KeyParser.Parse(key));
        Assert.False(string.IsNullOrWhiteSpace(e.Message));
    }

    [Fact]
    public void Rejects_truncated_key()
    {
        var key = SyntheticKey.Build(SyntheticKey.Config("amnezia-awg", "amnezia-awg"));

        Assert.Throws<KeyParseException>(() => KeyParser.Parse(key[..(key.Length / 2)]));
    }

    [Fact]
    public void Rejects_json_without_containers()
    {
        var config = new JsonObject { ["hostName"] = "203.0.113.10", ["containers"] = new JsonArray() };

        var e = Assert.Throws<KeyParseException>(() => KeyParser.Parse(SyntheticKey.Build(config)));
        Assert.Contains("нет настроек", e.Message);
    }

    [Fact]
    public void Rejects_default_container_that_is_not_in_the_list()
    {
        var config = SyntheticKey.Config("amnezia-xray", "amnezia-awg");

        Assert.Throws<KeyParseException>(() => KeyParser.Parse(SyntheticKey.Build(config)));
    }

    [Fact]
    public void Rejects_json_that_is_not_an_object()
    {
        Assert.Throws<KeyParseException>(() => KeyParser.Parse(SyntheticKey.Encode("[1,2,3]"u8.ToArray())));
    }

    [Fact]
    public void Rejects_oversized_compressed_payload()
    {
        // Заголовок обещает 100 МБ — дальше не распаковываем, а JSON из мусора не читается.
        var data = SyntheticKey.QCompress(new byte[10]);
        data[0] = 0x06;

        Assert.Throws<KeyParseException>(() => KeyParser.Parse(SyntheticKey.Encode(data)));
    }

    [Fact]
    public void ToString_and_errors_never_contain_secrets()
    {
        var key = SyntheticKey.Build(SyntheticKey.Config("amnezia-awg", "amnezia-awg"));
        var parsed = KeyParser.Parse(key);

        Assert.DoesNotContain(SyntheticKey.FakeSecret, parsed.ToString());
        Assert.Equal("amnezia-awg", parsed.ToString());

        var e = Assert.Throws<KeyParseException>(() => KeyParser.Parse(key[..(key.Length - 10)]));
        Assert.DoesNotContain(key[6..30], e.Message);
    }
}
