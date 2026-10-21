namespace ZapretPro.Core.Key;

/// <summary>
/// Протокол из ключа Amnezia. Определяется по имени контейнера (amnezia-awg, amnezia-xray и т. д.).
/// </summary>
public enum VpnProtocol
{
    Unknown,
    AmneziaXray,
    AmneziaWg,
    WireGuard,
    OpenVpn,
    OpenVpnCloak,
    Shadowsocks,
    Ikev2,
}

public static class VpnProtocolInfo
{
    public static VpnProtocol FromContainerName(string containerName) => containerName.ToLowerInvariant() switch
    {
        "amnezia-xray" => VpnProtocol.AmneziaXray,
        // Новые версии AmneziaWG могут называть контейнер с суффиксом версии
        var n when n.StartsWith("amnezia-awg", StringComparison.Ordinal) => VpnProtocol.AmneziaWg,
        "amnezia-wireguard" => VpnProtocol.WireGuard,
        "amnezia-openvpn" => VpnProtocol.OpenVpn,
        "amnezia-openvpn-cloak" => VpnProtocol.OpenVpnCloak,
        "amnezia-shadowsocks" => VpnProtocol.Shadowsocks,
        "amnezia-ikev2" => VpnProtocol.Ikev2,
        _ => VpnProtocol.Unknown,
    };

    public static string DisplayName(VpnProtocol protocol) => protocol switch
    {
        VpnProtocol.AmneziaXray => "XRay (VLESS / Reality)",
        VpnProtocol.AmneziaWg => "AmneziaWG",
        VpnProtocol.WireGuard => "WireGuard",
        VpnProtocol.OpenVpn => "OpenVPN",
        VpnProtocol.OpenVpnCloak => "OpenVPN over Cloak",
        VpnProtocol.Shadowsocks => "Shadowsocks",
        VpnProtocol.Ikev2 => "IKEv2",
        _ => "неизвестный",
    };

    /// <summary>Протоколы, для которых в этапе 3 есть ядро и раздельная маршрутизация (см. PLAN.md).</summary>
    public static bool IsSupported(VpnProtocol protocol) =>
        protocol is VpnProtocol.AmneziaXray or VpnProtocol.AmneziaWg;
}
