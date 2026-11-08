using System.Runtime.InteropServices;

namespace LitSSHmcp.Core.Services.Security;

/// <summary>
/// 把宿主进程加入 Windows Job Object（KILL_ON_JOB_CLOSE），确保 App/Web 退出时
/// 其派生的 MCP 子进程、终端进程等不会残留。非 Windows 平台为空操作。
/// </summary>
public static class ProcessJob
{
    private const uint JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly object Gate = new();
    private static IntPtr _job;

    public static void EnsureKillOnClose()
    {
        if (!OperatingSystem.IsWindows())
            return;

        lock (Gate)
        {
            if (_job != IntPtr.Zero)
                return;

            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                return;

            try
            {
                var info = new JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose
                    }
                };
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref info,
                        (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
                    return;

                if (!AssignProcessToJobObject(job, GetCurrentProcess()))
                    return;

                // 故意保留句柄：进程退出时由 OS 关闭并终止 Job 内所有子进程。
                _job = job;
                job = IntPtr.Zero;
            }
            finally
            {
                if (job != IntPtr.Zero)
                    CloseHandle(job);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job, uint infoClass, ref JobObjectExtendedLimitInformation info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
