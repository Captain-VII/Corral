using System.Numerics;
using System.Runtime.InteropServices;

namespace Corral.Core;

/// <summary>Choix d'affinité adapté au processeur (cœurs performants, V-Cache, sans SMT…).</summary>
public sealed record AffinityPreset(string Name, string Description, long Mask);

/// <summary>
/// Topologie du processeur lue via GetLogicalProcessorInformationEx : cœurs physiques (avec leur classe
/// d'efficacité sur les processeurs hybrides Intel) et caches L3 (un par CCD sur les Ryzen à plusieurs CCD).
/// Seul le groupe de processeurs 0 est pris en compte (jusqu'à 64 processeurs logiques).
/// </summary>
public sealed class CpuTopology
{
    public sealed record Core(long Mask, byte EfficiencyClass);
    public sealed record L3Cache(long Mask, long SizeBytes);

    public IReadOnlyList<Core> Cores { get; }
    public IReadOnlyList<L3Cache> L3Caches { get; }
    public long AllMask { get; }

    public CpuTopology(IReadOnlyList<Core> cores, IReadOnlyList<L3Cache> l3)
    {
        Cores = cores;
        L3Caches = l3;
        AllMask = cores.Aggregate(0L, (m, c) => m | c.Mask);
    }

    static CpuTopology? current;

    /// <summary>Topologie de cette machine (lue une fois), ou null si l'API échoue.</summary>
    public static CpuTopology? Current => current ??= TryRead();

    /// <summary>
    /// Choix proposés, uniquement ceux qui ont un sens sur ce processeur :
    /// cœurs P / cœurs E (hybride), CCD avec V-Cache / chaque CCD (plusieurs L3), sans SMT (Hyper-Threading actif).
    /// </summary>
    public IReadOnlyList<AffinityPreset> Presets()
    {
        var list = new List<AffinityPreset>();

        var classes = Cores.Select(c => c.EfficiencyClass).Distinct().OrderBy(c => c).ToList();
        if (classes.Count > 1)
        {
            long pMask = Cores.Where(c => c.EfficiencyClass == classes[^1]).Aggregate(0L, (m, c) => m | c.Mask);
            long eMask = Cores.Where(c => c.EfficiencyClass == classes[0]).Aggregate(0L, (m, c) => m | c.Mask);
            list.Add(new("Cœurs performants", $"Uniquement les cœurs P ({BitOperations.PopCount((ulong)pMask)} threads). Idéal pour les jeux.", pMask));
            list.Add(new("Cœurs efficaces", $"Uniquement les cœurs E ({BitOperations.PopCount((ulong)eMask)} threads). Pour les tâches de fond.", eMask));
        }

        if (L3Caches.Count > 1)
        {
            var biggest = L3Caches.OrderByDescending(c => c.SizeBytes).First();
            bool vcache = L3Caches.Any(c => c.SizeBytes < biggest.SizeBytes);
            if (vcache)
                list.Add(new("CCD avec V-Cache", $"Le bloc de cœurs avec le plus de cache ({biggest.SizeBytes / (1024 * 1024)} Mo). Idéal pour les jeux.", biggest.Mask));
            for (int i = 0; i < L3Caches.Count; i++)
                list.Add(new($"CCD {i + 1}", $"Le bloc de cœurs n° {i + 1} ({BitOperations.PopCount((ulong)L3Caches[i].Mask)} threads, {L3Caches[i].SizeBytes / (1024 * 1024)} Mo de cache).", L3Caches[i].Mask));
        }

        if (Cores.Any(c => BitOperations.PopCount((ulong)c.Mask) > 1))
        {
            // Premier thread de chaque cœur physique
            long noSmt = Cores.Aggregate(0L, (m, c) => m | (c.Mask & -c.Mask));
            list.Add(new("Sans SMT", $"Un seul thread par cœur physique ({Cores.Count} threads). Peut aider certains jeux anciens.", noSmt));
        }
        return list;
    }

    static CpuTopology? TryRead()
    {
        try
        {
            return new CpuTopology(ReadCores(), ReadL3());
        }
        catch (Exception ex)
        {
            Log.Error("Lecture de la topologie du processeur", ex);
            return null;
        }
    }

    const int RelationProcessorCore = 0;
    const int RelationCache = 2;

    static List<Core> ReadCores()
    {
        var cores = new List<Core>();
        foreach (var (buffer, offset) in Entries(RelationProcessorCore))
        {
            byte efficiency = Marshal.ReadByte(buffer, offset + 9);
            ushort groups = (ushort)Marshal.ReadInt16(buffer, offset + 30);
            // GROUP_AFFINITY alignée sur 8 octets à partir de l'offset 32
            if (groups > 0 && Marshal.ReadInt16(buffer, offset + 32 + 8) == 0)
                cores.Add(new Core(Marshal.ReadInt64(buffer, offset + 32), efficiency));
        }
        return cores;
    }

    static List<L3Cache> ReadL3()
    {
        var caches = new List<L3Cache>();
        foreach (var (buffer, offset) in Entries(RelationCache))
        {
            byte level = Marshal.ReadByte(buffer, offset + 8);
            if (level != 3)
                continue;
            uint size = (uint)Marshal.ReadInt32(buffer, offset + 12);
            if (Marshal.ReadInt16(buffer, offset + 40 + 8) == 0) // groupe 0
                caches.Add(new L3Cache(Marshal.ReadInt64(buffer, offset + 40), size));
        }
        return caches.OrderBy(c => c.Mask & -c.Mask).ToList(); // dans l'ordre des cœurs
    }

    /// <summary>Parcourt les entrées SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX d'une relation donnée.</summary>
    static IEnumerable<(IntPtr Buffer, int Offset)> Entries(int relationship)
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(relationship, IntPtr.Zero, ref length);
        if (length == 0)
            yield break;
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(relationship, buffer, ref length))
                throw new System.ComponentModel.Win32Exception();
            int offset = 0;
            while (offset < length)
            {
                int size = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0)
                    break;
                yield return (buffer, offset);
                offset += size;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}
