using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Corral.Core;

/// <summary>Priorité d'accès au disque (« E/S »). Haute exige un privilège système : non proposée.</summary>
public enum IoPriorityLevel { VeryLow = 0, Low = 1, Normal = 2 }

/// <summary>Priorité des pages mémoire : les plus basses quittent la RAM en premier sous pression.</summary>
public enum MemoryPriorityLevel { VeryLow = 1, Low = 2, Medium = 3, BelowNormal = 4, Normal = 5 }

/// <summary>
/// Réglages de processus au-delà de la priorité CPU : mode efficacité (EcoQoS, Windows 11),
/// priorité disque et priorité mémoire. Chaque « Get » sert à mémoriser l'état d'origine pour le restaurer.
/// Toutes les méthodes lèvent <see cref="Win32Exception"/> en cas d'échec.
/// </summary>
public static class ProcessTweaks
{
    const int ProcessMemoryPriority = 0;
    const int ProcessPowerThrottling = 4;
    const int ProcessIoPriority = 33;
    const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    /// <summary>
    /// Mode efficacité : true l'impose, false l'interdit, null rend la main à Windows.
    /// </summary>
    public static void SetEfficiencyMode(IntPtr process, bool? enabled)
    {
        var state = new PowerThrottlingState
        {
            Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = enabled == null ? 0 : PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = enabled == true ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
        };
        if (!SetProcessInformation(process, ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>()))
            throw new Win32Exception();
    }

    public static IoPriorityLevel GetIoPriority(IntPtr process)
    {
        int status = NtQueryInformationProcess(process, ProcessIoPriority, out int value, sizeof(int), out _);
        if (status != 0)
            throw new Win32Exception(RtlNtStatusToDosError(status));
        return (IoPriorityLevel)Math.Clamp(value, 0, 2);
    }

    public static void SetIoPriority(IntPtr process, IoPriorityLevel level)
    {
        int value = (int)level;
        int status = NtSetInformationProcess(process, ProcessIoPriority, ref value, sizeof(int));
        if (status != 0)
            throw new Win32Exception(RtlNtStatusToDosError(status));
    }

    public static MemoryPriorityLevel GetMemoryPriority(IntPtr process)
    {
        if (!GetProcessInformation(process, ProcessMemoryPriority, out uint value, sizeof(uint)))
            throw new Win32Exception();
        return (MemoryPriorityLevel)Math.Clamp((int)value, 1, 5);
    }

    public static void SetMemoryPriority(IntPtr process, MemoryPriorityLevel level)
    {
        uint value = (uint)level;
        if (!SetProcessInformation(process, ProcessMemoryPriority, ref value, sizeof(uint)))
            throw new Win32Exception();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessInformation(IntPtr process, int infoClass, ref uint info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessInformation(IntPtr process, int infoClass, out uint info, int size);

    [DllImport("ntdll.dll")]
    static extern int NtSetInformationProcess(IntPtr process, int infoClass, ref int info, int size);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr process, int infoClass, out int info, int size, out int returned);

    [DllImport("ntdll.dll")]
    static extern int RtlNtStatusToDosError(int status);
}
