using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ZapretPro.Core.Vpn;

/// <summary>
/// Маршруты в туннель, которые программа добавляет и убирает сама, на лету и без переподключения VPN.
/// Туннель поднят с «Table = off»: служба AmneziaWG маршрутов не создаёт, а пускает в шифрование всё,
/// что Windows направит в её адаптер. Направляем мы: подсети из списков и адреса, которые
/// узнал наш DNS для сайтов из списков. Маршруты временные (не сохраняются после перезагрузки)
/// и исчезают сами вместе с адаптером при остановке VPN.
/// </summary>
public interface IRouteTable
{
    IReadOnlyList<string> Installed { get; }
    void Reset();
    bool Add(IPNetwork network);
    void Sync(IReadOnlyCollection<IPNetwork> wanted);
}

public sealed class TunnelRoutes : IRouteTable
{
    private readonly string _adapter;
    private readonly object _lock = new();
    private readonly HashSet<IPNetwork> _installed = [];
    private ulong _luid;

    public TunnelRoutes(string adapter)
    {
        _adapter = adapter;
    }

    public event Action<string>? Log;

    public int Count
    {
        get
        {
            lock (_lock)
                return _installed.Count;
        }
    }

    public IReadOnlyList<string> Installed
    {
        get
        {
            lock (_lock)
                return _installed.Select(n => n.ToString()).ToList();
        }
    }

    /// <summary>Туннель только что (пере)создан: старые маршруты исчезли вместе с прежним адаптером.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _installed.Clear();
            _luid = 0;
        }
    }

    /// <summary>Добавляет маршрут, если его ещё нет. False — адаптера нет (VPN не поднят) или Windows отказала.</summary>
    public bool Add(IPNetwork network)
    {
        lock (_lock)
        {
            if (_installed.Contains(network))
                return true;
            if (_luid == 0 && !TryFindAdapter(out _luid))
                return false;

            var error = CreateRoute(_luid, network);
            if (error is 0 or ERROR_OBJECT_ALREADY_EXISTS)
            {
                _installed.Add(network);
                return true;
            }
            if (error is ERROR_NOT_FOUND or ERROR_FILE_NOT_FOUND)
                _luid = 0; // адаптер пересоздан — найдём заново в следующий раз
            Log?.Invoke($"Не удалось добавить маршрут {network}: {new Win32Exception((int)error).Message}");
            return false;
        }
    }

    public bool Remove(IPNetwork network)
    {
        lock (_lock)
        {
            if (!_installed.Remove(network) || _luid == 0)
                return false;
            return DeleteRoute(_luid, network) is 0 or ERROR_NOT_FOUND;
        }
    }

    /// <summary>Приводит набор маршрутов к нужному: добавляет недостающие, убирает лишние.</summary>
    public void Sync(IReadOnlyCollection<IPNetwork> wanted)
    {
        foreach (var network in wanted)
            Add(network);
        List<IPNetwork> extra;
        lock (_lock)
            extra = _installed.Where(n => !wanted.Contains(n)).ToList();
        foreach (var network in extra)
            Remove(network);
    }

    private bool TryFindAdapter(out ulong luid) => ConvertInterfaceAliasToLuid(_adapter, out luid) == 0;

    /// <summary>
    /// Для отчёта: есть ли в таблице Windows маршрут на адаптер VPN, покрывающий адрес, и какой интерфейс
    /// называет сама Windows (GetBestRoute2). Только для сведения: у знакомых GetBestRoute2 называл
    /// «другой интерфейс», хотя трафик шёл через туннель, — поэтому решения на нём не строим.
    /// </summary>
    public static (bool InTable, bool? WindowsSaysTunnel) Inspect(IPAddress address, string adapter)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || ConvertInterfaceAliasToLuid(adapter, out var luid) != 0)
            return (false, null);
        var index = ConvertInterfaceLuidToIndex(ref luid, out var i) == 0 ? i : 0;
        var inTable = ReadIPv4Routes().Any(r => r.InterfaceIndex == index && new IPNetwork(r.Destination, r.PrefixLength).Contains(address)
                                                && r.PrefixLength > 0);

        var destination = Marshal.AllocHGlobal(28);
        var source = Marshal.AllocHGlobal(28);
        var row = Marshal.AllocHGlobal(RowSize);
        try
        {
            Marshal.Copy(new byte[28], 0, destination, 28);
            WriteIPv4(destination, 0, address);
            if (GetBestRoute2(IntPtr.Zero, 0, IntPtr.Zero, destination, 0, row, source) != 0)
                return (inTable, null);
            return (inTable, (ulong)Marshal.ReadInt64(row, OffLuid) == luid);
        }
        finally
        {
            Marshal.FreeHGlobal(destination);
            Marshal.FreeHGlobal(source);
            Marshal.FreeHGlobal(row);
        }
    }

    // ---------- IP Helper API ----------
    // MIB_IPFORWARD_ROW2 (netioapi.h), 104 байта на x64. Поля пишем по смещениям: так меньше риска
    // ошибиться с выравниванием вложенных объединений SOCKADDR_INET.
    internal const int RowSize = 104;
    internal const int OffLuid = 0;
    internal const int OffIndex = 8;
    internal const int OffDestination = 12;   // SOCKADDR_INET (28 байт)
    internal const int OffPrefixLength = 40;
    internal const int OffNextHop = 44;       // SOCKADDR_INET (28 байт)
    internal const int OffMetric = 84;
    internal const int OffProtocol = 88;

    private const ushort AF_INET = 2;
    private const uint ERROR_FILE_NOT_FOUND = 2;
    private const uint ERROR_NOT_FOUND = 1168;
    private const uint ERROR_OBJECT_ALREADY_EXISTS = 5010;
    private const int MIB_IPPROTO_NETMGMT = 3;

    private static uint CreateRoute(ulong luid, IPNetwork network) => WithRow(luid, network, CreateIpForwardEntry2);

    private static uint DeleteRoute(ulong luid, IPNetwork network) => WithRow(luid, network, DeleteIpForwardEntry2);

    private static uint WithRow(ulong luid, IPNetwork network, Func<IntPtr, uint> call)
    {
        var row = Marshal.AllocHGlobal(RowSize);
        try
        {
            InitializeIpForwardEntry(row);
            Marshal.WriteInt64(row, OffLuid, (long)luid);
            WriteIPv4(row, OffDestination, network.BaseAddress);
            Marshal.WriteByte(row, OffPrefixLength, (byte)network.PrefixLength);
            WriteIPv4(row, OffNextHop, IPAddress.Any); // 0.0.0.0 — «прямо в адаптер»: у туннеля нет шлюза
            Marshal.WriteInt32(row, OffMetric, 0);
            Marshal.WriteInt32(row, OffProtocol, MIB_IPPROTO_NETMGMT);
            return call(row);
        }
        finally
        {
            Marshal.FreeHGlobal(row);
        }
    }

    private static void WriteIPv4(IntPtr row, int offset, IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Только IPv4", nameof(address));
        Marshal.WriteInt16(row, offset, (short)AF_INET);
        Marshal.WriteInt16(row, offset + 2, 0); // порт
        Marshal.Copy(address.GetAddressBytes(), 0, row + offset + 4, 4);
    }

    /// <summary>Читает таблицу маршрутов теми же смещениями — для проверки раскладки структуры в тестах (прав не требует).</summary>
    internal static IReadOnlyList<(IPAddress Destination, int PrefixLength, uint InterfaceIndex)> ReadIPv4Routes()
    {
        if (GetIpForwardTable2(AF_INET, out var table) != 0)
            return [];
        try
        {
            var count = Marshal.ReadInt32(table);
            var routes = new List<(IPAddress, int, uint)>();
            // MIB_IPFORWARD_TABLE2: ULONG NumEntries, затем массив строк, выровненный на 8
            for (var i = 0; i < count; i++)
            {
                var row = table + 8 + i * RowSize;
                if ((ushort)Marshal.ReadInt16(row, OffDestination) != AF_INET)
                    continue;
                var bytes = new byte[4];
                Marshal.Copy(row + OffDestination + 4, bytes, 0, 4);
                routes.Add((new IPAddress(bytes), Marshal.ReadByte(row, OffPrefixLength), (uint)Marshal.ReadInt32(row, OffIndex)));
            }
            return routes;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    [DllImport("iphlpapi.dll")]
    private static extern void InitializeIpForwardEntry(IntPtr row);

    [DllImport("iphlpapi.dll")]
    private static extern uint CreateIpForwardEntry2(IntPtr row);

    [DllImport("iphlpapi.dll")]
    private static extern uint DeleteIpForwardEntry2(IntPtr row);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetBestRoute2(IntPtr interfaceLuid, uint interfaceIndex, IntPtr sourceAddress,
        IntPtr destinationAddress, uint addressSortOptions, IntPtr bestRoute, IntPtr bestSourceAddress);

    [DllImport("iphlpapi.dll")]
    private static extern uint ConvertInterfaceLuidToIndex(ref ulong luid, out uint index);

    [DllImport("iphlpapi.dll")]
    private static extern uint GetIpForwardTable2(ushort family, out IntPtr table);

    [DllImport("iphlpapi.dll")]
    private static extern void FreeMibTable(IntPtr table);

    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint ConvertInterfaceAliasToLuid(string alias, out ulong luid);
}
