using System.Diagnostics;
using Corral.Models;

namespace Corral.Core;

public sealed record ProcessRow(int Pid, string Name, double Cpu, long MemoryBytes, string? Rule, bool Restrained);

public sealed record EngineSnapshot(double SystemCpu, bool Paused, IReadOnlyList<ProcessRow> Rows);

/// <summary>
/// Moteur : un seul thread (timer non réentrant) sous verrou. À chaque passage il liste les processus,
/// applique les règles aux nouveaux, fait tourner ProBalance et publie un instantané pour l'interface.
/// Toute modification est mémorisée pour être annulée à l'arrêt, en pause ou au changement de règles.
/// </summary>
public sealed class Engine : IDisposable
{
    sealed class Tracked
    {
        public Tracked(string name) => Name = name;
        public readonly string Name;
        public Rule? Rule;
        public ProcessPriorityClass? OrigPriority;
        public IntPtr? OrigAffinity;
        public ProcessPriorityClass? ProBalanceOrig;
        public TimeSpan? LastCpu;
        public bool CpuDenied;
    }

    readonly object sync = new();
    readonly PowerPlanManager power;
    readonly ProBalanceLogic proBalance = new();
    readonly SystemCpuSampler systemCpu = new();
    readonly Func<int> foregroundPid;
    readonly Dictionary<ProcKey, Tracked> tracked = new();
    readonly HashSet<ProcKey> jobLimited = new();
    readonly Stopwatch clock = new();
    readonly int ownPid = Environment.ProcessId;
    readonly int cores = Environment.ProcessorCount;
    Settings settings;
    bool reapply;
    bool paused;
    bool stopped;
    System.Threading.Timer? timer;

    public Engine(Settings settings, IPowerPlanApi powerApi, string? powerStateFile = null, Func<int>? foregroundPid = null)
    {
        this.settings = settings;
        power = new PowerPlanManager(powerApi, powerStateFile);
        this.foregroundPid = foregroundPid ?? Native.GetForegroundPid;
    }

    /// <summary>Levé sur le thread du moteur après chaque passage.</summary>
    public event Action<EngineSnapshot>? SnapshotReady;

    public void Start()
    {
        lock (sync)
        {
            if (stopped || timer != null)
                return;
            timer = new System.Threading.Timer(_ => OnTimer(), null, 0, Timeout.Infinite);
            Log.Info("Moteur démarré");
        }
    }

    /// <summary>Remplace la configuration ; tout est restauré puis réappliqué au prochain passage.</summary>
    public void UpdateSettings(Settings newSettings)
    {
        lock (sync)
        {
            settings = newSettings;
            reapply = true;
        }
    }

    public void SetPaused(bool value)
    {
        lock (sync)
        {
            if (paused == value)
                return;
            paused = value;
            reapply = true;
            Log.Info(value ? "Pause" : "Reprise");
        }
    }

    /// <summary>Arrête le moteur et restaure priorités, affinités et plan d'alimentation. Idempotent.</summary>
    public void Stop()
    {
        lock (sync)
        {
            if (stopped)
                return;
            stopped = true;
            timer?.Dispose();
            timer = null;
            try { RestoreAll(); }
            catch (Exception ex) { Log.Error("Restauration à l'arrêt", ex); }
            Log.Info("Moteur arrêté");
        }
    }

    public void Dispose() => Stop();

    /// <summary>Un passage synchrone (utilisé par les tests).</summary>
    public EngineSnapshot TickOnce()
    {
        lock (sync)
            return Tick();
    }

    void OnTimer()
    {
        EngineSnapshot? snapshot = null;
        lock (sync)
        {
            if (stopped)
                return;
            try { snapshot = Tick(); }
            catch (Exception ex) { Log.Error("Erreur moteur", ex); }
            finally { timer?.Change(settings.PollIntervalMs, Timeout.Infinite); }
        }
        if (snapshot != null)
        {
            try { SnapshotReady?.Invoke(snapshot); }
            catch (Exception ex) { Log.Error("Affichage", ex); }
        }
    }

    EngineSnapshot Tick()
    {
        var s = settings;
        if (reapply)
        {
            reapply = false;
            RestoreAll();
            tracked.Clear();
        }

        double elapsedMs = clock.IsRunning ? clock.Elapsed.TotalMilliseconds : 0;
        clock.Restart();
        double sysCpu = systemCpu.Sample();
        int fg = foregroundPid();
        bool pbActive = s.ProBalance.Enabled && !paused;
        var userExclusions = new HashSet<string>(s.ProBalance.Exclusions.Select(RuleMatcher.Normalize), StringComparer.OrdinalIgnoreCase);

        var procs = Process.GetProcesses();
        var live = new Dictionary<ProcKey, Process>(procs.Length);
        var rows = new List<ProcessRow>(procs.Length);
        var samples = new List<ProBalanceLogic.Sample>();
        try
        {
            foreach (var p in procs)
            {
                ProcKey key;
                try { key = new ProcKey(p.Id, p.ProcessName, StartTicks(p)); }
                catch { continue; } // processus terminé entre-temps
                if (!live.TryAdd(key, p))
                    continue;

                if (!tracked.TryGetValue(key, out var t))
                {
                    t = new Tracked(key.Name);
                    tracked[key] = t;
                    if (!paused)
                        ApplyRule(p, key, t, s);
                }

                double cpu = SampleCpu(p, t, elapsedMs);
                if (pbActive)
                {
                    bool eligible = !Exclusions.IsProtected(key.Name, key.Pid, ownPid)
                                    && key.Pid != fg
                                    && t.Rule?.Priority == null
                                    && !userExclusions.Contains(key.Name);
                    // On ne lit la priorité (appel système) que pour les candidats.
                    if (eligible && !proBalance.IsRestrained(key) && cpu >= s.ProBalance.ProcessThreshold)
                        eligible = HasNormalPriority(p);
                    samples.Add(new ProBalanceLogic.Sample(key, cpu, eligible));
                }

                long mem = 0;
                try { mem = p.WorkingSet64; } catch { }
                rows.Add(new ProcessRow(key.Pid, key.Name, cpu, mem, t.Rule?.Pattern, t.ProBalanceOrig != null));
            }

            foreach (var gone in tracked.Keys.Where(k => !live.ContainsKey(k)).ToList())
            {
                power.OnExit(gone);
                tracked.Remove(gone);
                jobLimited.Remove(gone);
            }

            if (pbActive)
                RunProBalance(sysCpu, samples, live, s.ProBalance);
            else
                proBalance.Reset();
        }
        finally
        {
            foreach (var p in procs)
                p.Dispose();
        }

        return new EngineSnapshot(sysCpu, paused, rows);
    }

    void ApplyRule(Process p, ProcKey key, Tracked t, Settings s)
    {
        if (Exclusions.IsProtected(key.Name, key.Pid, ownPid))
            return;
        var rule = RuleMatcher.Find(s.Rules, key.Name);
        t.Rule = rule;
        if (rule == null)
            return;

        var done = new List<string>();
        if (rule.Priority is { } priority)
        {
            if (priority == ProcessPriorityClass.RealTime)
                priority = ProcessPriorityClass.High; // temps réel peut bloquer le système
            if (TryDo(key, "priorité", () =>
                {
                    var current = p.PriorityClass;
                    p.PriorityClass = priority;
                    t.OrigPriority ??= current;
                }))
                done.Add($"priorité {priority}");
        }

        if (rule.AffinityMask is { } mask)
        {
            if (!RuleMatcher.IsValidAffinity(mask, cores))
                Log.Warn($"Règle « {rule.Pattern} » : masque d'affinité 0x{mask:X} invalide pour {cores} CPU");
            else if (TryDo(key, "affinité", () =>
                     {
                         var current = p.ProcessorAffinity;
                         p.ProcessorAffinity = (IntPtr)mask;
                         t.OrigAffinity ??= current;
                     }))
                done.Add($"affinité 0x{mask:X}");
        }

        if ((rule.CpuLimitPercent is > 0 || rule.MemoryLimitMB is > 0) && !jobLimited.Contains(key))
        {
            if (TryDo(key, "limites", () => JobLimiter.Apply(p, rule.CpuLimitPercent, rule.MemoryLimitMB)))
            {
                jobLimited.Add(key);
                done.Add($"limites CPU {rule.CpuLimitPercent?.ToString() ?? "-"}% / RAM {rule.MemoryLimitMB?.ToString() ?? "-"} Mo");
            }
        }

        if (rule.PowerPlan is { } plan)
        {
            power.OnStart(key, plan);
            done.Add("plan d'alimentation");
        }

        if (done.Count > 0)
            Log.Info($"{key.Name} ({key.Pid}) : règle « {rule.Pattern} » → {string.Join(", ", done)}");
    }

    void RunProBalance(double sysCpu, List<ProBalanceLogic.Sample> samples, Dictionary<ProcKey, Process> live, ProBalanceSettings cfg)
    {
        var toRestrain = new List<ProcKey>();
        var toRestore = new List<ProcKey>();
        proBalance.Evaluate(DateTime.UtcNow, sysCpu, samples, cfg, toRestrain, toRestore);

        foreach (var key in toRestrain)
        {
            if (!live.TryGetValue(key, out var p) || !tracked.TryGetValue(key, out var t) || t.ProBalanceOrig != null)
                continue;
            if (TryDo(key, "ProBalance", () =>
                {
                    var current = p.PriorityClass;
                    p.PriorityClass = ProcessPriorityClass.BelowNormal;
                    t.ProBalanceOrig = current;
                }))
                Log.Info($"ProBalance : {key.Name} ({key.Pid}) abaissé (CPU système {sysCpu:0}%)");
        }

        foreach (var key in toRestore)
        {
            if (!tracked.TryGetValue(key, out var t) || t.ProBalanceOrig is not { } orig)
                continue;
            t.ProBalanceOrig = null;
            if (live.TryGetValue(key, out var p) && TryDo(key, "ProBalance restauration", () => p.PriorityClass = orig))
                Log.Info($"ProBalance : {key.Name} ({key.Pid}) restauré en {orig}");
        }
    }

    void RestoreAll()
    {
        foreach (var (key, t) in tracked)
        {
            var priority = t.OrigPriority ?? t.ProBalanceOrig;
            var affinity = t.OrigAffinity;
            t.OrigPriority = null;
            t.OrigAffinity = null;
            t.ProBalanceOrig = null;
            if (priority == null && affinity == null)
                continue;
            using var p = OpenSame(key);
            if (p == null)
                continue;
            if (priority is { } pr)
                TryDo(key, "restauration priorité", () => p.PriorityClass = pr);
            if (affinity is { } af)
                TryDo(key, "restauration affinité", () => p.ProcessorAffinity = af);
        }
        power.RestoreAll();
        proBalance.Reset();
    }

    double SampleCpu(Process p, Tracked t, double elapsedMs)
    {
        if (t.CpuDenied)
            return 0;
        TimeSpan current;
        try { current = p.TotalProcessorTime; }
        catch
        {
            t.CpuDenied = true;
            return 0;
        }
        double pct = 0;
        if (t.LastCpu is { } last && elapsedMs > 0)
            pct = Math.Clamp((current - last).TotalMilliseconds / (elapsedMs * cores) * 100, 0, 100);
        t.LastCpu = current;
        return pct;
    }

    static bool HasNormalPriority(Process p)
    {
        try { return p.PriorityClass is ProcessPriorityClass.Normal or ProcessPriorityClass.AboveNormal; }
        catch { return false; }
    }

    static long StartTicks(Process p)
    {
        try { return p.StartTime.Ticks; }
        catch { return 0; } // processus protégé : heure inaccessible
    }

    /// <summary>Rouvre le processus seulement s'il s'agit toujours du même (PID non réutilisé).</summary>
    static Process? OpenSame(ProcKey key)
    {
        Process? p = null;
        try
        {
            p = Process.GetProcessById(key.Pid);
            if (string.Equals(p.ProcessName, key.Name, StringComparison.OrdinalIgnoreCase) && StartTicks(p) == key.StartTicks)
                return p;
        }
        catch { }
        p?.Dispose();
        return null;
    }

    static bool TryDo(ProcKey key, string what, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"{key.Name} ({key.Pid}) : échec {what} : {ex.Message}");
            return false;
        }
    }
}
