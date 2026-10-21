using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ZapretPro.Core.Dns;

/// <summary>
/// Переключает DNS Windows на наш (127.0.0.1 / ::1) и возвращает прежний.
/// Прежние настройки сохраняются в файл до изменения: если программа упадёт, при следующем запуске
/// она вернёт их первым делом. Вторым DNS остаётся прежний IPv4-сервер — если наш DNS не ответит,
/// Windows через секунду спросит его, и интернет не пропадёт.
/// </summary>
public sealed class SystemDns
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _backupPath;
    private readonly string _ownAdapter;
    private readonly Func<string, Task<(int Code, string Output)>> _netsh;

    public SystemDns(string backupPath, string ownAdapter, Func<string, Task<(int Code, string Output)>>? netsh = null)
    {
        _backupPath = backupPath;
        _ownAdapter = ownAdapter;
        _netsh = netsh ?? RunNetshAsync;
    }

    public event Action<string>? Log;

    public sealed record AdapterBackup(string Id, string Name, string[]? StaticV4, string[]? StaticV6, string[] EffectiveServers);

    public bool HasBackup => File.Exists(_backupPath);

    /// <summary>
    /// Включает наш DNS на всех подключённых к интернету адаптерах. Возвращает DNS-серверы, которые
    /// были до нас, — им наш DNS будет пересылать обычные вопросы.
    /// </summary>
    public async Task<IReadOnlyList<IPEndPoint>> ActivateAsync(IReadOnlyList<AdapterBackup>? adapters = null)
    {
        // Остались настройки от прошлого запуска (программа упала) — сначала вернуть их
        if (HasBackup)
            await RestoreAsync();

        adapters ??= Capture();
        if (adapters.Count == 0)
            throw new InvalidOperationException("Не нашёл ни одного подключённого к интернету адаптера");

        Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
        File.WriteAllText(_backupPath, JsonSerializer.Serialize(adapters, Json));

        foreach (var adapter in adapters)
        {
            var fallback = adapter.EffectiveServers
                .Select(IPAddress.Parse)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

            await NetshAsync($"interface ipv4 set dnsservers name=\"{adapter.Name}\" source=static address=127.0.0.1 register=primary validate=no");
            if (fallback is not null)
                await NetshAsync($"interface ipv4 add dnsservers name=\"{adapter.Name}\" address={fallback} index=2 validate=no");
            // IPv6-DNS тоже на наш, иначе Windows спрашивала бы его мимо нас
            await NetshAsync($"interface ipv6 set dnsservers name=\"{adapter.Name}\" source=static address=::1 register=primary validate=no",
                quietOnError: true);
        }

        FlushCache();
        var upstreams = adapters
            .SelectMany(a => a.EffectiveServers)
            .Select(IPAddress.Parse)
            .Where(a => !IPAddress.IsLoopback(a))
            .Distinct()
            .Select(a => new IPEndPoint(a, 53))
            .ToList();
        // DNS не было вообще (бывает в странных сетях) — Яндекс и Google, они работают из России
        if (upstreams.Count == 0)
            upstreams = [new(IPAddress.Parse("77.88.8.8"), 53), new(IPAddress.Parse("8.8.8.8"), 53)];
        Log?.Invoke($"DNS переключён на программу на адаптерах: {string.Join(", ", adapters.Select(a => a.Name))}");
        return upstreams;
    }

    /// <summary>Возвращает DNS как был до программы.</summary>
    public async Task RestoreAsync()
    {
        if (!HasBackup)
            return;

        List<AdapterBackup>? adapters;
        try
        {
            adapters = JsonSerializer.Deserialize<List<AdapterBackup>>(File.ReadAllText(_backupPath));
        }
        catch (JsonException)
        {
            adapters = null;
        }

        foreach (var adapter in adapters ?? [])
        {
            await RestoreFamilyAsync("ipv4", adapter.Name, adapter.StaticV4);
            await RestoreFamilyAsync("ipv6", adapter.Name, adapter.StaticV6);
        }

        File.Delete(_backupPath);
        FlushCache();
        Log?.Invoke("DNS возвращён как был");
    }

    private async Task RestoreFamilyAsync(string family, string name, string[]? servers)
    {
        if (servers is null || servers.Length == 0)
        {
            await NetshAsync($"interface {family} set dnsservers name=\"{name}\" source=dhcp", quietOnError: true);
            return;
        }
        await NetshAsync($"interface {family} set dnsservers name=\"{name}\" source=static address={servers[0]} register=primary validate=no",
            quietOnError: true);
        for (var i = 1; i < servers.Length; i++)
            await NetshAsync($"interface {family} add dnsservers name=\"{name}\" address={servers[i]} index={i + 1} validate=no",
                quietOnError: true);
    }

    /// <summary>
    /// Включённые адаптеры с выходом в интернет или со своими DNS-серверами, и их DNS сейчас.
    /// Windows рассылает вопрос параллельно на DNS всех адаптеров и берёт самый быстрый ответ, поэтому
    /// переключать нужно все, у кого DNS есть, — иначе ответ провайдера мог бы обогнать наш.
    /// </summary>
    public IReadOnlyList<AdapterBackup> Capture() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    && !n.Name.Equals(_ownAdapter, StringComparison.OrdinalIgnoreCase))
        .Select(n => (Adapter: n, Props: n.GetIPProperties()))
        .Where(x => x.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                    && !g.Address.Equals(IPAddress.Any))
                    || x.Props.DnsAddresses.Any(IsRealServer))
        .Select(x => new AdapterBackup(
            x.Adapter.Id,
            x.Adapter.Name,
            ReadStatic(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + x.Adapter.Id),
            ReadStatic(@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\" + x.Adapter.Id),
            // IPv4 первыми: роутеры и провайдеры надёжнее отвечают по IPv4
            x.Props.DnsAddresses.Where(IsRealServer).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .Select(a => a.ToString()).ToArray()))
        .ToList();

    /// <summary>
    /// Настоящий DNS-сервер, а не заглушка. fec0:0:0:ffff::1–3 Windows показывает у адаптеров без IPv6-DNS
    /// (устаревшие «site-local» адреса) — по ним никто не отвечает, и каждый вопрос ждал бы таймаута.
    /// </summary>
    internal static bool IsRealServer(IPAddress address) => !IPAddress.IsLoopback(address) && !address.IsIPv6SiteLocal;

    /// <summary>DNS, заданные вручную (NameServer в реестре). Null — DNS выдаёт роутер (DHCP).</summary>
    private static string[]? ReadStatic(string keyPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        var value = key?.GetValue("NameServer") as string;
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var servers = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Where(s => IPAddress.TryParse(s, out var ip) && !IPAddress.IsLoopback(ip))
            .ToArray();
        return servers.Length == 0 ? null : servers;
    }

    private async Task NetshAsync(string args, bool quietOnError = false)
    {
        var (code, output) = await _netsh(args);
        if (code != 0 && !quietOnError)
            throw new InvalidOperationException($"netsh {args}: {output}");
        if (code != 0)
            Log?.Invoke($"netsh {args}: {output}");
    }

    private static async Task<(int, string)> RunNetshAsync(string args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("netsh.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output.Trim());
    }

    /// <summary>Забыть адреса, которые Windows уже запомнила (в том числе подменённые провайдером).</summary>
    public static void FlushCache()
    {
        try
        {
            DnsFlushResolverCache();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dnsapi.dll")]
    private static extern bool DnsFlushResolverCache();
}
