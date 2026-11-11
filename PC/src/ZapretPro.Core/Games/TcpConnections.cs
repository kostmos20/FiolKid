using System.Net;
using System.Runtime.InteropServices;

namespace ZapretPro.Core.Games;

/// <summary>
/// Прямые TCP-соединения процессов (IPv4) и их обрыв. Нужно, когда адрес игры только что ушёл в VPN:
/// уже открытое соединение так и шло бы мимо туннеля, а провайдер часто не блокирует, а душит — загрузка
/// не падает, а висит. Порвали — игра сама переподключается, уже через VPN.
/// </summary>
public static class TcpConnections
{
    public readonly record struct Connection(int Pid, IPAddress Remote, uint LocalAddr, uint LocalPort, uint RemoteAddr, uint RemotePort, uint State);

    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const uint StateEstablished = 5;
    private const uint StateDeleteTcb = 12;

    /// <summary>Установленные TCP-соединения IPv4 этих процессов.</summary>
    public static IReadOnlyList<Connection> Of(IReadOnlySet<int> pids)
    {
        var result = new List<Connection>();
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0) != 0)
                    continue; // таблица выросла — размер обновлён, пробуем ещё раз
                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(buffer + 4 + i * rowSize);
                    if (row.State == StateEstablished && pids.Contains((int)row.OwningPid))
                        result.Add(new Connection((int)row.OwningPid, new IPAddress(row.RemoteAddr), row.LocalAddr, row.LocalPort,
                            row.RemoteAddr, row.RemotePort, row.State));
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return result;
    }

    /// <summary>Порвать соединения процессов с этим адресом. Возвращает, сколько порвано.</summary>
    public static int Close(IReadOnlySet<int> pids, IPAddress remote) =>
        Of(pids).Where(c => c.Remote.Equals(remote)).Count(Close);

    public static bool Close(Connection c)
    {
        var row = new MibTcpRow
        {
            State = StateDeleteTcb,
            LocalAddr = c.LocalAddr,
            LocalPort = c.LocalPort,
            RemoteAddr = c.RemoteAddr,
            RemotePort = c.RemotePort,
        };
        return SetTcpEntry(ref row) == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    private static extern uint SetTcpEntry(ref MibTcpRow row);
}
