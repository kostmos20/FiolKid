using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace ZapretPro.Core.Zapret;

/// <summary>
/// Подключён ли к нашему прокси сам Telegram. Прокси может работать идеально, но пока в Telegram Desktop
/// (или AyuGram, Kotatogram и т. п.) не нажали «Подключить», Telegram ходит мимо него и не работает.
/// Telegram держит соединение с прокси постоянно, поэтому достаточно найти его в таблице TCP-соединений.
/// </summary>
public static class TelegramClients
{
    // Telegram Desktop и его известные копии с другим названием
    private static readonly string[] DesktopNames =
        ["Telegram", "AyuGram", "Kotatogram", "64Gram", "Materialgram", "Forkgram", "Unigram"];

    /// <summary>Запущен ли хоть один Telegram: если нет, «не подключён к прокси» — просто закрыт, а не поломка.</summary>
    public static bool DesktopRunning() => DesktopNames.Any(name =>
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes)
            p.Dispose();
        return processes.Length > 0;
    });

    /// <summary>Имена процессов, подключённых к прокси (кроме самого прокси и нашей программы).</summary>
    public static IReadOnlyList<string> ConnectedTo(int port) => ConnectedTo(port, includeSelf: false);

    internal static IReadOnlyList<string> ConnectedTo(int port, bool includeSelf)
    {
        var self = includeSelf ? -1 : Environment.ProcessId;
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in OwnersOfConnectionsTo(IPAddress.Loopback, port))
        {
            if (pid == self)
                continue;
            try
            {
                using var process = Process.GetProcessById(pid);
                if (!process.ProcessName.StartsWith("TgWsProxy", StringComparison.OrdinalIgnoreCase))
                    names.Add(process.ProcessName);
            }
            catch (ArgumentException)
            {
                // Процесс уже завершился
            }
        }
        return names.ToList();
    }

    private static IEnumerable<int> OwnersOfConnectionsTo(IPAddress address, int port)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
                if (result == ERROR_INSUFFICIENT_BUFFER)
                    continue; // таблица выросла между вызовами
                if (result != 0)
                    throw new Win32Exception((int)result);

                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                var target = BitConverter.ToUInt32(address.GetAddressBytes());
                var pids = new List<int>();
                for (var i = 0; i < count; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(buffer + 4 + i * rowSize);
                    // Порт хранится в сетевом порядке байт в младших 16 битах
                    var remotePort = (int)(((row.RemotePort & 0xff) << 8) | ((row.RemotePort >> 8) & 0xff));
                    if (row.State == MIB_TCP_STATE_ESTAB && row.RemoteAddr == target && remotePort == port)
                        pids.Add((int)row.OwningPid);
                }
                return pids.Distinct().ToList();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return [];
    }

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint MIB_TCP_STATE_ESTAB = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
