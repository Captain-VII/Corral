using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using Microsoft.Win32;
using Corral.Core;
using Corral.Models;

namespace Corral.Service;

/// <summary>Service Windows « Corral » (installé par le MSI) : héberge le moteur, sans fenêtre ni session.</summary>
public sealed class CorralService : ServiceBase
{
    public const string Name = "Corral";

    ServiceHost? host;

    public CorralService()
    {
        ServiceName = Name;
        CanStop = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args) => host = ServiceHost.StartService();

    // Arrêt du service ou de Windows : tout ce que Corral a modifié est restauré
    protected override void OnStop() => host?.Dispose();

    protected override void OnShutdown() => host?.Dispose();
}

/// <summary>Moteur + pipe : reçoit les commandes des interfaces et leur diffuse instantanés, notifications et journal.</summary>
public sealed class ServiceHost : IDisposable
{
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Corral");

    readonly string dir;
    readonly PolicySet policies;
    readonly PipeServer server;
    readonly RuleStore store;
    readonly Engine engine;
    readonly GpuSampler gpu = new();
    readonly object sync = new();
    Settings settings;
    bool fresh;
    int updating;
    bool disposed;

    string RelaunchFile => Path.Combine(dir, "relaunch.json");

    /// <param name="powerApi">Plans d'alimentation (les tests en passent un factice).</param>
    public ServiceHost(string dataDirectory, string pipeName, PolicySet policies, bool firstInstance = true, IPowerPlanApi? powerApi = null)
    {
        dir = dataDirectory;
        this.policies = policies;
        Directory.CreateDirectory(dir);
        powerApi ??= new PowerCfg();
        var powerStateFile = Path.Combine(dir, "powerplan.restore");
        PowerPlanManager.RecoverFromCrash(powerApi, powerStateFile);
        store = new RuleStore(Path.Combine(dir, "config.json"));
        fresh = !File.Exists(store.FilePath);
        settings = Program.PrepareSettings(store, policies);

        server = new PipeServer(pipeName, firstInstance);
        engine = new Engine(RuleStore.Clone(settings), powerApi, powerStateFile, () => server.ActiveClient()?.Session?.ForegroundPid ?? 0)
        {
            Gpu = new GpuPreferences(Path.Combine(dir, "gpu-managed.json")) { Hive = OpenUserHive },
            GpuUsage = gpu.Sample,
            // Sans interface connectée, personne n'est devant l'écran à mesurer : on ne passe jamais en économie
            IdleTimeSource = () => TimeSpan.FromSeconds(server.ActiveClient()?.Session?.IdleSeconds ?? 0),
            DisplayRequiredSource = () => server.ActiveClient()?.Session?.DisplayRequired ?? false,
        };
        engine.SnapshotReady += snap =>
        {
            if (server.Clients.Count > 0)
                server.Broadcast(new PipeMessage { Type = "snapshot", Snapshot = snap, Flag = engine.GameModeManual, Text = engine.KeepAwakeReason });
        };
        engine.ProBalanceActed += (names, cpu) => server.Broadcast(new PipeMessage { Type = "acted", Names = names.ToArray(), Value = cpu });
        engine.GameModeChanged += (active, trigger) => server.Broadcast(new PipeMessage { Type = "game-changed", Flag = active, Text = trigger });
        engine.Notification += (title, message) => server.Broadcast(new PipeMessage { Type = "notify", Text = title, Text2 = message });
        Log.Written += OnLog;
        server.Connected += OnConnected;
        server.Received += OnReceived;
    }

    /// <summary>Démarrage en tant que service : dossier protégé, journal, Observateur d'événements.</summary>
    public static ServiceHost StartService()
    {
        var dir = DataDirectory;
        Directory.CreateDirectory(dir);
        Secure(dir);
        Log.Init(dir);
        Log.EnableEventLog();
        Log.Info(Tr($"Service Corral {Updater.CurrentVersion.ToString(3)} démarré", $"Corral service {Updater.CurrentVersion.ToString(3)} started"));
        var host = new ServiceHost(dir, Protocol.PipeName, Policies.Current);
        Lang.Set(host.settings.Language);
        host.Start();
        host.RelaunchInterfaces();
        return host;
    }

    public void Start()
    {
        server.Start();
        engine.Start();
    }

    /// <summary>Seuls SYSTEM et les administrateurs écrivent dans le dossier ; les utilisateurs le lisent (journal).</summary>
    static void Secure(string dir)
    {
        try
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            void Allow(WellKnownSidType who, FileSystemRights rights) =>
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(who, null), rights, inherit, PropagationFlags.None, AccessControlType.Allow));
            Allow(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
            Allow(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
            Allow(WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);
            new DirectoryInfo(dir).SetAccessControl(security);
        }
        catch (Exception ex)
        {
            Log.Error("Protection du dossier du service", ex);
        }
    }

    RegistryKey? OpenUserHive()
    {
        var sid = server.ActiveClient()?.Sid;
        try { return sid == null ? null : Registry.Users.OpenSubKey(sid, writable: true); }
        catch { return null; }
    }

    void OnLog(LogEntry entry) => server.Broadcast(new PipeMessage { Type = "log", Log = new[] { entry } });

    void OnConnected(PipeClient client)
    {
        lock (sync)
            client.Send(new PipeMessage { Type = "welcome", Settings = settings, Flag = fresh, Log = Log.Recent().TakeLast(200).ToArray() });
    }

    void OnReceived(PipeClient client, PipeMessage m)
    {
        switch (m.Type)
        {
            case "session":
                client.Session = m.Session;
                client.SessionTime = DateTime.UtcNow;
                break;
            case "save":
            case "apply":
                if (m.Settings != null)
                    Save(m.Settings, apply: m.Type == "apply");
                break;
            case "pause":
                engine.SetPaused(m.Flag);
                break;
            case "game":
                engine.SetGameMode(m.Flag);
                break;
            case "clean":
                engine.CleanMemoryNow();
                break;
            case "priority":
                client.Send(new PipeMessage
                {
                    Type = "result",
                    Id = m.Id,
                    Text = policies.LockSettings
                        ? Tr("Réglage verrouillé par votre organisation.", "Setting locked by your organization.")
                        : engine.SetPriorityOnce(m.Pid, m.Text ?? "", m.Priority),
                });
                break;
            case "update":
                _ = UpdateAsync(client, m.Id);
                break;
        }
    }

    void Save(Settings incoming, bool apply)
    {
        if (policies.LockSettings)
            return; // l'interface est en lecture seule : on ignore toute tentative
        incoming.Normalize();
        Policies.Apply(incoming, policies);
        lock (sync)
        {
            settings = incoming;
            fresh = false;
            try { store.Save(incoming); }
            catch (Exception ex) { Log.Error("Enregistrement de la configuration", ex); }
        }
        if (apply)
            engine.UpdateSettings(RuleStore.Clone(incoming));
    }

    /// <summary>Télécharge l'installeur signé, prévient les interfaces puis lance msiexec (qui arrête et relance le service).</summary>
    async Task UpdateAsync(PipeClient client, long id)
    {
        string? error = null;
        if (policies.DisableUpdates)
            error = Tr("Mises à jour désactivées par votre organisation.", "Updates disabled by your organization.");
        else if (Interlocked.Exchange(ref updating, 1) == 1)
            error = Tr("Une mise à jour est déjà en cours.", "An update is already in progress.");
        if (error != null)
        {
            client.Send(new PipeMessage { Type = "result", Id = id, Text = error });
            return;
        }
        try
        {
            var info = await Updater.CheckAsync() ?? throw new InvalidOperationException(Tr("Corral est déjà à jour.", "Corral is already up to date."));
            var folder = Path.Combine(dir, "update");
            var msi = await Updater.DownloadMsiAsync(info, folder);
            // Les interfaces vont se fermer : le nouveau service les relancera dans leurs sessions
            var sessions = server.Clients.Select(c => c.SessionId).Where(s => s > 0).Distinct().ToArray();
            File.WriteAllText(RelaunchFile, JsonSerializer.Serialize(sessions));
            client.Send(new PipeMessage { Type = "result", Id = id });
            server.Broadcast(new PipeMessage { Type = "updating" });
            Log.Info(Tr($"Installation de la version {info.Version.ToString(3)}", $"Installing version {info.Version.ToString(3)}"), LogCategory.Update);
            await Task.Delay(2000); // laisse les interfaces se fermer (fichier en cours d'utilisation sinon)
            Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msi}\" /qn /norestart /l*v \"{Path.Combine(folder, "install.log")}\"")
            {
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Mise à jour par le service", ex);
            client.Send(new PipeMessage { Type = "result", Id = id, Text = ex.Message });
            Interlocked.Exchange(ref updating, 0);
        }
    }

    /// <summary>Après une mise à jour : relance l'interface dans les sessions où elle tournait.</summary>
    void RelaunchInterfaces()
    {
        try
        {
            if (!File.Exists(RelaunchFile))
                return;
            bool recent = DateTime.UtcNow - File.GetLastWriteTimeUtc(RelaunchFile) < TimeSpan.FromMinutes(15);
            var sessions = JsonSerializer.Deserialize<int[]>(File.ReadAllText(RelaunchFile)) ?? Array.Empty<int>();
            File.Delete(RelaunchFile);
            if (!recent)
                return;
            foreach (var session in sessions)
            {
                try { SessionLauncher.Launch(Environment.ProcessPath!, "--updated", session); }
                catch (Exception ex) { Log.Warn(Tr($"Relance de l'interface (session {session}) : {ex.Message}", $"Interface relaunch (session {session}): {ex.Message}")); }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Relance des interfaces", ex);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
        }
        Log.Written -= OnLog;
        server.Dispose();
        engine.Stop();
        gpu.Dispose();
        Log.Info(Tr("Service Corral arrêté", "Corral service stopped"));
    }
}
