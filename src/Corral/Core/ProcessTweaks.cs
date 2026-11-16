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

    /// <summary>
    /// CPU Sets par défaut du processus : Windows place ses threads de préférence sur ces cœurs, mais garde le droit
    /// d'en déborder si le système en a besoin (contrairement à l'affinité, stricte). Null ou vide efface les CPU Sets.
    /// </summary>
    public static void SetCpuSets(IntPtr process, uint[]? ids)
    {
        if (!SetProcessDefaultCpuSets(process, ids is { Length: > 0 } ? ids : null, (uint)(ids?.Length ?? 0)))
            throw new Win32Exception();
    }

    /// <summary>CPU Sets par défaut du processus (vide = aucun, cas habituel).</summary>
    public static uint[] GetCpuSets(IntPtr process)
    {
        if (GetProcessDefaultCpuSets(process, null, 0, out uint required))
            return Array.Empty<uint>();
        int error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer)
            throw new Win32Exception(error);
        var ids = new uint[required];
        if (!GetProcessDefaultCpuSets(process, ids, required, out required))
            throw new Win32Exception();
        return ids[..(int)Math.Min(required, (uint)ids.Length)];
    }

    static Dictionary<int, uint>? cpuSetIds;

    /// <summary>Identifiants CPU Set des processeurs logiques cochés dans le masque (groupe 0, comme l'affinité).</summary>
    public static uint[] CpuSetIdsFor(long mask) => CpuSetIdsFor(mask, cpuSetIds ??= TryReadCpuSetIds());

    /// <param name="table">Numéro de processeur logique → identifiant CPU Set.</param>
    public static uint[] CpuSetIdsFor(long mask, IReadOnlyDictionary<int, uint> table) =>
        Enumerable.Range(0, 64).Where(i => ((mask >> i) & 1) == 1 && table.ContainsKey(i)).Select(i => table[i]).ToArray();

    /// <summary>Table lue une fois ; vide si l'API échoue (erreur journalisée une seule fois).</summary>
    static Dictionary<int, uint> TryReadCpuSetIds()
    {
        try { return ReadCpuSetIds(); }
        catch (Exception ex)
        {
            Log.Error(Tr("Lecture des CPU Sets", "Reading CPU Sets"), ex);
            return new Dictionary<int, uint>();
        }
    }

    static Dictionary<int, uint> ReadCpuSetIds()
    {
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint length, IntPtr.Zero, 0);
        if (length == 0)
            throw new Win32Exception();
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0))
                throw new Win32Exception();
            var ids = new Dictionary<int, uint>();
            for (int offset = 0; offset < length;)
            {
                int size = Marshal.ReadInt32(buffer, offset);
                if (size <= 0)
                    break;
                // SYSTEM_CPU_SET_INFORMATION : Size, Type (0 = CpuSetInformation), Id (+8), Group (+12), LogicalProcessorIndex (+14)
                if (Marshal.ReadInt32(buffer, offset + 4) == 0 && Marshal.ReadInt16(buffer, offset + 12) == 0)
                    ids[Marshal.ReadByte(buffer, offset + 14)] = (uint)Marshal.ReadInt32(buffer, offset + 8);
                offset += size;
            }
            return ids;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetProcessDefaultCpuSets(IntPtr process, uint[]? cpuSetIds, uint count);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessDefaultCpuSets(IntPtr process, [Out] uint[]? cpuSetIds, uint count, out uint required);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemCpuSetInformation(IntPtr info, uint length, out uint returned, IntPtr process, uint flags);

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
