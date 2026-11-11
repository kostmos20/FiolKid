using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZapretPro.Core.Infrastructure;

/// <summary>Запущенные процессы и пути их exe — быстро, без чтения списка модулей.</summary>
public static class ProcessPaths
{
    public readonly record struct Entry(int Pid, string Path);

    public static IReadOnlyList<Entry> Snapshot()
    {
        var list = new List<Entry>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (Of(process.Id) is { } path)
                    list.Add(new Entry(process.Id, path));
            }
        }
        return list;
    }

    /// <summary>Путь exe процесса или null (процесс уже закрылся или Windows его не отдаёт).</summary>
    public static string? Of(int pid)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, char[] name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
