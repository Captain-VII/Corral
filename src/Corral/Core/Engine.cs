using System.Diagnostics;
using Corral.Models;

namespace Corral.Core;

/// <param name="RuleSummary">Effet de la règle en clair (« Priorité haute · 8 cœurs »), pour l'infobulle.</param>
public sealed record ProcessRow(int Pid, string Name, double Cpu, long MemoryBytes, string? Rule, bool Restrained,
    string? Path = null, string? RuleSummary = null, long IoBytesPerSec = 0, double Gpu = 0);

public sealed record EngineSnapshot(double SystemCpu, bool Paused, IReadOnlyList<ProcessRow> Rows,
    bool GameMode = false, string? GameTrigger = null, long MemoryUsed = 0, long MemoryTotal = 0)
{
    /// <summary>Mémoire physique utilisée, en % (0 si inconnue).</summary>
    public double MemoryPercent => MemoryTotal > 0 ? MemoryUsed * 100.0 / MemoryTotal : 0;
}

/// <summary>
/// Moteur : un seul thread (timer non réentrant) sous verrou. À chaque passage il liste les processus,
/// applique les règles aux nouveaux, fait tourner ProBalance et publie un instantané pour l'interface.
/// Toute modification est mémorisée pour être annulée à l'arrêt, en pause ou au changement de règles.
/// </summary>
public sealed class Engine : IEngine, IDisposable
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
        public ulong? LastIo;
        public bool IoDenied;
        public bool CpuDenied;
        public string? Path;
        public bool PathLoaded;
        public IoPriorityLevel? OrigIo;
        public MemoryPriorityLevel? OrigMemory;
        public bool EfficiencySet;
        /// <summary>Priorité d'origine si le boost du premier plan l'a relevée.</summary>
        public ProcessPriorityClass? BoostOrig;
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
            Log.Info(enabled ? Tr("Mode Jeu activé manuellement", "Game Mode turned on manually") : Tr("Mode Jeu manuel désactivé", "Manual Game Mode turned off"), LogCategory.Rule);
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
            Log.Info(Tr("Moteur démarré", "Engine started"));
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
            catch (Exception ex) { Log.Error(Tr("Restauration à l'arrêt", "Restore on stop"), ex); }
            Log.Info(Tr("Moteur arrêté", "Engine stopped"));
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
            catch (Exception ex) { Log.Error(Tr("Erreur moteur", "Engine error"), ex); }
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
                return Tr("Processus système : Corral n'y touche pas.", "System process: Corral does not touch it.");
            var key = tracked.Keys.FirstOrDefault(k => k.Pid == pid && string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));
            using var p = key.Name == null ? null : OpenSame(key);
            if (p == null)
                return Tr("Le processus n'existe plus.", "The process no longer exists.");
            if (priority == ProcessPriorityClass.RealTime)
                priority = ProcessPriorityClass.High;
            try
            {
                p.PriorityClass = priority;
            }
            catch (Exception ex)
            {
                return Tr("Accès refusé : ", "Access denied: ") + ex.Message;
            }
            // ProBalance ne doit pas « restaurer » par-dessus le choix de l'utilisateur.
            if (tracked.TryGetValue(key, out var t))
                t.ProBalanceOrig = null;
            Log.Info(Tr($"{name} ({pid}) : priorité changée en {PriorityLabel(priority)} (ponctuel)", $"{name} ({pid}): priority changed to {PriorityLabel(priority)} (one-off)"), LogCategory.Rule);
            return null;
        }
    }

    public static string PriorityLabel(ProcessPriorityClass p) => p switch
    {
        ProcessPriorityClass.Idle => Tr("Inactive", "Idle"),
        ProcessPriorityClass.BelowNormal => Tr("Inférieure à la normale", "Below normal"),
        ProcessPriorityClass.Normal => Tr("Normale", "Normal"),
        ProcessPriorityClass.AboveNormal => Tr("Supérieure à la normale", "Above normal"),
        ProcessPriorityClass.High => Tr("Haute", "High"),
        _ => Tr("Temps réel", "Realtime"),
    };

    public static string IoLabel(IoPriorityLevel l) => l switch
    {
        IoPriorityLevel.VeryLow => Tr("Très basse", "Very low"),
        IoPriorityLevel.Low => Tr("Basse", "Low"),
        _ => Tr("Normale", "Normal"),
    };

    public static string MemoryLabel(MemoryPriorityLevel l) => l switch
    {
        MemoryPriorityLevel.VeryLow => Tr("Très basse", "Very low"),
        MemoryPriorityLevel.Low => Tr("Basse", "Low"),
        MemoryPriorityLevel.Medium => Tr("Moyenne", "Medium"),
        MemoryPriorityLevel.BelowNormal => Tr("Inférieure à la normale", "Below normal"),
        _ => Tr("Normale", "Normal"),
    };

    /// <summary>Effet d'une règle en une ligne, pour les infobulles.</summary>
    public static string? Summary(Rule? r)
    {
        if (r == null)
            return null;
        var parts = new List<string>();
        if (r.Priority is { } p) parts.Add(Tr("priorité ", "priority ") + PriorityLabel(p).ToLowerInvariant());
        if (r.AffinityMask is { } m) parts.Add(Tr($"{System.Numerics.BitOperations.PopCount((ulong)m)} cœurs", $"{System.Numerics.BitOperations.PopCount((ulong)m)} cores"));
        if (r.PowerPlan != null) parts.Add(Tr("plan d'alimentation", "power plan"));
        if (r.CpuLimitPercent is { } c) parts.Add($"CPU max {c} %");
        if (r.MemoryLimitMB is { } mem) parts.Add(Tr($"RAM max {mem} Mo", $"RAM max {mem} MB"));
        if (r.EfficiencyMode == true) parts.Add(Tr("mode efficacité", "efficiency mode"));
        if (r.EfficiencyMode == false) parts.Add(Tr("jamais en mode efficacité", "never in efficiency mode"));
        if (r.IoPriority is { } io) parts.Add(Tr("disque ", "disk ") + IoLabel(io).ToLowerInvariant());
        if (r.MemoryPriority is { } mp) parts.Add(Tr("mémoire ", "memory ") + MemoryLabel(mp).ToLowerInvariant());
        if (r.IsGame) parts.Add(Tr("jeu (Mode Jeu)", "game (Game Mode)"));
        if (r.GpuPreference is { } gpu) parts.Add(Tr("carte graphique ", "graphics card ") + GpuPreferences.Label(gpu).ToLowerInvariant());
        if (r.KeepAwake) parts.Add(Tr("empêche la veille", "prevents sleep"));
        if (r.Block == BlockMode.Always) parts.Add(Tr("bloqué", "blocked"));
        if (r.Block == BlockMode.SingleInstance) parts.Add(Tr("une seule instance", "single instance"));
        if (r.HasAlert) parts.Add(Tr("surveillé", "monitored"));
        return parts.Count == 0 ? Tr("aucun effet", "no effect") : string.Join(" · ", parts);
    }

    EngineSnapshot Tick()
    {
        var s = settings;
        if (reapply)
        {
            reapply = false;
            RestoreAll();
            tracked.Clear();
            CleanUpGpuPreferences(s);
        }

        double elapsedMs = clock.IsRunning ? clock.Elapsed.TotalMilliseconds : 0;
        clock.Restart();
        double sysCpu = systemCpu.Sample();
        int fg = foregroundPid();
        var pb = EffectiveProBalance(s);
        bool pbActive = pb.Enabled && !paused;
        var userExclusions = pb.Exclusions;

        IReadOnlyDictionary<int, double> gpu = NoGpu;
        try { if (GpuUsage != null) gpu = GpuUsage(); }
        catch (Exception ex) { GpuUsage = null; Log.Error(Tr("Usage GPU", "GPU usage"), ex); }

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
                    // Chemin lu avant la règle : la préférence GPU en a besoin
                    t.Path = Native.GetProcessPath(key.Pid);
                    t.PathLoaded = true;
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
                rows.Add(new ProcessRow(key.Pid, key.Name, cpu, mem, t.Rule?.Pattern, t.ProBalanceOrig != null, t.Path, Summary(t.Rule),
                    SampleIo(key.Pid, t, elapsedMs), gpu.GetValueOrDefault(key.Pid)));
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
            UpdateForegroundBoost(s, live, fg);
            UpdateIdleSaver(s);
            RunMemoryCleanup(s, live, rows);
        }
        finally
        {
            foreach (var p in procs)
                p.Dispose();
        }

        var (memUsed, memTotal) = Native.GetMemoryUsage();
        return new EngineSnapshot(sysCpu, paused, rows, gameActive, gameTrigger, memUsed, memTotal);
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
            Log.Info(want ? Tr("Mode Jeu activé", "Game Mode on") + (trigger != null ? $" ({trigger})" : "") : Tr("Mode Jeu désactivé", "Game Mode off"), LogCategory.Rule);
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
                if (TryDo(key, Tr("fermeture (une seule instance)", "close (single instance)"), () => live[key].Kill()))
                {
                    Log.Info(Tr($"{key.Name} ({key.Pid}) fermé : une seule instance autorisée", $"{key.Name} ({key.Pid}) closed: only one instance allowed"), LogCategory.Rule);
                    pendingNotifications.Add((Tr("Programme bloqué", "Program blocked"), Tr($"Une seule instance de « {key.Name} » est autorisée : la nouvelle a été fermée.", $"Only one instance of “{key.Name}” is allowed: the new one was closed.")));
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
                reason = Tr($"{cpu:0} % du processeur", $"{cpu:0} % of the CPU");
            else if (rule.AlertMemoryMB is > 0 and var m && privateBytes >= (long)m * 1024 * 1024)
                reason = Tr($"{privateBytes / (1024 * 1024):N0} Mo de mémoire", $"{privateBytes / (1024 * 1024):N0} MB of memory");

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

            var duration = Tr($"depuis {rule.AlertMinutes} min", $"for {rule.AlertMinutes} min");
            switch (rule.AlertAction)
            {
                case AlertAction.Lower:
                    if (TryDo(key, Tr("surveillance : abaissement", "monitoring: lowering"), () =>
                        {
                            var current = p.PriorityClass;
                            p.PriorityClass = ProcessPriorityClass.Idle;
                            t.OrigPriority ??= current;
                        }))
                        Notify(key, Tr($"« {key.Name} » utilise {reason} {duration} : sa priorité a été baissée.", $"“{key.Name}” has been using {reason} {duration}: its priority was lowered."));
                    break;
                case AlertAction.Close:
                    if (TryDo(key, Tr("surveillance : fermeture", "monitoring: close"), () => p.Kill()))
                        Notify(key, Tr($"« {key.Name} » utilisait {reason} {duration} : il a été fermé.", $"“{key.Name}” was using {reason} {duration}: it was closed."));
                    break;
                default:
                    Notify(key, Tr($"« {key.Name} » utilise {reason} {duration}.", $"“{key.Name}” has been using {reason} {duration}."));
                    break;
            }
        }

        void Notify(ProcKey key, string message)
        {
            Log.Warn(Tr($"Surveillance : {message} (PID {key.Pid})", $"Monitoring: {message} (PID {key.Pid})"), LogCategory.Rule);
            pendingNotifications.Add(("Surveillance", message));
        }
    }

    // ---------- Optimisations (v1.6) ----------

    static readonly ProcKey IdleKey = new(-2, "__economie_au_repos", 0);
    ProcKey? boosted;
    bool idleActive;
    DateTime lastCleanup = DateTime.MinValue;
    bool cleanupRequested;
    static readonly TimeSpan CleanupCooldown = TimeSpan.FromMinutes(5);

    /// <summary>Temps d'inactivité de l'utilisateur (remplaçable pour les tests).</summary>
    public Func<TimeSpan> IdleTimeSource { get; set; } = Idle.UserIdleTime;

    /// <summary>Une vidéo ou une présentation demande l'écran (remplaçable pour les tests).</summary>
    public Func<bool> DisplayRequiredSource { get; set; } = Idle.DisplayRequired;

    /// <summary>Préférences GPU par programme (null = fonction désactivée, par exemple dans les tests).</summary>
    public GpuPreferences? Gpu { get; set; }

    /// <summary>Usage du GPU par PID, appelé à chaque passage sur le thread du moteur (null = colonne GPU à 0).</summary>
    public Func<IReadOnlyDictionary<int, double>>? GpuUsage { get; set; }

    static readonly IReadOnlyDictionary<int, double> NoGpu = new Dictionary<int, double>();

    public bool IdleSaverActive
    {
        get { lock (sync) return idleActive; }
    }

    /// <summary>Demande un nettoyage mémoire au prochain passage (bouton « Nettoyer maintenant »).</summary>
    public void CleanMemoryNow()
    {
        lock (sync)
            cleanupRequested = true;
    }

    /// <summary>
    /// Boost du premier plan : la fenêtre active passe en priorité supérieure (si elle est en priorité normale
    /// et qu'aucune règle ne fixe sa priorité) ; la précédente retrouve sa priorité.
    /// </summary>
    void UpdateForegroundBoost(Settings s, Dictionary<ProcKey, Process> live, int fg)
    {
        bool enabled = s.ForegroundBoost.Enabled && !paused;
        if (boosted is { } b && (!enabled || b.Pid != fg || !live.ContainsKey(b)))
        {
            if (tracked.TryGetValue(b, out var bt) && bt.BoostOrig is { } orig && live.TryGetValue(b, out var bp))
                TryDo(b, Tr("fin du boost", "end of boost"), () =>
                {
                    if (bp.PriorityClass == ProcessPriorityClass.AboveNormal) // inchangé depuis le boost
                        bp.PriorityClass = orig;
                });
            if (tracked.TryGetValue(b, out var t0))
                t0.BoostOrig = null;
            boosted = null;
        }
        if (!enabled || boosted != null)
            return;
        var key = live.Keys.FirstOrDefault(k => k.Pid == fg);
        if (key.Name == null || !tracked.TryGetValue(key, out var t) || t.Rule?.Priority != null
            || Exclusions.IsProtected(key.Name, key.Pid, ownPid) || t.ProBalanceOrig != null)
            return;
        var p = live[key];
        if (TryDo(key, Tr("boost du premier plan", "foreground boost"), () =>
            {
                if (p.PriorityClass != ProcessPriorityClass.Normal)
                    return;
                p.PriorityClass = ProcessPriorityClass.AboveNormal;
                t.BoostOrig = ProcessPriorityClass.Normal;
            }) && t.BoostOrig != null)
            boosted = key;
    }

    /// <summary>
    /// Économie au repos : plan économique après N minutes sans clavier ni souris, sauf pendant le Mode Jeu,
    /// une vidéo (écran demandé) ou quand une règle impose déjà un plan.
    /// </summary>
    void UpdateIdleSaver(Settings s)
    {
        var cfg = s.IdleSaver;
        bool want = cfg.Enabled && !paused && !gameActive
                    && IdleTimeSource() >= TimeSpan.FromMinutes(cfg.Minutes)
                    && !DisplayRequiredSource()
                    && !power.HasRequestsOtherThan(IdleKey);
        if (want == idleActive)
            return;
        idleActive = want;
        if (want)
        {
            power.OnStart(IdleKey, cfg.Plan);
            Log.Info(Tr($"Économie au repos : aucune activité depuis {cfg.Minutes} min, plan économique activé", $"Idle saver: no activity for {cfg.Minutes} min, power-saving plan on"), LogCategory.Power);
        }
        else
        {
            power.OnExit(IdleKey);
            Log.Info(Tr("Économie au repos : retour au plan habituel", "Idle saver: back to the usual plan"), LogCategory.Power);
        }
    }

    /// <summary>
    /// Nettoyage mémoire au-delà du seuil (au plus toutes les 5 min) ou sur demande : liste de veille vidée
    /// et programmes inactifs allégés (ni premier plan, ni jeu, ni système, moins de 1 % de CPU).
    /// </summary>
    void RunMemoryCleanup(Settings s, Dictionary<ProcKey, Process> live, List<ProcessRow> rows)
    {
        var cfg = s.MemoryCleanup;
        var (usedBefore, total) = Native.GetMemoryUsage();
        bool auto = cfg.Enabled && !paused && total > 0 && usedBefore * 100.0 / total >= cfg.ThresholdPercent
                    && now() - lastCleanup >= CleanupCooldown;
        if (!auto && !cleanupRequested)
            return;
        bool manual = cleanupRequested;
        cleanupRequested = false;
        lastCleanup = now();

        var done = new List<string>();
        if (cfg.PurgeStandby || manual)
        {
            try
            {
                MemoryCleaner.PurgeStandbyList();
                done.Add(Tr("cache vidé", "cache emptied"));
            }
            catch (Exception ex)
            {
                Log.Warn(Tr($"Nettoyage mémoire : liste de veille non vidée ({ex.Message})", $"Memory cleanup: standby list not emptied ({ex.Message})"), LogCategory.Power);
            }
        }
        if (cfg.TrimIdle || manual)
        {
            int fg = foregroundPid();
            var cpu = rows.ToDictionary(r => r.Pid, r => r.Cpu);
            int trimmed = 0;
            foreach (var (key, p) in live)
            {
                if (key.Pid == fg || Exclusions.IsProtected(key.Name, key.Pid, ownPid)
                    || (tracked.TryGetValue(key, out var t) && t.Rule?.IsGame == true)
                    || cpu.GetValueOrDefault(key.Pid) >= 1)
                    continue;
                try
                {
                    MemoryCleaner.TrimWorkingSet(p);
                    trimmed++;
                }
                catch { } // processus protégé ou terminé : ignoré
            }
            done.Add(Tr($"{trimmed} programmes inactifs allégés", $"{trimmed} idle programs trimmed"));
        }

        var (usedAfter, _) = Native.GetMemoryUsage();
        long freed = Math.Max(0, usedBefore - usedAfter);
        var message = Tr($"{freed / (1024 * 1024):N0} Mo libérés ({string.Join(", ", done)}).", $"{freed / (1024 * 1024):N0} MB freed ({string.Join(", ", done)}).");
        Log.Info(Tr($"Nettoyage mémoire{(manual ? "" : " automatique")} : {message}", $"{(manual ? "Memory" : "Automatic memory")} cleanup: {message}"), LogCategory.Power);
        if (manual) // l'automatique reste discret : journal seulement
            pendingNotifications.Add((Tr("Nettoyage mémoire", "Memory cleanup"), message));
    }

    /// <summary>Retire les préférences GPU posées par Corral pour les programmes qu'aucune règle ne vise plus.</summary>
    void CleanUpGpuPreferences(Settings s)
    {
        if (Gpu == null)
            return;
        try
        {
            var wanted = Gpu.Managed.Where(path => s.Rules.Any(r =>
                r.Enabled && r.GpuPreference != null && RuleMatcher.Matches(r.Pattern, Path.GetFileNameWithoutExtension(path))));
            foreach (var removed in Gpu.RemoveExcept(wanted.ToList()))
                Log.Info(Tr($"Préférence GPU retirée : {removed}", $"GPU preference removed: {removed}"), LogCategory.Rule);
        }
        catch (Exception ex)
        {
            Log.Error(Tr("Nettoyage des préférences GPU", "GPU preference cleanup"), ex);
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
                reason = Tr($"Corral : {string.Join(", ", names.Take(3))} en cours", $"Corral: {string.Join(", ", names.Take(3))} running");
        }
        if (reason == KeepAwakeReason)
            return;
        try
        {
            keepAwake.Set(reason);
            if ((reason == null) != (KeepAwakeReason == null))
                Log.Info(reason == null ? Tr("Mise en veille de nouveau autorisée", "Sleep allowed again") : Tr($"Mise en veille empêchée ({reason})", $"Sleep prevented ({reason})"), LogCategory.Power);
            KeepAwakeReason = reason;
        }
        catch (Exception ex)
        {
            Log.Error(Tr("Empêcher la mise en veille", "Prevent sleep"), ex);
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
                Log.Info(Tr($"{key.Name} ({key.Pid}) bloqué par la règle « {rule.Pattern} »", $"{key.Name} ({key.Pid}) blocked by rule “{rule.Pattern}”"), LogCategory.Rule);
                pendingNotifications.Add((Tr("Programme bloqué", "Program blocked"), Tr($"« {key.Name} » a été fermé : la règle « {rule.Pattern} » l'interdit.", $"“{key.Name}” was closed: rule “{rule.Pattern}” forbids it.")));
            }
            return;
        }

        var done = new List<string>();
        if (rule.Priority is { } priority)
        {
            if (priority == ProcessPriorityClass.RealTime)
                priority = ProcessPriorityClass.High; // temps réel peut bloquer le système
            if (TryDo(key, Tr("priorité", "priority"), () =>
                {
                    var current = p.PriorityClass;
                    p.PriorityClass = priority;
                    t.OrigPriority ??= current;
                }))
                done.Add(Tr($"priorité {priority}", $"priority {priority}"));
        }

        if (rule.AffinityMask is { } mask)
        {
            if (!RuleMatcher.IsValidAffinity(mask, cores))
                Log.Warn(Tr($"Règle « {rule.Pattern} » : masque d'affinité 0x{mask:X} invalide pour {cores} CPU", $"Rule “{rule.Pattern}”: affinity mask 0x{mask:X} invalid for {cores} CPUs"), LogCategory.Rule);
            else if (TryDo(key, Tr("affinité", "affinity"), () =>
                     {
                         var current = p.ProcessorAffinity;
                         p.ProcessorAffinity = (IntPtr)mask;
                         t.OrigAffinity ??= current;
                     }))
                done.Add(Tr($"affinité 0x{mask:X}", $"affinity 0x{mask:X}"));
        }

        if ((rule.CpuLimitPercent is > 0 || rule.MemoryLimitMB is > 0) && !jobLimited.Contains(key))
        {
            if (TryDo(key, "limites", () => JobLimiter.Apply(p, rule.CpuLimitPercent, rule.MemoryLimitMB)))
            {
                jobLimited.Add(key);
                done.Add(Tr("limites", "limits") + $" CPU {rule.CpuLimitPercent?.ToString() ?? "-"}% / RAM {rule.MemoryLimitMB?.ToString() ?? "-"} {Tr("Mo", "MB")}");
            }
        }

        if (rule.GpuPreference is { } gpuPref && t.Path != null && Gpu != null
            && TryDo(key, Tr("préférence GPU", "GPU preference"), () => Gpu.Set(t.Path, gpuPref)))
            done.Add(Tr($"carte graphique « {GpuPreferences.Label(gpuPref)} » (au prochain lancement)", $"graphics card “{GpuPreferences.Label(gpuPref)}” (at next start)"));

        if (rule.PowerPlan is { } plan)
        {
            power.OnStart(key, plan);
            done.Add(Tr("plan d'alimentation", "power plan"));
        }

        if (rule.EfficiencyMode is { } eco && TryDo(key, Tr("mode efficacité", "efficiency mode"), () =>
            {
                ProcessTweaks.SetEfficiencyMode(p.Handle, eco);
                t.EfficiencySet = true;
            }))
            done.Add(eco ? Tr("mode efficacité", "efficiency mode") : Tr("mode efficacité interdit", "efficiency mode forbidden"));

        if (rule.IoPriority is { } io && TryDo(key, Tr("priorité disque", "disk priority"), () =>
            {
                var current = ProcessTweaks.GetIoPriority(p.Handle);
                ProcessTweaks.SetIoPriority(p.Handle, io);
                t.OrigIo ??= current;
            }))
            done.Add(Tr($"disque {io}", $"disk {io}"));

        if (rule.MemoryPriority is { } memPrio && TryDo(key, Tr("priorité mémoire", "memory priority"), () =>
            {
                var current = ProcessTweaks.GetMemoryPriority(p.Handle);
                ProcessTweaks.SetMemoryPriority(p.Handle, memPrio);
                t.OrigMemory ??= current;
            }))
            done.Add(Tr($"mémoire {memPrio}", $"memory {memPrio}"));

        if (done.Count > 0)
            Log.Info(Tr($"{key.Name} ({key.Pid}) : règle « {rule.Pattern} » → {string.Join(", ", done)}", $"{key.Name} ({key.Pid}): rule “{rule.Pattern}” → {string.Join(", ", done)}"), LogCategory.Rule);
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
                Log.Info(Tr($"ProBalance : {key.Name} ({key.Pid}) abaissé (CPU système {sysCpu:0}%)", $"ProBalance: {key.Name} ({key.Pid}) lowered (system CPU {sysCpu:0}%)"), LogCategory.ProBalance);
                pendingActed.Add(key.Name);
            }
        }

        foreach (var key in toRestore)
        {
            if (!tracked.TryGetValue(key, out var t) || t.ProBalanceOrig is not { } orig)
                continue;
            t.ProBalanceOrig = null;
            if (live.TryGetValue(key, out var p) && TryDo(key, Tr("ProBalance restauration", "ProBalance restore"), () => p.PriorityClass = orig))
                Log.Info(Tr($"ProBalance : {key.Name} ({key.Pid}) restauré en {orig}", $"ProBalance: {key.Name} ({key.Pid}) restored to {orig}"), LogCategory.ProBalance);
        }
    }

    void RestoreAll()
    {
        foreach (var (key, t) in tracked)
        {
            var priority = t.OrigPriority ?? t.ProBalanceOrig ?? t.BoostOrig;
            t.BoostOrig = null;
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
                TryDo(key, Tr("restauration priorité", "priority restore"), () => p.PriorityClass = pr);
            if (affinity is { } af)
                TryDo(key, Tr("restauration affinité", "affinity restore"), () => p.ProcessorAffinity = af);
            if (io is { } i)
                TryDo(key, Tr("restauration priorité disque", "disk priority restore"), () => ProcessTweaks.SetIoPriority(p.Handle, i));
            if (memory is { } m)
                TryDo(key, Tr("restauration priorité mémoire", "memory priority restore"), () => ProcessTweaks.SetMemoryPriority(p.Handle, m));
            if (efficiency)
                TryDo(key, Tr("restauration mode efficacité", "efficiency mode restore"), () => ProcessTweaks.SetEfficiencyMode(p.Handle, null));
        }
        power.RestoreAll();
        gamePlanApplied = false;
        boosted = null;
        idleActive = false;
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

    static long SampleIo(int pid, Tracked t, double elapsedMs)
    {
        if (t.IoDenied)
            return 0;
        var current = ProcessIo.Read(pid);
        if (current == null)
        {
            t.IoDenied = true;
            return 0;
        }
        long rate = 0;
        if (t.LastIo is { } last && elapsedMs > 0 && current >= last)
            rate = (long)((current.Value - last) * 1000 / elapsedMs);
        t.LastIo = current;
        return rate;
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
            Log.Warn(Tr($"{key.Name} ({key.Pid}) : échec {what} : {ex.Message}", $"{key.Name} ({key.Pid}): {what} failed: {ex.Message}"));
            return false;
        }
    }
}
