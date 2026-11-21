using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using ZapretPro.Core.Dns;
using ZapretPro.Core.Vpn;

namespace ZapretPro.Core.Health;

/// <summary>
/// Соединения «напрямую»: мимо туннеля VPN и мимо нашего DNS. Так проверяются zapret и «есть ли интернет»:
/// кривой VPN не должен выглядеть как «нет интернета» и не должен валить проверку альтов.
/// Адрес узнаём у DNS провайдера (настройки Wi-Fi или сетевой карты), сокет привязываем к адресу этой
/// карты — тогда Windows не пустит его в туннель, даже если адрес сайта есть в маршрутах VPN.
/// </summary>
public static class DirectNet
{
    // DNS провайдера не известен (в запасном режиме нашего DNS на карте стоит 127.0.0.1) — Яндекс и Google
    private static readonly IPEndPoint[] FallbackDns =
        [new(IPAddress.Parse("77.88.8.8"), 53), new(IPAddress.Parse("8.8.8.8"), 53)];

    // Российский адрес, который программа никогда не пускает в туннель: по нему Windows скажет, какая карта главная
    private static readonly IPAddress Probe = IPAddress.Parse("77.88.8.8");

    public static SocketsHttpHandler Handler(TimeSpan connectTimeout) => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectTimeout = connectTimeout,
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var (local, dns) = Physical();
        var host = context.DnsEndPoint.Host;
        IReadOnlyList<IPAddress> addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await ResolveAsync(host, dns, ct);
        if (addresses.Count == 0)
            throw new DirectDnsException();

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                if (local is not null)
                    socket.Bind(new IPEndPoint(local, 0));
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException e)
            {
                socket.Dispose();
                lastError = e;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw lastError ?? new SocketException((int)SocketError.NotConnected);
    }

    /// <summary>
    /// IPv4-адреса имени: сначала у DNS провайдера (наша программа ходит к нему мимо фильтра WFP). Бывает,
    /// что провайдер перехватывает обычные DNS-запросы про YouTube и отвечает пустотой, а Windows и браузер
    /// ходят к тому же 8.8.8.8 зашифрованным DNS и адрес получают. Тогда спрашиваем у Windows: адрес нужен
    /// только чтобы соединиться, а само соединение всё равно пойдёт напрямую, мимо туннеля.
    /// </summary>
    internal static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, IReadOnlyList<IPEndPoint> dns, CancellationToken ct)
        => await ResolveAsync(host, dns, ct, SystemResolveAsync);

    // Последние удачные ответы: провайдер то отдаёт адрес YouTube, то нет — пропуск DNS не должен
    // выглядеть как «альт не работает»
    private static readonly ConcurrentDictionary<string, (IReadOnlyList<IPAddress> Addresses, DateTime At)> Known =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan KnownLifetime = TimeSpan.FromDays(1);

    internal static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, IReadOnlyList<IPEndPoint> dns, CancellationToken ct,
        Func<string, CancellationToken, Task<IReadOnlyList<IPAddress>>> system)
    {
        var addresses = await AskProviderAsync(host, dns, ct);
        if (addresses.Count == 0)
            addresses = (await system(host, ct)).Where(IsUsable).ToList();
        if (addresses.Count > 0)
        {
            Known[host] = (addresses, DateTime.UtcNow);
            return addresses;
        }
        return Known.TryGetValue(host, out var known) && DateTime.UtcNow - known.At < KnownLifetime ? known.Addresses : [];
    }

    /// <summary>
    /// Каждый сервер провайдера отдельно; берём первый ответ, в котором есть адрес. Пустой ответ по UDP бывает
    /// подделкой (перехват по дороге) — тогда тот же сервер ещё и по TCP.
    /// </summary>
    private static async Task<IReadOnlyList<IPAddress>> AskProviderAsync(string host, IReadOnlyList<IPEndPoint> dns, CancellationToken ct)
    {
        if (dns.Count == 0)
            return [];
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = dns.Select(server => AskServerAsync(host, server, stop.Token)).ToList();
        var all = pending.ToArray();
        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending);
                pending.Remove(done);
                var addresses = await done;
                if (addresses.Count > 0)
                    return addresses;
            }
            return [];
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(all);
        }
    }

    private static async Task<IReadOnlyList<IPAddress>> AskServerAsync(string host, IPEndPoint server, CancellationToken ct)
    {
        try
        {
            var query = DnsMessage.Query(host, 1, (ushort)Random.Shared.Next(1, ushort.MaxValue));
            var answer = await SmartDns.ForwardAsync(query, [server], ct);
            var addresses = answer is null ? [] : DnsMessage.ReadARecords(answer).Where(IsUsable).ToList();
            if (addresses.Count > 0 || ct.IsCancellationRequested)
                return addresses;
            var tcp = await SmartDns.AskTcpAsync(query, server, ct);
            return tcp is null ? [] : DnsMessage.ReadARecords(tcp).Where(IsUsable).ToList();
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<IPAddress>> SystemResolveAsync(string host, CancellationToken ct)
    {
        try
        {
            return await System.Net.Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct);
        }
        catch (SocketException)
        {
            return [];
        }
    }

    // Заглушки вместо адреса (0.0.0.0, 127.x) — тоже «не узнал»
    private static bool IsUsable(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any);

    /// <summary>Адрес главной сетевой карты (Wi-Fi, кабель) и её DNS. Карты VPN не в счёт.</summary>
    internal static (IPAddress? Local, IReadOnlyList<IPEndPoint> Dns) Physical()
    {
        var cards = new List<(int Index, IPAddress Local, List<IPEndPoint> Dns)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || IsVirtualTunnel(nic))
                    continue;
                var props = nic.GetIPProperties();
                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                    continue;
                var local = props.UnicastAddresses.Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (local is null)
                    continue;
                var dns = props.DnsAddresses
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    .Select(a => new IPEndPoint(a, 53)).ToList();
                cards.Add((props.GetIPv4Properties()?.Index ?? -1, local, dns));
            }
        }
        catch (NetworkInformationException)
        {
        }

        if (cards.Count == 0)
            return (null, FallbackDns);
        var best = BestInterfaceIndex();
        var main = cards.FirstOrDefault(c => c.Index == best);
        if (main.Local is null)
            main = cards[0];
        var servers = main.Dns.Count > 0 ? main.Dns : cards.SelectMany(c => c.Dns).Distinct().ToList();
        return (main.Local, servers.Count > 0 ? servers : FallbackDns);
    }

    private static bool IsVirtualTunnel(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            return true;
        if (nic.Name.Equals(VpnManager.TunnelName, StringComparison.OrdinalIgnoreCase))
            return true;
        var text = nic.Name + " " + nic.Description;
        return new[] { "sing-tun", "wintun", "wireguard", "amneziawg" }.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private static int BestInterfaceIndex()
    {
        try
        {
            var bytes = Probe.GetAddressBytes();
            return GetBestInterface(BitConverter.ToUInt32(bytes, 0), out var index) == 0 ? (int)index : -1;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return -1;
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);
}

/// <summary>Адрес сайта не узнали ни у провайдера, ни у Windows. Это сбой DNS, а не блокировка: альт тут ни при чём.</summary>
public sealed class DirectDnsException() : SocketException((int)SocketError.HostNotFound);
