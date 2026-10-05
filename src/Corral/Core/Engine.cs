using System.Diagnostics;
using Corral.Models;

namespace Corral.Core;

/// <param name="RuleSummary">Effet de la règle en clair (« Priorité haute · 8 cœurs »), pour l'infobulle.</param>
public sealed record ProcessRow(int Pid, string Name, double Cpu, long MemoryBytes, string? Rule, bool Restrained,
    string? Path = null, string? RuleSummary = null);

public sealed record EngineSnapshot(double SystemCpu, bool Paused, IReadOnlyList<ProcessRow> Rows,
    bool GameMode = false, string? GameTrigger = null);

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
        public string? Path;
        public bool PathLoaded;
        public IoPriorityLevel? OrigIo;
        public MemoryPriorityLevel? OrigMemory;
        public bool EfficiencySet;
        public DateTime? AlertSince;
        public bool AlertFired;
    }

    /// <summary>Clé fictive pour la demande de plan d'alimentation du Mode Jeu.</summary>
    static readonly ProcKey GameKey = new(-1, "__mode_jeu", 0);
    public const string GameModeRuleName = "Mode Jeu";

    readonly object sync = new();
    readonly PowerPlanManager power;
    readonly ProBalanceLogic proBalance = new();
    readonly SystemCpuSampler systemCpu = new();
    readonly Func<int> foregroundPid;
    readonly Dictionary<ProcKey, Tracked> tracked = new();
    readonly HashSet<ProcKey> jobLimited = new();
    readonly List<string> pendingActed = new();
    readonly List<(string Title, string Message)> pendingNotifications = new();
    readonly KeepAwake keepAwake = new();
    readonly Func<DateTime> now;
    readonly Stopwatch clock = new();
    readonly int ownPid = Environment.ProcessId;
    readonly int cores = Environment.ProcessorCount;
    Settings settings;
    bool reapply;
    bool paused;
    bool stopped;
    bool gameManual;
    bool gameActive;
    string? gameTrigger;
    bool gamePlanApplied;
    (bool Active, string? Trigger)? pendingGameEvent;
    System.Threading.Timer? timer;

    /// <param name="clock">Horloge (les tests de surveillance l'avancent à la main).</param>
    public Engine(Settings settings, IPowerPlanApi powerApi, string? powerStateFile = null, Func<int>? foregroundPid = null, Func<DateTime>? clock = null)
    {
        this.settings = settings;
        power = new PowerPlanManager(powerApi, powerStateFile);
        this.foregroundPid = foregroundPid ?? Native.GetForegroundPid;
        now = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Message à afficher à l'utilisateur (programme bloqué, alerte de surveillance). Levé hors verrou.</summary>
    public event Action<string, string>? Notification;

    /// <summary>Raison de la demande « empêcher la veille » en cours, ou null.</summary>
    public string? KeepAwakeReason { get; private set; }

    /// <summary>Levé sur le thread du moteur après chaque passage.</summary>
    public event Action<EngineSnapshot>? SnapshotReady;

    /// <summary>ProBalance vient d'abaisser des programmes (noms, CPU système). Levé sur le thread du moteur.</summary>
    public event Action<IReadOnlyList<string>, double>? ProBalanceActed;

    /// <summary>Le Mode Jeu vient de s'activer ou de se désactiver (jeu déclencheur, ou null si manuel).</summary>
    public event Action<bool, string?>? GameModeChanged;

    /// <summary>Active ou désactive le Mode Jeu manuellement (en plus du déclenchement automatique).</summary>
    public void SetGameMode(bool enabled)
    {
        lock (sync)
        {
            gameManual = enabled;
            Log.Info(enabled ? "Mode Jeu activé manuellement" : "Mode Jeu manuel désactivé", LogCategory.Rule);
        }
    }

    public bool GameModeManual
    {
        get { lock (sync) return gameManual; }
    }

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
            keepAwake.Dispose();
            KeepAwakeReason = null;
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
        List<string>? acted = null;
        (bool Active, string? Trigger)? game;
        List<(string Title, string Message)> notifications;
        lock (sync)
        {
            if (stopped)
                return;
            try { snapshot = Tick(); }
            catch (Exception ex) { Log.Error("Erreur moteur", ex); }
            finally { timer?.Change(settings.PollIntervalMs, Timeout.Infinite); }
            if (pendingActed.Count > 0)
            {
                acted = new List<string>(pendingActed);
                pendingActed.Clear();
            }
            game = pendingGameEvent;
            pendingGameEvent = null;
            notifications = pendingNotifications.ToList();
            pendingNotifications.Clear();
        }
        // Événements levés hors verrou : un abonné lent ne bloque pas le moteur.
        try
        {
            if (snapshot != null)
                SnapshotReady?.Invoke(snapshot);
            if (acted != null && snapshot != null)
                ProBalanceActed?.Invoke(acted, snapshot.SystemCpu);
            if (game is { } g)
                GameModeChanged?.Invoke(g.Active, g.Trigger);
            foreach (var (title, message) in notifications)
                Notification?.Invoke(title, message);
        }
        catch (Exception ex) { Log.Error("Affichage", ex); }
    }

    /// <summary>
    /// Change la priorité d'un processus une seule fois, sans règle (menu « Priorité maintenant »).
    /// Renvoie un message d'erreur, ou null si c'est fait.
    /// </summary>
    public string? SetPriorityOnce(int pid, string name, ProcessPriorityClass priority)
    {
        lock (sync)
        {
            if (Exclusions.IsProtected(name, pid, ownPid))
                return "Processus système : Corral n'y touche pas.";
            var key = tracked.Keys.FirstOrDefault(k => k.Pid == pid && string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));
            using var p = key.Name == null ? null : OpenSame(key);
            if (p == null)
                return "Le processus n'existe plus.";
            if (priority == ProcessPriorityClass.RealTime)
                priority = ProcessPriorityClass.High;
            try
            {
                p.PriorityClass = priority;
            }
            catch (Exception ex)
            {
                return "Accès refusé : " + ex.Message;
            }
            // ProBalance ne doit pas « restaurer » par-dessus le choix de l'utilisateur.
            if (tracked.TryGetValue(key, out var t))
                t.ProBalanceOrig = null;
            Log.Info($"{name} ({pid}) : priorité changée en {PriorityLabel(priority)} (ponctuel)", LogCategory.Rule);
            return null;
        }
    }

    public static string PriorityLabel(ProcessPriorityClass p) => p switch
    {
        ProcessPriorityClass.Idle => "Inactive",
        ProcessPriorityClass.BelowNormal => "Inférieure à la normale",
        ProcessPriorityClass.Normal => "Normale",
        ProcessPriorityClass.AboveNormal => "Supérieure à la normale",
        ProcessPriorityClass.High => "Haute",
        _ => "Temps réel",
    };

    public static string IoLabel(IoPriorityLevel l) => l switch
    {
        IoPriorityLevel.VeryLow => "Très basse",
        IoPriorityLevel.Low => "Basse",
        _ => "Normale",
    };

    public static string MemoryLabel(MemoryPriorityLevel l) => l switch
    {
        MemoryPriorityLevel.VeryLow => "Très basse",
        MemoryPriorityLevel.Low => "Basse",
        MemoryPriorityLevel.Medium => "Moyenne",
        MemoryPriorityLevel.BelowNormal => "Inférieure à la normale",
        _ => "Normale",
    };

    /// <summary>Effet d'une règle en une ligne, pour les infobulles.</summary>
    public static string? Summary(Rule? r)
    {
        if (r == null)
            return null;
        var parts = new List<string>();
        if (r.Priority is { } p) parts.Add("priorité " + PriorityLabel(p).ToLowerInvariant());
        if (r.AffinityMask is { } m) parts.Add($"{System.Numerics.BitOperations.PopCount((ulong)m)} cœurs");
        if (r.PowerPlan != null) parts.Add("plan d'alimentation");
        if (r.CpuLimitPercent is { } c) parts.Add($"CPU max {c} %");
        if (r.MemoryLimitMB is { } mem) parts.Add($"RAM max {mem} Mo");
        if (r.EfficiencyMode == true) parts.Add("mode efficacité");
        if (r.EfficiencyMode == false) parts.Add("jamais en mode efficacité");
        if (r.IoPriority is { } io) parts.Add("disque " + IoLabel(io).ToLowerInvariant());
        if (r.MemoryPriority is { } mp) parts.Add("mémoire " + MemoryLabel(mp).ToLowerInvariant());
        if (r.IsGame) parts.Add("jeu (Mode Jeu)");
        if (r.KeepAwake) parts.Add("empêche la veille");
        if (r.Block == BlockMode.Always) parts.Add("bloqué");
        if (r.Block == BlockMode.SingleInstance) parts.Add("une seule instance");
        if (r.HasAlert) parts.Add("surveillé");
        return parts.Count == 0 ? "aucun effet" : string.Join(" · ", parts);
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
        var pb = EffectiveProBalance(s);
        bool pbActive = pb.Enabled && !paused;
        var userExclusions = pb.Exclusions;

        var procs = Process.GetProcesses();
        var live = new Dictionary<ProcKey, Process>(procs.Length);
        var rows = new List<ProcessRow>(procs.Length);
        var samples = new List<ProBalanceLogic.Sample>();
        var cpuByKey = new Dictionary<ProcKey, double>();
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
                if (t.Rule?.HasAlert == true)
                    cpuByKey[key] = cpu;
                if (pbActive)
                {
                    bool eligible = !Exclusions.IsProtected(key.Name, key.Pid, ownPid)
                                    && key.Pid != fg
                                    && t.Rule?.Priority == null
                                    && !userExclusions.Any(x => RuleMatcher.Matches(x, key.Name)); // jokers acceptés
                    // On ne lit la priorité (appel système) que pour les candidats.
                    if (eligible && !proBalance.IsRestrained(key) && cpu >= pb.ProcessThreshold)
                        eligible = HasNormalPriority(p);
                    samples.Add(new ProBalanceLogic.Sample(key, cpu, eligible));
                }

                long mem = 0;
                try { mem = p.WorkingSet64; } catch { }
                if (!t.PathLoaded)
                {
                    t.Path = Native.GetProcessPath(key.Pid);
                    t.PathLoaded = true;
                }
                rows.Add(new ProcessRow(key.Pid, key.Name, cpu, mem, t.Rule?.Pattern, t.ProBalanceOrig != null, t.Path, Summary(t.Rule)));
            }

            foreach (var gone in tracked.Keys.Where(k => !live.ContainsKey(k)).ToList())
            {
                power.OnExit(gone);
                tracked.Remove(gone);
                jobLimited.Remove(gone);
            }

            if (pbActive)
                RunProBalance(sysCpu, samples, live, pb);
            else
                proBalance.Reset();

            UpdateGameMode(s);
            if (!paused)
            {
                EnforceSingleInstance(live);
                RunAlerts(live, cpuByKey);
            }
            UpdateKeepAwake();
        }
        finally
        {
            foreach (var p in procs)
                p.Dispose();
        }

        return new EngineSnapshot(sysCpu, paused, rows, gameActive, gameTrigger);
    }

    /// <summary>ProBalance tel qu'appliqué : les seuils « Réactif » remplacent les réglages pendant le Mode Jeu.</summary>
    ProBalanceSettings EffectiveProBalance(Settings s)
    {
        if (!gameActive || !s.GameMode.ReactiveProBalance)
            return s.ProBalance;
        var pb = new ProBalanceSettings { Enabled = s.ProBalance.Enabled, Exclusions = s.ProBalance.Exclusions };
        pb.ApplyPreset(ProBalanceSettings.Presets[^1]);
        return pb;
    }

    /// <summary>
    /// Le Mode Jeu est actif s'il est demandé manuellement, ou (option automatique) si un programme
    /// dont la règle est marquée « jeu » tourne. Tout changement d'état déclenche une réapplication
    /// complète au passage suivant (programmes de fond abaissés ou restaurés).
    /// </summary>
    void UpdateGameMode(Settings s)
    {
        string? trigger = null;
        if (!paused && s.GameMode.Automatic)
            trigger = tracked.FirstOrDefault(kv => kv.Value.Rule?.IsGame == true).Value?.Name;
        bool want = !paused && (gameManual || trigger != null);

        if (want != gameActive)
        {
            gameActive = want;
            gameTrigger = want ? trigger : null;
            pendingGameEvent = (want, gameTrigger);
            reapply = true;
            Log.Info(want ? $"Mode Jeu activé{(trigger != null ? $" ({trigger})" : "")}" : "Mode Jeu désactivé", LogCategory.Rule);
            return;
        }
        if (gameActive && !gamePlanApplied && s.GameMode.PowerPlan is { } plan && plan != Guid.Empty)
        {
            power.OnStart(GameKey, plan);
            gamePlanApplied = true;
        }
    }

    /// <summary>« Une seule instance » : on garde la plus ancienne de chaque exécutable, les autres sont fermées.</summary>
    void EnforceSingleInstance(Dictionary<ProcKey, Process> live)
    {
        var groups = tracked
            .Where(kv => kv.Value.Rule?.Block == BlockMode.SingleInstance && live.ContainsKey(kv.Key))
            .GroupBy(kv => (kv.Value.Rule, Name: kv.Key.Name.ToLowerInvariant()))
            .Where(g => g.Count() > 1);
        foreach (var group in groups)
        {
            foreach (var (key, _) in group.OrderBy(kv => kv.Key.StartTicks).Skip(1))
            {
                if (TryDo(key, "fermeture (une seule instance)", () => live[key].Kill()))
                {
                    Log.Info($"{key.Name} ({key.Pid}) fermé : une seule instance autorisée", LogCategory.Rule);
                    pendingNotifications.Add(("Programme bloqué", $"Une seule instance de « {key.Name} » est autorisée : la nouvelle a été fermée."));
                }
            }
        }
    }

    /// <summary>
    /// Surveillance : si un programme dépasse son seuil CPU ou mémoire pendant la durée prévue,
    /// on prévient, on abaisse sa priorité ou on le ferme — une fois, jusqu'à ce qu'il repasse sous le seuil.
    /// </summary>
    void RunAlerts(Dictionary<ProcKey, Process> live, Dictionary<ProcKey, double> cpuByKey)
    {
        var t0 = now();
        foreach (var (key, cpu) in cpuByKey)
        {
            if (!tracked.TryGetValue(key, out var t) || t.Rule is not { HasAlert: true } rule || !live.TryGetValue(key, out var p))
                continue;
            long privateBytes = 0;
            try { privateBytes = p.PrivateMemorySize64; } catch { }
            string? reason = null;
            if (rule.AlertCpuPercent is > 0 and var c && cpu >= c)
                reason = $"{cpu:0} % du processeur";
            else if (rule.AlertMemoryMB is > 0 and var m && privateBytes >= (long)m * 1024 * 1024)
                reason = $"{privateBytes / (1024 * 1024):N0} Mo de mémoire";

            if (reason == null)
            {
                t.AlertSince = null;
                t.AlertFired = false;
                continue;
            }
            t.AlertSince ??= t0;
            if (t.AlertFired || t0 - t.AlertSince.Value < TimeSpan.FromMinutes(rule.AlertMinutes))
                continue;
            t.AlertFired = true;

            var duration = $"depuis {rule.AlertMinutes} min";
            switch (rule.AlertAction)
            {
                case AlertAction.Lower:
                    if (TryDo(key, "surveillance : abaissement", () =>
                        {
                            var current = p.PriorityClass;
                            p.PriorityClass = ProcessPriorityClass.Idle;
                            t.OrigPriority ??= current;
                        }))
                        Notify(key, $"« {key.Name} » utilise {reason} {duration} : sa priorité a été baissée.");
                    break;
                case AlertAction.Close:
                    if (TryDo(key, "surveillance : fermeture", () => p.Kill()))
                        Notify(key, $"« {key.Name} » utilisait {reason} {duration} : il a été fermé.");
                    break;
                default:
                    Notify(key, $"« {key.Name} » utilise {reason} {duration}.");
                    break;
            }
        }

        void Notify(ProcKey key, string message)
        {
            Log.Warn($"Surveillance : {message} (PID {key.Pid})", LogCategory.Rule);
            pendingNotifications.Add(("Surveillance", message));
        }
    }

    /// <summary>Demande « pas de mise en veille » tant qu'un programme dont la règle l'exige tourne.</summary>
    void UpdateKeepAwake()
    {
        string? reason = null;
        if (!paused)
        {
            var names = tracked.Values.Where(t => t.Rule?.KeepAwake == true).Select(t => t.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count > 0)
                reason = $"Corral : {string.Join(", ", names.Take(3))} en cours";
        }
        if (reason == KeepAwakeReason)
            return;
        try
        {
            keepAwake.Set(reason);
            if ((reason == null) != (KeepAwakeReason == null))
                Log.Info(reason == null ? "Mise en veille de nouveau autorisée" : $"Mise en veille empêchée ({reason})", LogCategory.Power);
            KeepAwakeReason = reason;
        }
        catch (Exception ex)
        {
            Log.Error("Empêcher la mise en veille", ex);
        }
    }

    /// <summary>Motif qui viserait tous les programmes : jamais de blocage avec (sécurité).</summary>
    public static bool IsCatchAll(string pattern) =>
        RuleMatcher.Normalize(pattern).Trim('*', '?').Length == 0;

    static bool IsBackgroundApp(Settings s, string name) =>
        s.GameMode.BackgroundApps.Any(x => RuleMatcher.Matches(x, name));

    void ApplyRule(Process p, ProcKey key, Tracked t, Settings s)
    {
        if (Exclusions.IsProtected(key.Name, key.Pid, ownPid))
            return;
        var rule = RuleMatcher.Find(s.Rules, key.Name);
        // Mode Jeu : les programmes de fond sans règle propre passent en priorité basse + mode efficacité
        if (rule == null && gameActive && s.GameMode.LowerBackground && IsBackgroundApp(s, key.Name))
            rule = new Rule { Pattern = GameModeRuleName, Priority = ProcessPriorityClass.BelowNormal, EfficiencyMode = true };
        t.Rule = rule;
        if (rule == null)
            return;

        if (rule.Block == BlockMode.Always && !IsCatchAll(rule.Pattern))
        {
            if (TryDo(key, "blocage", () => p.Kill()))
            {
                Log.Info($"{key.Name} ({key.Pid}) bloqué par la règle « {rule.Pattern} »", LogCategory.Rule);
                pendingNotifications.Add(("Programme bloqué", $"« {key.Name} » a été fermé : la règle « {rule.Pattern} » l'interdit."));
            }
            return;
        }

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
                Log.Warn($"Règle « {rule.Pattern} » : masque d'affinité 0x{mask:X} invalide pour {cores} CPU", LogCategory.Rule);
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

        if (rule.EfficiencyMode is { } eco && TryDo(key, "mode efficacité", () =>
            {
                ProcessTweaks.SetEfficiencyMode(p.Handle, eco);
                t.EfficiencySet = true;
            }))
            done.Add(eco ? "mode efficacité" : "mode efficacité interdit");

        if (rule.IoPriority is { } io && TryDo(key, "priorité disque", () =>
            {
                var current = ProcessTweaks.GetIoPriority(p.Handle);
                ProcessTweaks.SetIoPriority(p.Handle, io);
                t.OrigIo ??= current;
            }))
            done.Add($"disque {io}");

        if (rule.MemoryPriority is { } memPrio && TryDo(key, "priorité mémoire", () =>
            {
                var current = ProcessTweaks.GetMemoryPriority(p.Handle);
                ProcessTweaks.SetMemoryPriority(p.Handle, memPrio);
                t.OrigMemory ??= current;
            }))
            done.Add($"mémoire {memPrio}");

        if (done.Count > 0)
            Log.Info($"{key.Name} ({key.Pid}) : règle « {rule.Pattern} » → {string.Join(", ", done)}", LogCategory.Rule);
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
            {
                Log.Info($"ProBalance : {key.Name} ({key.Pid}) abaissé (CPU système {sysCpu:0}%)", LogCategory.ProBalance);
                pendingActed.Add(key.Name);
            }
        }

        foreach (var key in toRestore)
        {
            if (!tracked.TryGetValue(key, out var t) || t.ProBalanceOrig is not { } orig)
                continue;
            t.ProBalanceOrig = null;
            if (live.TryGetValue(key, out var p) && TryDo(key, "ProBalance restauration", () => p.PriorityClass = orig))
                Log.Info($"ProBalance : {key.Name} ({key.Pid}) restauré en {orig}", LogCategory.ProBalance);
        }
    }

    void RestoreAll()
    {
        foreach (var (key, t) in tracked)
        {
            var priority = t.OrigPriority ?? t.ProBalanceOrig;
            var affinity = t.OrigAffinity;
            var io = t.OrigIo;
            var memory = t.OrigMemory;
            bool efficiency = t.EfficiencySet;
            (t.OrigPriority, t.OrigAffinity, t.ProBalanceOrig, t.OrigIo, t.OrigMemory, t.EfficiencySet) = (null, null, null, null, null, false);
            if (priority == null && affinity == null && io == null && memory == null && !efficiency)
                continue;
            using var p = OpenSame(key);
            if (p == null)
                continue;
            if (priority is { } pr)
                TryDo(key, "restauration priorité", () => p.PriorityClass = pr);
            if (affinity is { } af)
                TryDo(key, "restauration affinité", () => p.ProcessorAffinity = af);
            if (io is { } i)
                TryDo(key, "restauration priorité disque", () => ProcessTweaks.SetIoPriority(p.Handle, i));
            if (memory is { } m)
                TryDo(key, "restauration priorité mémoire", () => ProcessTweaks.SetMemoryPriority(p.Handle, m));
            if (efficiency)
                TryDo(key, "restauration mode efficacité", () => ProcessTweaks.SetEfficiencyMode(p.Handle, null));
        }
        power.RestoreAll();
        gamePlanApplied = false;
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
