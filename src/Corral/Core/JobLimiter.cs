using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Corral.Core;

/// <summary>
/// Limites CPU / mémoire via un Job Object. Le handle du job est fermé tout de suite :
/// le job reste en vie tant que le processus y est, et sans KILL_ON_JOB_CLOSE rien n'est tué.
/// </summary>
public static class JobLimiter
{
    const int JobObjectExtendedLimitInformation = 9;
    const int JobObjectCpuRateControlInformation = 15;
    const uint JOB_OBJECT_LIMIT_PROCESS_MEMORY = 0x100;
    const uint JOB_OBJECT_CPU_RATE_CONTROL_ENABLE = 0x1;
    const uint JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP = 0x4;

    public static void Apply(Process process, int? cpuPercent, int? memoryMb)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw new Win32Exception();
        try
        {
            if (cpuPercent is > 0 and < 100)
            {
                var cpu = new JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
                {
                    ControlFlags = JOB_OBJECT_CPU_RATE_CONTROL_ENABLE | JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
                    CpuRate = (uint)cpuPercent.Value * 100, // en centièmes de %
                };
                Set(job, JobObjectCpuRateControlInformation, cpu);
            }
            if (memoryMb is > 0)
            {
                var ext = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                ext.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_PROCESS_MEMORY;
                ext.ProcessMemoryLimit = (UIntPtr)((ulong)memoryMb.Value * 1024 * 1024);
                Set(job, JobObjectExtendedLimitInformation, ext);
            }
            if (!AssignProcessToJobObject(job, process.Handle))
                throw new Win32Exception();
        }
        finally
        {
            CloseHandle(job);
        }
    }

    public static bool IsInJob(Process process) =>
        IsProcessInJob(process.Handle, IntPtr.Zero, out var result) && result;

    static void Set<T>(IntPtr job, int infoClass, T info) where T : struct
    {
        int size = Marshal.SizeOf<T>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, infoClass, buffer, (uint)size))
                throw new Win32Exception();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);
}
