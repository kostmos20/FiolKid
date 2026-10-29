using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZapretPro.Core.Infrastructure;

/// <summary>
/// Помещает программу в Job Object с флагом «убить всех при закрытии».
/// Дочерние процессы (winws, tg-ws-proxy) наследуют задание, поэтому завершаются
/// вместе с программой, даже если она упала или её сняли через диспетчер задач.
/// </summary>
public static class ChildProcesses
{
    private static IntPtr _job;

    public static void BindToCurrentProcess()
    {
        if (_job != IntPtr.Zero)
            return;

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw new Win32Exception();

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            // BREAKAWAY_OK: выйти из задания может только процесс, запущенный с особым флагом (обновлялка,
            // которая должна пережить программу); winws и прокси запускаются без него и остаются внутри
            BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK },
        };
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, (uint)size)
            || !AssignProcessToJobObject(job, Process.GetCurrentProcess().Handle))
        {
            var error = new Win32Exception();
            CloseHandle(job);
            throw error;
        }

        // Дескриптор держим до конца жизни процесса: его закрытие и убивает детей.
        _job = job;
    }

    /// <summary>Завершает процесс вместе со всеми его потомками (PyInstaller-exe запускает дочерний процесс).</summary>
    public static void KillTree(Process? process)
    {
        if (process is null)
            return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // Процесс уже завершился сам
        }
    }

    /// <summary>Полный путь exe чужого процесса или null, если Windows его не отдаёт.</summary>
    public static string? PathOf(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Запустить процесс вне задания: он не завершится вместе с программой. Нужен обновлялке —
    /// она заменяет файлы, когда программа уже закрылась.
    /// </summary>
    public static void StartDetached(string exe, string arguments)
    {
        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        var flags = _job != IntPtr.Zero ? CREATE_BREAKAWAY_FROM_JOB : 0;
        // Строка команды — изменяемый буфер: CreateProcessW вправе писать в неё
        if (!CreateProcess(exe, new System.Text.StringBuilder($"\"{exe}\" {arguments}"), IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero,
                Path.GetDirectoryName(exe), ref startup, out var info))
            throw new Win32Exception();
        CloseHandle(info.hProcess);
        CloseHandle(info.hThread);
    }

    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string application, System.Text.StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref STARTUPINFO startup, out PROCESS_INFORMATION info);

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
