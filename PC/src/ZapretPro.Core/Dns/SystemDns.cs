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
/// Прежние настройки сохраняются в файл до изменения и удаляются, только когда DNS действительно вернулся.
/// Не вернулся (адаптер выключен, процесс прибили посреди дела) — файл остаётся и программа пробует снова:
/// при запуске, по таймеру, при смене сети. Если FiolKid упадёт — вернёт сторожевой процесс (--dns-guard),
/// если выключат компьютер — задача Планировщика при следующем включении (<see cref="DnsRestoreTask"/>).
/// </summary>
public sealed class SystemDns
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _backupPath;
    private readonly string _ownAdapter;
    private readonly Func<string, Task<(int Code, string Output)>> _netsh;
    private readonly IAdapterDns _adapters;
    private readonly IDnsRestoreTask _restoreTask;
    private readonly Func<IReadOnlyList<AdapterBackup>>? _capture;

    public SystemDns(string backupPath, string ownAdapter, Func<string, Task<(int Code, string Output)>>? netsh = null,
        IAdapterDns? adapters = null, IDnsRestoreTask? restoreTask = null, Func<IReadOnlyList<AdapterBackup>>? capture = null)
    {
        _capture = capture;
        _backupPath = backupPath;
        _ownAdapter = ownAdapter;
        _netsh = netsh ?? RunNetshAsync;
        _adapters = adapters ?? new WindowsAdapterDns();
        _restoreTask = restoreTask ?? new DnsRestoreTask();
    }

    public event Action<string>? Log;

    public sealed record AdapterBackup(string Id, string Name, string[]? StaticV4, string[]? StaticV6, string[] EffectiveServers);

    public bool HasBackup => File.Exists(_backupPath);

    /// <summary>
    /// Отметка «программа хоть раз переключала DNS на себя». Без неё 127.0.0.1 у адаптера — чужой
    /// (свой DNS-фильтр человека), и чинить его мы не имеем права.
    /// </summary>
    private string UsedMarkPath => Path.Combine(Path.GetDirectoryName(_backupPath)!, "dns-used.txt");

    /// <summary>
    /// Включает наш DNS на всех подключённых к интернету адаптерах. Возвращает DNS-серверы, которые
    /// были до нас, — им наш DNS будет пересылать обычные вопросы.
    /// </summary>
    public async Task<IReadOnlyList<IPEndPoint>> ActivateAsync(IReadOnlyList<AdapterBackup>? adapters = null)
    {
        // Остались настройки от прошлого запуска (программа упала) — сначала вернуть их. Не вернулись
        // целиком — старые записи главнее: в них настоящие DNS, а у адаптера сейчас, может, ещё наш
        List<AdapterBackup> previous = [];
        if (HasBackup && !await RestoreAsync())
            previous = ReadBackup() ?? [];

        adapters ??= Capture();
        if (adapters.Count == 0)
            throw new InvalidOperationException("Не нашёл ни одного подключённого к интернету адаптера");

        var backup = previous.Concat(adapters.Where(a => previous.All(p => !p.Id.Equals(a.Id, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(_backupPath)!);
        File.WriteAllText(_backupPath, JsonSerializer.Serialize(backup, Json));
        File.WriteAllText(UsedMarkPath, "FiolKid переключал DNS Windows на себя");
        // Страховка на случай, если компьютер выключат, не дав программе вернуть DNS
        try
        {
            await _restoreTask.InstallAsync(_backupPath);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException
                                    or System.ComponentModel.Win32Exception)
        {
            Log?.Invoke($"Задача Планировщика для возврата DNS не создалась: {e.Message}");
        }

        foreach (var adapter in adapters)
        {
            var name = _adapters.Name(adapter.Id) ?? adapter.Name;
            // Только наш: второй DNS провайдера Windows спросила бы напрямую, если наш задумался,
            // и сайт из списков получил бы адрес без маршрута в VPN. Провайдера спрашивает наш DNS сам
            await NetshAsync($"interface ipv4 set dnsservers name=\"{name}\" source=static address=127.0.0.1 register=primary validate=no");
            // IPv6-DNS тоже на наш, иначе Windows спрашивала бы его мимо нас
            await NetshAsync($"interface ipv6 set dnsservers name=\"{name}\" source=static address=::1 register=primary validate=no",
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

    /// <summary>
    /// Возвращает DNS как был до программы. True — вернулся везде (или возвращать нечего); false — где-то
    /// не вышло, файл с прежними настройками остаётся, чтобы попробовать ещё раз.
    /// </summary>
    public async Task<bool> RestoreAsync()
    {
        if (!HasBackup)
            return true;

        var adapters = ReadBackup();
        var failed = new List<string>();
        if (adapters is null)
        {
            // Файл испорчен: прежних DNS не узнать, но наш адрес убрать надо — ставим «автоматически»
            Log?.Invoke("Файл с прежними DNS испорчен — ставлю DNS «автоматически» там, где остался адрес программы");
            foreach (var (id, name) in _adapters.All())
            {
                foreach (var family in Families)
                {
                    if (IsOurs(_adapters.Read(family, id)) && !await SetFamilyAsync(family, id, name, null))
                        failed.Add(name);
                }
            }
        }
        else
        {
            foreach (var adapter in adapters)
            {
                var name = _adapters.Name(adapter.Id) ?? adapter.Name;
                if (!await SetFamilyAsync("ipv4", adapter.Id, name, adapter.StaticV4) |
                    !await SetFamilyAsync("ipv6", adapter.Id, name, adapter.StaticV6))
                    failed.Add(name);
            }
        }

        FlushCache();
        if (failed.Count > 0)
        {
            Log?.Invoke($"DNS вернулся не везде (не вышло: {string.Join(", ", failed.Distinct())}) — попробую ещё раз");
            return false;
        }

        File.Delete(_backupPath);
        try
        {
            await _restoreTask.RemoveAsync();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException
                                    or System.ComponentModel.Win32Exception)
        {
            Log?.Invoke($"Задача Планировщика для возврата DNS не удалилась: {e.Message}");
        }
        Log?.Invoke("DNS возвращён как был");
        return true;
    }

    /// <summary>
    /// Чинит DNS, застрявший на адресе программы без файла с прежними настройками (так бывало до 0.9.12:
    /// возврат не удавался, а файл всё равно удалялся). Трогает адаптер, только если программа на этом
    /// компьютере уже переключала DNS, у адаптера ровно наш адрес и на 127.0.0.1:53 никто не отвечает —
    /// чужой работающий DNS-фильтр остаётся как есть. Возвращает, у скольких адаптеров починил.
    /// </summary>
    public async Task<int> ResetStrayAsync(Func<bool> loopbackAnswers)
    {
        if (HasBackup || !File.Exists(UsedMarkPath))
            return 0;
        var stuck = _adapters.All()
            .Where(a => !a.Name.Equals(_ownAdapter, StringComparison.OrdinalIgnoreCase))
            .SelectMany(a => Families.Where(f => IsExactlyOurs(f, _adapters.Read(f, a.Id))).Select(f => (a.Id, a.Name, Family: f)))
            .ToList();
        if (stuck.Count == 0 || loopbackAnswers())
            return 0;

        var fixedNames = new HashSet<string>();
        foreach (var (id, name, family) in stuck)
        {
            if (await SetFamilyAsync(family, id, name, null))
                fixedNames.Add(name);
        }
        FlushCache();
        if (fixedNames.Count > 0)
            Log?.Invoke($"DNS Windows застрял на адресе программы, а она его не обслуживала — вернул «автоматически»: {string.Join(", ", fixedNames)}");
        return fixedNames.Count;
    }

    /// <summary>Отметка «DNS адаптера VPN переключён на программу»: в ней ID адаптера, чтобы вернуть даже без него.</summary>
    public string TunnelMarkPath => Path.Combine(Path.GetDirectoryName(_backupPath)!, "dns-tunnel.txt");

    public bool HasTunnelMark => File.Exists(TunnelMarkPath);

    /// <summary>Адаптер VPN сейчас есть в системе.</summary>
    public bool TunnelPresent => _adapters.IdOf(_ownAdapter) is not null;

    /// <summary>Адаптер VPN есть и DNS у него — наш.</summary>
    public bool TunnelReady => _adapters.IdOf(_ownAdapter) is { } id && IsExactlyOurs("ipv4", _adapters.Read("ipv4", id));

    /// <summary>
    /// Ставит наш DNS только на адаптер VPN — физические адаптеры не трогаем. Адаптер VPN исчезает вместе
    /// с VPN, так что застрять 127.0.0.1 негде. Метрику адаптера ставим самую маленькую: Windows сначала
    /// спрашивает DNS адаптера с меньшей метрикой. Маршруты от этого не меняются — у адаптера VPN только
    /// узкие маршруты сайтов из списков. False — не вышло (адаптера нет, Windows отказала).
    /// </summary>
    public async Task<bool> ActivateTunnelAsync()
    {
        var id = _adapters.IdOf(_ownAdapter);
        if (id is null)
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(TunnelMarkPath)!);
        File.WriteAllText(TunnelMarkPath, id);

        await NetshAsync($"interface ipv4 set dnsservers name=\"{_ownAdapter}\" source=static address=127.0.0.1 register=none validate=no",
            quietOnError: true);
        await NetshAsync($"interface ipv6 set dnsservers name=\"{_ownAdapter}\" source=static address=::1 register=none validate=no",
            quietOnError: true);
        await NetshAsync($"interface ipv4 set interface interface=\"{_ownAdapter}\" metric=1", quietOnError: true);
        await NetshAsync($"interface ipv6 set interface interface=\"{_ownAdapter}\" metric=1", quietOnError: true);
        if (!IsExactlyOurs("ipv4", _adapters.Read("ipv4", id)))
            return false;
        FlushCache();
        return true;
    }

    /// <summary>
    /// Убирает наш DNS с адаптера VPN (по ID из отметки — даже если адаптера сейчас нет: Windows помнит
    /// его настройки и вернула бы их при следующем включении VPN). True — убран или убирать нечего.
    /// </summary>
    public async Task<bool> ClearTunnelAsync()
    {
        if (!HasTunnelMark)
            return true;
        string id;
        try
        {
            id = File.ReadAllText(TunnelMarkPath).Trim();
        }
        catch (IOException)
        {
            return false;
        }
        var ok = true;
        if (id.Length > 0)
        {
            var name = _adapters.Name(id) ?? _ownAdapter;
            foreach (var family in Families)
            {
                if (IsOurs(_adapters.Read(family, id)))
                    ok &= await SetFamilyAsync(family, id, name, null);
            }
        }
        FlushCache();
        if (ok)
            File.Delete(TunnelMarkPath);
        return ok;
    }

    private static readonly string[] Families = ["ipv4", "ipv6"];

    private static bool IsOurs(string[]? servers) =>
        servers is not null && servers.Any(s => IPAddress.TryParse(s, out var ip) && IPAddress.IsLoopback(ip));

    private static bool IsExactlyOurs(string family, string[]? servers) =>
        servers is { Length: 1 } && servers[0] == (family == "ipv4" ? "127.0.0.1" : "::1");

    private List<AdapterBackup>? ReadBackup()
    {
        try
        {
            return JsonSerializer.Deserialize<List<AdapterBackup>>(File.ReadAllText(_backupPath));
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ставит DNS одного семейства и проверяет по реестру, что он действительно встал. netsh не справился
    /// (адаптер выключен или переименован) — пишет в реестр сам: Windows возьмёт настройку при включении адаптера.
    /// </summary>
    private async Task<bool> SetFamilyAsync(string family, string id, string name, string[]? servers)
    {
        // Адаптера в системе больше нет — возвращать некому
        if (!_adapters.Exists(family, id))
            return true;

        if (servers is null || servers.Length == 0)
        {
            await NetshAsync($"interface {family} set dnsservers name=\"{name}\" source=dhcp", quietOnError: true);
        }
        else
        {
            await NetshAsync($"interface {family} set dnsservers name=\"{name}\" source=static address={servers[0]} register=primary validate=no",
                quietOnError: true);
            for (var i = 1; i < servers.Length; i++)
                await NetshAsync($"interface {family} add dnsservers name=\"{name}\" address={servers[i]} index={i + 1} validate=no",
                    quietOnError: true);
        }
        if (Matches(family, id, servers))
            return true;

        try
        {
            _adapters.Write(family, id, servers);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log?.Invoke($"DNS {family} у «{name}»: не записался в реестр ({e.Message})");
        }
        return Matches(family, id, servers);
    }

    /// <summary>У адаптера ровно те DNS, что ожидались, и нашего адреса среди них нет.</summary>
    private bool Matches(string family, string id, string[]? servers)
    {
        var current = _adapters.Read(family, id) ?? [];
        return !IsOurs(current) && current.SequenceEqual(servers ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Включённые адаптеры с выходом в интернет или со своими DNS-серверами, и их DNS сейчас.
    /// Windows рассылает вопрос параллельно на DNS всех адаптеров и берёт самый быстрый ответ, поэтому
    /// переключать нужно все, у кого DNS есть, — иначе ответ провайдера мог бы обогнать наш.
    /// </summary>
    public IReadOnlyList<AdapterBackup> Capture() => _capture?.Invoke() ?? CaptureWindows();

    private List<AdapterBackup> CaptureWindows() => NetworkInterface.GetAllNetworkInterfaces()
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
            WithoutLoopback(_adapters.Read("ipv4", x.Adapter.Id)),
            WithoutLoopback(_adapters.Read("ipv6", x.Adapter.Id)),
            // IPv4 первыми: роутеры и провайдеры надёжнее отвечают по IPv4
            x.Props.DnsAddresses.Where(IsRealServer).OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .Select(a => a.ToString()).ToArray()))
        .ToList();

    /// <summary>Наш же адрес, оставшийся от прошлого раза, прежним DNS не считается.</summary>
    private static string[]? WithoutLoopback(string[]? servers)
    {
        var real = servers?.Where(s => IPAddress.TryParse(s, out var ip) && !IPAddress.IsLoopback(ip)).ToArray();
        return real is { Length: > 0 } ? real : null;
    }

    /// <summary>
    /// Настоящий DNS-сервер, а не заглушка. fec0:0:0:ffff::1–3 Windows показывает у адаптеров без IPv6-DNS
    /// (устаревшие «site-local» адреса) — по ним никто не отвечает, и каждый вопрос ждал бы таймаута.
    /// </summary>
    internal static bool IsRealServer(IPAddress address) => !IPAddress.IsLoopback(address) && !address.IsIPv6SiteLocal;

    /// <summary>
    /// Отвечает ли кто-нибудь на 127.0.0.1:53. Спрашиваем имя из зоны .invalid: наш DNS отвечает на него сам,
    /// не дожидаясь провайдера, а любой другой DNS — быстрым «нет такого».
    /// </summary>
    public static bool LoopbackAnswers(TimeSpan? timeout = null, int port = 53)
    {
        var query = DnsMessage.Query(SmartDns.HealthName, DnsMessage.TypeA, (ushort)Random.Shared.Next(1, ushort.MaxValue));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var udp = new UdpClient(AddressFamily.InterNetwork);
                udp.Client.ReceiveTimeout = (int)(timeout ?? TimeSpan.FromSeconds(3)).TotalMilliseconds;
                udp.Connect(IPAddress.Loopback, port);
                udp.Send(query, query.Length);
                IPEndPoint? from = null;
                var answer = udp.Receive(ref from);
                if (answer.Length >= 12 && DnsMessage.Id(answer) == DnsMessage.Id(query))
                    return true;
            }
            catch (SocketException)
            {
                // Никто не слушает (ICMP «порт недоступен») или не ответил вовремя
            }
        }
        return false;
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

/// <summary>DNS, заданные у адаптеров вручную, — по ID адаптера, а не по имени: имя человек может поменять.</summary>
public interface IAdapterDns
{
    /// <summary>Есть ли настройки этого семейства у адаптера (нет — адаптер удалён из системы).</summary>
    bool Exists(string family, string id);

    /// <summary>DNS, заданные вручную. Null — «автоматически» (выдаёт роутер).</summary>
    string[]? Read(string family, string id);

    /// <summary>Записать прямо в реестр. Null — «автоматически».</summary>
    void Write(string family, string id, string[]? servers);

    /// <summary>Нынешнее имя адаптера (для netsh) или null, если сейчас его не видно.</summary>
    string? Name(string id);

    /// <summary>Все адаптеры, которые видит Windows, включая отключённые.</summary>
    IReadOnlyList<(string Id, string Name)> All();

    /// <summary>ID адаптера с таким именем (любого типа, в том числе VPN) или null.</summary>
    string? IdOf(string name);
}

/// <summary>Настройки DNS адаптеров в реестре Windows (NameServer в Tcpip и Tcpip6).</summary>
public sealed class WindowsAdapterDns : IAdapterDns
{
    private static string KeyPath(string family, string id) =>
        $@"SYSTEM\CurrentControlSet\Services\{(family == "ipv4" ? "Tcpip" : "Tcpip6")}\Parameters\Interfaces\{id}";

    public bool Exists(string family, string id)
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath(family, id));
        return key is not null;
    }

    public string[]? Read(string family, string id)
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath(family, id));
        var value = key?.GetValue("NameServer") as string;
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
    }

    public void Write(string family, string id, string[]? servers)
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath(family, id), writable: true)
                        ?? throw new IOException("нет настроек адаптера в реестре");
        key.SetValue("NameServer", servers is { Length: > 0 } ? string.Join(",", servers) : "", RegistryValueKind.String);
    }

    public string? Name(string id) => NetworkInterface.GetAllNetworkInterfaces()
        .FirstOrDefault(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name;

    public string? IdOf(string name) => NetworkInterface.GetAllNetworkInterfaces()
        .FirstOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Id;

    public IReadOnlyList<(string Id, string Name)> All() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
        .Select(n => (n.Id, n.Name))
        .ToList();
}
