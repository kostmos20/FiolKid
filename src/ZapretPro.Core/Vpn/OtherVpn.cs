using System.Net.NetworkInformation;

namespace ZapretPro.Core.Vpn;

/// <summary>
/// Ищет другие включённые VPN. Особенно важно, если это Amnezia VPN с тем же ключом: у обоих туннелей
/// один внутренний адрес, Windows не даёт поднять второй, а сервер общается только с одним клиентом.
/// </summary>
public static class OtherVpn
{
    // По описанию сетевого адаптера: так выглядят адаптеры популярных VPN-клиентов на Windows
    private static readonly string[] Markers =
    [
        "amnezia", "wireguard", "wintun", "tap-windows", "openvpn", "outline", "hiddify", "sing-box", "v2ray",
        "xray", "nekoray", "warp", "protonvpn", "nordlynx", "expressvpn", "windscribe", "tun2socks",
    ];

    /// <summary>Названия включённых VPN-адаптеров, кроме нашего.</summary>
    public static IReadOnlyList<string> Detect(string ownAdapter)
    {
        try
        {
            return Filter(NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Select(n => (n.Name, n.Description)), ownAdapter);
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    internal static IReadOnlyList<string> Filter(IEnumerable<(string Name, string Description)> adapters, string ownAdapter) =>
        adapters
            .Where(a => !a.Name.Equals(ownAdapter, StringComparison.OrdinalIgnoreCase))
            .Where(a => Markers.Any(m => a.Name.Contains(m, StringComparison.OrdinalIgnoreCase)
                                         || a.Description.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.Name == a.Description ? a.Name : $"{a.Name} ({a.Description})")
            .Distinct()
            .ToList();
}
