using System.Globalization;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace Corral.Core;

/// <summary>Octets lus et écrits par un processus depuis son lancement (disque, réseau et périphériques confondus).</summary>
public static class ProcessIo
{
    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

    /// <summary>Total lu + écrit, ou null si le processus est inaccessible.</summary>
    public static ulong? Read(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return null;
        try
        {
            return GetProcessIoCounters(h, out var c) ? c.ReadTransferCount + c.WriteTransferCount : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}

/// <summary>
/// Compteurs de performances PDH lus ensemble, par leur nom anglais (indépendant de la langue de Windows).
/// Au premier échec, la lecture est abandonnée (journalisé une fois). À n'utiliser que depuis un seul thread.
/// </summary>
public sealed class PdhCounters : IDisposable
{
    const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000, PDH_MORE_DATA = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    struct Item
    {
        public IntPtr Name;
        public uint Status;
        public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);

    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);

    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(IntPtr query);

    readonly string[] paths;
    readonly string warning;
    readonly IntPtr[] counters;
    IntPtr query;
    bool failed;

    /// <param name="warning">Début du message journalisé en cas d'échec (déjà traduit).</param>
    public PdhCounters(string warning, params string[] paths)
    {
        this.warning = warning;
        this.paths = paths;
        counters = new IntPtr[paths.Length];
    }

    /// <summary>Valeurs par instance de chaque compteur (dans l'ordre des chemins), ou null si indisponibles.
    /// Un compteur sans valeur (premier passage, aucune instance) donne une liste vide.</summary>
    public List<(string Instance, double Value)>[]? Collect()
    {
        if (failed)
            return null;
        try
        {
            if (query == IntPtr.Zero)
            {
                if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0)
                    return Fail(Tr("compteurs indisponibles", "counters unavailable"));
                for (int i = 0; i < paths.Length; i++)
                    if (PdhAddEnglishCounter(query, paths[i], IntPtr.Zero, out counters[i]) != 0)
                        return Fail(Tr("compteurs indisponibles", "counters unavailable"));
            }
            if (PdhCollectQueryData(query) != 0)
                return null;
            var result = new List<(string, double)>[counters.Length];
            for (int i = 0; i < counters.Length; i++)
                result[i] = Read(counters[i]);
            return result;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    static List<(string, double)> Read(IntPtr counter)
    {
        var values = new List<(string, double)>();
        uint size = 0;
        uint status = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0)
            return values; // premier passage (pas encore de référence) ou aucune instance
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, buffer) != 0)
                return values;
            int stride = Marshal.SizeOf<Item>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Item>(buffer + i * stride);
                if (item.Status <= 1 && Marshal.PtrToStringUni(item.Name) is { } name) // donnée valide ou nouvelle
                    values.Add((name, item.Value));
            }
            return values;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    List<(string, double)>[]? Fail(string reason)
    {
        failed = true;
        Log.Warn(warning + reason);
        return null;
    }

    public void Dispose()
    {
        if (query != IntPtr.Zero)
            PdhCloseQuery(query);
        query = IntPtr.Zero;
    }
}

/// <summary>
/// Usage du GPU par processus, lu dans les compteurs de performances « GPU Engine » (comme le Gestionnaire des tâches) :
/// pour chaque processus, on additionne les moteurs d'un même type (3D, Copy, VideoDecode…) et on garde le type le plus chargé.
/// À n'utiliser que depuis un seul thread (celui du moteur).
/// </summary>
public sealed class GpuSampler : IDisposable
{
    public const string Counter = @"\GPU Engine(*)\Utilization Percentage";
    static readonly IReadOnlyDictionary<int, double> Empty = new Dictionary<int, double>();

    readonly PdhCounters pdh = new(Tr("Usage GPU par processus désactivé : ", "Per-process GPU usage disabled: "), Counter);

    /// <summary>Usage par PID (0-100 %). Vide si les compteurs sont indisponibles (pilote ancien, machine virtuelle…).</summary>
    public IReadOnlyDictionary<int, double> Sample() => pdh.Collect() is { } v ? Aggregate(v[0]) : Empty;

    /// <summary>Regroupe les instances « pid_1234_luid_…_eng_0_engtype_3D » par processus.</summary>
    public static Dictionary<int, double> Aggregate(IEnumerable<(string Instance, double Value)> values)
    {
        var byEngine = new Dictionary<(int Pid, string Type), double>();
        foreach (var (instance, value) in values)
        {
            if (!instance.StartsWith("pid_", StringComparison.Ordinal))
                continue;
            int end = instance.IndexOf('_', 4);
            if (end < 0 || !int.TryParse(instance.AsSpan(4, end - 4), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid == 0)
                continue;
            int t = instance.IndexOf("engtype_", StringComparison.Ordinal);
            var type = t >= 0 ? instance[(t + 8)..] : "";
            byEngine.TryGetValue((pid, type), out var sum);
            byEngine[(pid, type)] = sum + Math.Max(0, value);
        }
        var result = new Dictionary<int, double>();
        foreach (var ((pid, _), v) in byEngine)
            result[pid] = Math.Min(100, Math.Max(result.GetValueOrDefault(pid), v));
        return result;
    }

    /// <summary>Usage global : tous processus confondus, par carte et type de moteur ; le plus chargé l'emporte (comme le Gestionnaire des tâches).</summary>
    public static double Total(IEnumerable<(string Instance, double Value)> values) =>
        ByEngine(values) is { Count: > 0 } engines ? engines.Values.Max() : 0;

    /// <summary>Charge par type de moteur (« 3D », « VideoDecode »…), tous processus confondus ; avec deux cartes, la plus chargée.</summary>
    public static Dictionary<string, double> ByEngine(IEnumerable<(string Instance, double Value)> values)
    {
        var perCard = new Dictionary<(string Card, string Type), double>();
        foreach (var (instance, value) in values)
        {
            int luid = instance.IndexOf("_luid_", StringComparison.Ordinal), eng = instance.IndexOf("_eng_", StringComparison.Ordinal);
            int t = instance.IndexOf("engtype_", StringComparison.Ordinal);
            if (luid < 0 || eng < luid || t < eng)
                continue;
            var key = (instance[luid..eng], instance[(t + 8)..]);
            perCard[key] = perCard.GetValueOrDefault(key) + Math.Max(0, value);
        }
        var result = new Dictionary<string, double>();
        foreach (var ((_, type), v) in perCard)
            result[type] = Math.Min(100, Math.Max(result.GetValueOrDefault(type), v));
        return result;
    }

    public void Dispose() => pdh.Dispose();
}

/// <summary>
/// Mesures de tout le PC pour la page Graphique. <see cref="Read"/> (débits, GPU) est appelé à chaque instantané du moteur ;
/// <see cref="ReadDetails"/> (par cœur, par disque, par carte réseau) seulement quand la page est affichée, depuis un autre thread :
/// chaque requête PDH n'est utilisée que par un seul thread.
/// </summary>
public sealed class SystemSampler : IDisposable
{
    /// <summary>Débits en octets/s ; GPU en % (null si non lu), mémoire vidéo en octets, charge par type de moteur.</summary>
    public sealed record Sample(double DiskRead, double DiskWrite, double NetReceived, double NetSent,
        double? Gpu, IReadOnlyDictionary<string, double> GpuEngines, long GpuDedicated, long GpuShared);

    /// <summary>Charge de chaque processeur logique (%), fréquence (MHz), défauts de page/s, activité (%) et file d'attente
    /// de chaque disque physique, trafic (octets/s) et débit nominal (bits/s) de chaque carte réseau.</summary>
    public sealed record Details(IReadOnlyList<double> Threads, double? FrequencyMHz, double PageFaults,
        IReadOnlyList<(string Name, double Active, double Queue)> Disks,
        IReadOnlyList<(string Name, double BytesPerSec, double Bandwidth)> Adapters);

    /// <summary>Mémoire (octets) et compteurs du système, via GetPerformanceInfo.</summary>
    public sealed record Performance(long CommitTotal, long CommitLimit, long PhysicalTotal, long PhysicalAvailable, long SystemCache,
        int Handles, int Processes, int Threads);

    static readonly IReadOnlyDictionary<string, double> NoEngines = new Dictionary<string, double>();

    readonly PdhCounters io = new(Tr("Débits disque et réseau indisponibles : ", "Disk and network rates unavailable: "),
        @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", @"\PhysicalDisk(_Total)\Disk Write Bytes/sec",
        @"\Network Interface(*)\Bytes Received/sec", @"\Network Interface(*)\Bytes Sent/sec");
    readonly PdhCounters gpu = new(Tr("Usage GPU global indisponible : ", "Overall GPU usage unavailable: "),
        GpuSampler.Counter, @"\GPU Adapter Memory(*)\Dedicated Usage", @"\GPU Adapter Memory(*)\Shared Usage");
    readonly PdhCounters detail = new(Tr("Détail de la page Graphique indisponible : ", "Charts page details unavailable: "),
        @"\Processor Information(*)\% Processor Time", @"\Processor Information(_Total)\% Processor Performance",
        @"\Processor Information(_Total)\Processor Frequency", @"\Memory\Page Faults/sec",
        @"\PhysicalDisk(*)\% Idle Time", @"\PhysicalDisk(*)\Avg. Disk Queue Length",
        @"\Network Interface(*)\Bytes Total/sec", @"\Network Interface(*)\Current Bandwidth");

    /// <summary>Null si les compteurs sont indisponibles. Le GPU (compteurs coûteux) n'est lu que si <paramref name="withGpu"/>.</summary>
    public Sample? Read(bool withGpu)
    {
        if (io.Collect() is not { } v)
            return null;
        double? total = null;
        var engines = NoEngines;
        long dedicated = 0, shared = 0;
        if (withGpu && gpu.Collect() is { } gv)
        {
            engines = GpuSampler.ByEngine(gv[0]);
            total = engines.Count > 0 ? engines.Values.Max() : 0;
            // shortcut: carte la plus remplie seulement (dGPU en général), sans relier le LUID au nom lu dans le registre
            if (gv[1].Count > 0)
            {
                var busiest = gv[1].MaxBy(x => x.Value);
                dedicated = (long)busiest.Value;
                shared = (long)gv[2].FirstOrDefault(x => x.Instance == busiest.Instance).Value;
            }
        }
        return new Sample(v[0].Sum(x => x.Value), v[1].Sum(x => x.Value), v[2].Sum(x => x.Value), v[3].Sum(x => x.Value),
            total, engines, dedicated, shared);
    }

    public Details? ReadDetails()
    {
        if (detail.Collect() is not { } v)
            return null;
        var threads = v[0].Where(x => !x.Instance.Contains("_Total", StringComparison.Ordinal))
            .Select(x => (Key: LogicalIndex(x.Instance), x.Value)).OrderBy(x => x.Key).Select(x => Math.Clamp(x.Value, 0, 100)).ToList();
        double? freq = v[1].Count > 0 && v[2].Count > 0 ? v[2][0].Value * v[1][0].Value / 100 : null;
        var queues = v[5].ToDictionary(x => x.Instance, x => x.Value);
        var disks = v[4].Where(x => x.Instance != "_Total").OrderBy(x => x.Instance, StringComparer.Ordinal)
            .Select(x => (x.Instance, Math.Clamp(100 - x.Value, 0, 100), queues.GetValueOrDefault(x.Instance))).ToList();
        var bandwidth = v[7].GroupBy(x => x.Instance).ToDictionary(g => g.Key, g => g.First().Value);
        var adapters = v[6].Where(x => bandwidth.GetValueOrDefault(x.Instance) > 0)
            .Select(x => (x.Instance, x.Value, bandwidth[x.Instance])).OrderByDescending(x => x.Item2).ToList();
        return new Details(threads, freq, v[3].Sum(x => x.Value), disks, adapters);
    }

    /// <summary>Instance « groupe,numéro » d'un processeur logique, triable (« 0,10 » après « 0,9 »).</summary>
    internal static (int Group, int Number) LogicalIndex(string instance)
    {
        var parts = instance.Split(',');
        return (parts.Length == 2 && int.TryParse(parts[0], out int g) && int.TryParse(parts[1], out int n)) ? (g, n) : (int.MaxValue, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, uint size);

    public static Performance? ReadPerformance()
    {
        if (!GetPerformanceInfo(out var i, (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
            return null;
        long page = (long)i.PageSize;
        return new Performance((long)i.CommitTotal * page, (long)i.CommitLimit * page, (long)i.PhysicalTotal * page,
            (long)i.PhysicalAvailable * page, (long)i.SystemCache * page, (int)i.HandleCount, (int)i.ProcessCount, (int)i.ThreadCount);
    }

    public void Dispose()
    {
        io.Dispose();
        gpu.Dispose();
        detail.Dispose();
    }
}

/// <summary>Noms du matériel affichés par la page Graphique, lus une fois dans le registre.</summary>
public static class Hardware
{
    static string? cpuName;
    static (string Name, long Memory)? gpu;

    public static string CpuName => cpuName ??= Read(() =>
        (Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0")?.GetValue("ProcessorNameString") as string)?.Trim() ?? "", "");

    /// <summary>Carte graphique qui a le plus de mémoire dédiée (la carte dédiée plutôt que le GPU intégré).</summary>
    public static (string Name, long Memory) Gpu => gpu ??= Read(ReadGpu, ("", 0L));

    static (string, long) ReadGpu()
    {
        using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        var best = ("", 0L);
        foreach (var sub in cls?.GetSubKeyNames() ?? Array.Empty<string>())
        {
            using var k = Read(() => cls!.OpenSubKey(sub), null); // « Properties » est interdite en lecture
            if (k?.GetValue("DriverDesc") is not string name)
                continue;
            long memory = k.GetValue("HardwareInformation.qwMemorySize") switch
            {
                long q => q,
                byte[] b when b.Length >= 8 => BitConverter.ToInt64(b),
                _ => k.GetValue("HardwareInformation.MemorySize") switch { int d => (uint)d, byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b), _ => 0 },
            };
            if (best.Item1 == "" || memory > best.Item2)
                best = (name, memory);
        }
        return best;
    }

    static T Read<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception) { return fallback; } // registre illisible : on affiche sans le nom
    }
}

/// <summary>Historique récent de chaque processus (CPU, GPU, mémoire, E/S), pour les courbes de la fiche détails. Thread-safe.</summary>
public sealed class ProcessHistory
{
    public readonly record struct Sample(DateTime Time, float Cpu, float Gpu, long Memory, long Io);

    readonly object sync = new();
    readonly Dictionary<(int Pid, string Name), Queue<Sample>> series = new();
    readonly TimeSpan keep;

    public ProcessHistory(TimeSpan keep) => this.keep = keep;

    public TimeSpan Keep => keep;

    public void Add(DateTime time, IReadOnlyList<ProcessRow> rows)
    {
        lock (sync)
        {
            var seen = new HashSet<(int, string)>();
            foreach (var r in rows)
            {
                var key = (r.Pid, r.Name);
                seen.Add(key);
                if (!series.TryGetValue(key, out var q))
                    series[key] = q = new Queue<Sample>();
                q.Enqueue(new Sample(time, (float)r.Cpu, (float)r.Gpu, r.MemoryBytes, r.IoBytesPerSec));
                while (time - q.Peek().Time > keep)
                    q.Dequeue();
            }
            // Processus fermés : leur historique part avec eux
            foreach (var gone in series.Keys.Where(k => !seen.Contains(k)).ToList())
                series.Remove(gone);
        }
    }

    public List<Sample> Get(int pid, string name)
    {
        lock (sync)
            return series.TryGetValue((pid, name), out var q) ? q.ToList() : new();
    }
}
