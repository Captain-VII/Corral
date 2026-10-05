using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Corral.Core;

/// <summary>
/// Historique compact de l'usage par programme (instances regroupées par nom), pour le « top » du graphique.
/// Seuls les 15 plus gros consommateurs de chaque mesure sont gardés, pour borner la mémoire.
/// </summary>
public sealed class TopTracker
{
    public enum Metric { Cpu, Memory }

    public sealed record Entry(string Name, double Value);

    readonly object sync = new();
    readonly Queue<(DateTime Time, Dictionary<string, (double Cpu, long Mem)> Usage)> samples = new();
    readonly TimeSpan keep;
    const int PerSample = 15;

    public TopTracker(TimeSpan keep) => this.keep = keep;

    public void Add(DateTime time, IEnumerable<ProcessRow> rows)
    {
        var byName = new Dictionary<string, (double Cpu, long Mem)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            byName.TryGetValue(r.Name, out var u);
            byName[r.Name] = (u.Cpu + r.Cpu, u.Mem + r.MemoryBytes);
        }
        var kept = byName.OrderByDescending(kv => kv.Value.Cpu).Take(PerSample)
            .Concat(byName.OrderByDescending(kv => kv.Value.Mem).Take(PerSample))
            .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        lock (sync)
        {
            samples.Enqueue((time, kept));
            while (time - samples.Peek().Time > keep)
                samples.Dequeue();
        }
    }

    /// <summary>
    /// Les <paramref name="count"/> plus gourmands depuis <paramref name="from"/> :
    /// CPU moyen sur la période (en %), ou pic de mémoire (en octets).
    /// </summary>
    public List<Entry> Top(DateTime from, Metric metric, int count = 5)
    {
        lock (sync)
        {
            var window = samples.Where(s => s.Time >= from).ToList();
            if (window.Count == 0)
                return new();
            var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, usage) in window)
            {
                foreach (var (name, u) in usage)
                {
                    totals.TryGetValue(name, out var v);
                    totals[name] = metric == Metric.Cpu ? v + u.Cpu : Math.Max(v, u.Mem);
                }
            }
            return totals
                .Select(kv => new Entry(kv.Key, metric == Metric.Cpu ? kv.Value / window.Count : kv.Value))
                .Where(e => e.Value > 0)
                .OrderByDescending(e => e.Value)
                .Take(count)
                .ToList();
        }
    }
}

/// <summary>
/// Statistiques des interventions de ProBalance, par jour (30 derniers jours), enregistrées dans stats.json.
/// </summary>
public sealed class ProBalanceStats
{
    public sealed class Day
    {
        public DateOnly Date { get; set; }
        public int Count { get; set; }
        public Dictionary<string, int> ByProgram { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    readonly object sync = new();
    readonly string? path;
    readonly Func<DateOnly> today;
    List<Day> days = new();

    public ProBalanceStats(string? path, Func<DateOnly>? today = null)
    {
        this.path = path;
        this.today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));
        Load();
    }

    public void Record(IEnumerable<string> programs)
    {
        lock (sync)
        {
            var date = today();
            var day = days.FirstOrDefault(d => d.Date == date);
            if (day == null)
            {
                day = new Day { Date = date };
                days.Add(day);
                days.RemoveAll(d => d.Date < date.AddDays(-30));
            }
            foreach (var name in programs)
            {
                day.Count++;
                day.ByProgram.TryGetValue(name, out var n);
                day.ByProgram[name] = n + 1;
            }
            Save();
        }
    }

    public int CountToday()
    {
        lock (sync)
            return days.FirstOrDefault(d => d.Date == today())?.Count ?? 0;
    }

    /// <summary>Interventions des <paramref name="lastDays"/> derniers jours (aujourd'hui compris).</summary>
    public int Count(int lastDays)
    {
        lock (sync)
        {
            var from = today().AddDays(-(lastDays - 1));
            return days.Where(d => d.Date >= from).Sum(d => d.Count);
        }
    }

    /// <summary>Programmes le plus souvent abaissés sur la période.</summary>
    public List<(string Name, int Count)> TopPrograms(int lastDays, int count = 5)
    {
        lock (sync)
        {
            var from = today().AddDays(-(lastDays - 1));
            return days.Where(d => d.Date >= from)
                .SelectMany(d => d.ByProgram)
                .GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, g.Sum(kv => kv.Value)))
                .OrderByDescending(x => x.Item2)
                .Take(count)
                .ToList();
        }
    }

    public void Reset()
    {
        lock (sync)
        {
            days.Clear();
            Save();
        }
    }

    void Load()
    {
        if (path == null || !File.Exists(path))
            return;
        try
        {
            days = JsonSerializer.Deserialize<List<Day>>(File.ReadAllText(path)) ?? new();
            foreach (var d in days)
                d.ByProgram = new Dictionary<string, int>(d.ByProgram ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Error("Lecture des statistiques ProBalance", ex);
            days = new();
        }
    }

    void Save()
    {
        if (path == null)
            return;
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(days));
        }
        catch (Exception ex)
        {
            Log.Error("Enregistrement des statistiques ProBalance", ex);
        }
    }
}

/// <summary>Informations détaillées sur un processus (fenêtre « Détails »). Chaque champ peut être inconnu.</summary>
public sealed record ProcessDetails(
    int Pid, string Name, string? Path, string? CommandLine, int? ParentPid, string? ParentName,
    DateTime? StartTime, int? Threads, int? Handles, long? WorkingSet, long? PrivateBytes, TimeSpan? CpuTime,
    string? Priority, string? Company, string? Description, string? Version)
{
    public static ProcessDetails Read(int pid)
    {
        using var p = Process.GetProcessById(pid);
        string name = p.ProcessName;
        string? path = Native.GetProcessPath(pid);
        T? Try<T>(Func<T> f) where T : struct { try { return f(); } catch { return null; } }
        string? TryS(Func<string?> f) { try { return f(); } catch { return null; } }

        int? parent = ReadParentPid(pid);
        string? parentName = parent is { } pp ? TryS(() => { using var q = Process.GetProcessById(pp); return q.ProcessName; }) : null;
        FileVersionInfo? info = null;
        if (path != null)
        {
            try { info = FileVersionInfo.GetVersionInfo(path); }
            catch { } // fichier inaccessible
        }

        return new ProcessDetails(pid, name, path, ReadCommandLine(pid), parent, parentName,
            Try(() => p.StartTime), Try(() => p.Threads.Count), Try(() => p.HandleCount),
            Try(() => p.WorkingSet64), Try(() => p.PrivateMemorySize64), Try(() => p.TotalProcessorTime),
            TryS(() => Engine.PriorityLabel(p.PriorityClass)),
            NullIfEmpty(info?.CompanyName), NullIfEmpty(info?.FileDescription), NullIfEmpty(info?.ProductVersion ?? info?.FileVersion));
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int ProcessBasicInformation = 0;
    const int ProcessCommandLineInformation = 60;

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    static int? ReadParentPid(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return null;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            return NtQueryInformationProcess(h, ProcessBasicInformation, ref pbi, Marshal.SizeOf(pbi), out _) == 0
                ? (int)pbi.InheritedFromUniqueProcessId
                : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>Ligne de commande (Windows 8.1+, droits limités suffisants).</summary>
    static string? ReadCommandLine(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return null;
        try
        {
            NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out int size);
            if (size <= 0)
                return null;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buffer, size, out _) != 0)
                    return null;
                // UNICODE_STRING : Length (ushort), MaximumLength (ushort), [alignement], Buffer (pointeur)
                int length = (ushort)Marshal.ReadInt16(buffer);
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return length > 0 ? Marshal.PtrToStringUni(text, length / 2) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref PROCESS_BASIC_INFORMATION info, int size, out int returned);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int size, out int returned);
}
