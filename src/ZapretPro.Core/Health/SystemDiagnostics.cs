using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using ZapretPro.Core.Vpn;
using ZapretPro.Core.Zapret;

namespace ZapretPro.Core.Health;

/// <summary>
/// Что на компьютере может мешать — для отчёта. Каждая строка отвечает на вопрос, который раньше
/// приходилось выяснять командами в PowerShell: включён ли мёртвый системный прокси, какие ещё VPN
/// и обходы блокировок запущены, какой DNS у адаптеров, не переписан ли файл hosts.
/// </summary>
public static class SystemDiagnostics
{
    // Программы, которые сами меняют прокси, DNS или ставят свой драйвер
    private static readonly (string Process, string Name)[] Conflicting =
    [
        ("v2rayN", "v2rayN"), ("Happ", "Happ"), ("nekoray", "NekoRay"), ("nekobox", "NekoBox"), ("hiddify", "Hiddify"),
        ("Warp-svc", "Cloudflare WARP"), ("Cloudflare WARP", "Cloudflare WARP"), ("AmneziaVPN", "Amnezia VPN"),
        ("goodbyedpi", "GoodbyeDPI"), ("winws", "другой zapret (winws)"), ("Outline", "Outline"), ("clash", "Clash"),
        ("throne", "Throne"), ("karing", "Karing"), ("xray", "Xray"), ("v2ray", "V2Ray"),
    ];

    public static string Build(string ownAdapter, string ownInstallDir)
    {
        var sb = new StringBuilder();
        Line(sb, "Windows", () => $"{Environment.OSVersion.Version}, {(Environment.Is64BitOperatingSystem ? "64" : "32")}-бит, " +
                                  $"права администратора: {(IsAdmin() ? "да" : "НЕТ")}, папка программы: {Shorten(ownInstallDir)}");
        Line(sb, "Системный прокси", SystemProxy);
        Line(sb, "Другие VPN", () => OtherVpn.Detect(ownAdapter) is { Count: > 0 } v ? string.Join(", ", v) : "нет");
        Line(sb, "Мешающие программы", () => ConflictingPrograms(ownInstallDir) is { Count: > 0 } c ? string.Join(", ", c) : "нет");
        Line(sb, "Службы обхода", () => ZapretManager.OtherBypassTools() is { Count: > 0 } s ? string.Join(", ", s) : "нет");
        Line(sb, "Адаптеры", () => Adapters(ownAdapter));
        Line(sb, "Файл hosts", Hosts);
        return sb.ToString().TrimEnd();
    }

    private static void Line(StringBuilder sb, string title, Func<string> value)
    {
        string text;
        try
        {
            text = value();
        }
        catch (Exception e)
        {
            text = $"не узнать ({e.GetType().Name})";
        }
        sb.AppendLine($"{title}: {text}");
    }

    private static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Прокси из настроек Windows. Включённый прокси от закрытой программы (v2rayN, Happ) — частая причина
    /// «ничего не открывается»: браузер идёт в никуда.
    /// </summary>
    internal static string SystemProxy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        if (key is null)
            return "нет";
        var enabled = key.GetValue("ProxyEnable") is int i && i != 0;
        var server = key.GetValue("ProxyServer") as string;
        var autoConfig = key.GetValue("AutoConfigURL") as string;
        var parts = new List<string>();
        if (enabled)
        {
            var alive = server is not null && IsLocalPortClosed(server) ? " — НИКТО НЕ СЛУШАЕТ этот адрес, браузер не откроет сайты" : "";
            parts.Add($"ВКЛЮЧЁН {server ?? "?"}{alive}");
        }
        if (!string.IsNullOrWhiteSpace(autoConfig))
            parts.Add("есть сценарий автонастройки (PAC)");
        return parts.Count == 0 ? "выключен" : string.Join("; ", parts);
    }

    /// <summary>Прокси вида 127.0.0.1:10809, а порт никто не слушает — программа-прокси закрыта.</summary>
    private static bool IsLocalPortClosed(string server)
    {
        var address = server.Split(';')[0];
        if (address.Contains('='))
            address = address[(address.IndexOf('=') + 1)..];
        var colon = address.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(address[(colon + 1)..], out var port))
            return false;
        var host = address[..colon];
        if (host is not ("127.0.0.1" or "localhost"))
            return false;
        using var client = new TcpClient();
        try
        {
            return !client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            return true;
        }
    }

    private static IReadOnlyList<string> ConflictingPrograms(string ownInstallDir)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, title) in Conflicting)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    // Наш собственный winws и sing-box — не помеха
                    var path = Infrastructure.ChildProcesses.PathOf(process);
                    if (path is not null && path.StartsWith(ownInstallDir, StringComparison.OrdinalIgnoreCase))
                        continue;
                    found.Add(title);
                }
            }
        }
        return found.ToList();
    }

    private static string Adapters(string ownAdapter)
    {
        var lines = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n =>
            {
                var props = n.GetIPProperties();
                var gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
                var v6 = props.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal);
                var dns = string.Join(" ", props.DnsAddresses.Where(a => !a.IsIPv6LinkLocal).Select(a => a.ToString()));
                var own = n.Name.Equals(ownAdapter, StringComparison.OrdinalIgnoreCase) ? " [наш VPN]" : "";
                return $"«{n.Name}»{own}{(gateway ? ", в интернет" : "")}{(v6 ? ", IPv6" : "")}, DNS {(dns.Length == 0 ? "—" : dns)}";
            })
            .ToList();
        return lines.Count == 0 ? "нет подключённых" : string.Join("; ", lines);
    }

    /// <summary>Сайты, переписанные в hosts: так «блокируют» ChatGPT и YouTube некоторые программы и вирусы.</summary>
    private static string Hosts()
    {
        var path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        if (!File.Exists(path))
            return "нет файла";
        var entries = File.ReadAllLines(path)
            .Select(l => l.Split('#')[0].Trim())
            .Where(l => l.Length > 0)
            .ToList();
        string[] watched = ["openai", "chatgpt", "youtube", "googlevideo", "discord", "telegram", "instagram", "claude", "anthropic"];
        var suspicious = entries.Where(e => watched.Any(w => e.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        return $"{entries.Count} записей" + (suspicious.Count > 0 ? $", ИЗ НИХ ПРО НУЖНЫЕ САЙТЫ: {string.Join("; ", suspicious.Take(5))}" : "");
    }

    private static string Shorten(string path) =>
        path.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
}
