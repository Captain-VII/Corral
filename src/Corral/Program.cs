using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using Corral.Core;
using Corral.Models;
using Corral.Service;
using Corral.UI;

namespace Corral;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--service"))
        {
            ServiceBase.Run(new CorralService());
            return;
        }

        Lang.Set(null); // langue de Windows, en attendant la configuration
        bool justUpdated = args.Contains("--updated");
        bool restarted = args.Contains("--restart"); // changement de langue

        // Installation MSI : le moteur tourne dans le service, l'interface s'y connecte sans droits administrateur.
        // Exe portable : le moteur tourne ici, il faut les droits administrateur.
        RemoteEngine? remote = null;
        if (RemoteEngine.ServiceInstalled)
        {
            remote = RemoteEngine.Connect(TimeSpan.FromSeconds(justUpdated ? 90 : 30));
            if (remote == null)
            {
                MessageBox.Show(Tr("Le service Corral ne répond pas. Redémarrez le PC ou réinstallez Corral.", "The Corral service is not responding. Restart the PC or reinstall Corral."),
                    "Corral", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        else if (!IsAdministrator())
        {
            RelaunchElevated(args);
            return;
        }

        // Une seule interface par session avec le service, un seul moteur par PC sinon
        using var mutex = new Mutex(true, remote != null ? @"Local\Corral_UI" : @"Global\Corral_SingleInstance", out bool firstInstance);
        if (!firstInstance && (justUpdated || restarted))
        {
            // Après une mise à jour, l'ancienne version est en train de se fermer : on attend qu'elle libère la place.
            try { firstInstance = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { firstInstance = true; }
        }
        if (!firstInstance)
        {
            remote?.Dispose();
            MessageBox.Show(Tr("Corral est déjà lancé (icône dans la zone de notification).", "Corral is already running (icon in the notification area)."), "Corral",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error("Erreur interface", e.Exception);
            MessageBox.Show(e.Exception.Message, Tr("Corral - erreur", "Corral - error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Erreur fatale", e.ExceptionObject as Exception);

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Corral");
        var store = new RuleStore(Path.Combine(dir, "config.json"));
        if (remote != null)
            RunWithService(remote, store, args, justUpdated, restarted);
        else
            RunLocal(store, args, justUpdated, restarted);
    }

    static void RunWithService(RemoteEngine remote, RuleStore localStore, string[] args, bool justUpdated, bool restarted)
    {
        Log.Init(remote.ConfigDirectory);
        Log.Info(Tr("Interface connectée au service Corral", "Interface connected to the Corral service"));
        var settings = remote.InitialSettings;
        if (remote.Fresh && File.Exists(localStore.FilePath))
        {
            // Premier démarrage du service : il reprend la configuration de l'exe portable
            settings = localStore.Load();
            Policies.Apply(settings, Policies.Current);
            remote.UpdateSettings(settings);
            Log.Info(Tr("Configuration de l'exe portable transmise au service", "Portable exe settings handed over to the service"));
        }
        Lang.Set(settings.Language);
        Profiles.Ensure(settings, Tr("Principal", "Main"));
        UI.Theme.Set(settings.Theme);
        if (Installation.IsMsi)
        {
            // Le raccourci et le lancement à l'ouverture de session sont ceux de l'installeur
            var exe = Environment.ProcessPath!;
            Task.Run(() => StartMenu.Sync(false, exe));
        }

        using (remote)
            Application.Run(new TrayContext(remote, remote, settings, startHidden: (args.Contains("--minimized") || justUpdated) && !restarted, justUpdated));
    }

    static void RunLocal(RuleStore store, string[] args, bool justUpdated, bool restarted)
    {
        Log.Init(store.ConfigDirectory);
        Log.Info("Démarrage de Corral");
        Log.EnableEventLog();

        var powerApi = new PowerCfg();
        var powerStateFile = Path.Combine(store.ConfigDirectory, "powerplan.restore");
        PowerPlanManager.RecoverFromCrash(powerApi, powerStateFile);

        var settings = PrepareSettings(store, Policies.Current);
        Lang.Set(settings.Language);
        UI.Theme.Set(settings.Theme);

        if (Updater.IsPublishedBuild)
        {
            // Raccourci du menu Démarrer et tâche de démarrage pointés sur l'exe actuel (même s'il a été déplacé).
            // En arrière-plan : schtasks et le shell prennent quelques dizaines de ms.
            var exe = Environment.ProcessPath!;
            bool shortcut = settings.StartMenuShortcut;
            Task.Run(() =>
            {
                StartMenu.Sync(shortcut, exe);
                AutoStart.RepairPath(exe);
            });
        }

        using var gpuSampler = new GpuSampler();
        using var engine = new Engine(RuleStore.Clone(settings), powerApi, powerStateFile)
        {
            Gpu = new GpuPreferences(Path.Combine(store.ConfigDirectory, "gpu-managed.json")),
            GpuUsage = gpuSampler.Sample,
        };
        // Restaure tout si Windows ferme la session sans passer par « Quitter ».
        SystemEvents.SessionEnding += (_, _) => engine.Stop();
        Application.ApplicationExit += (_, _) => engine.Stop();

        Application.Run(new TrayContext(engine, store, settings, startHidden: (args.Contains("--minimized") || justUpdated) && !restarted, justUpdated));
    }

    /// <summary>Charge la configuration et la complète (profil, stratégies, plan du Mode Jeu). Partagé avec le service.</summary>
    internal static Settings PrepareSettings(RuleStore store, PolicySet policies)
    {
        var settings = store.Load();
        Profiles.Ensure(settings, Tr("Principal", "Main"));
        Policies.Apply(settings, policies);
        if (settings.GameMode.PowerPlan == null)
        {
            // Premier lancement du Mode Jeu : proposer le plan Performances de Windows s'il existe
            var perf = PowerCfg.List().FirstOrDefault(p => PowerCfg.PerformancePlans.Contains(p.Id));
            settings.GameMode.PowerPlan = perf?.Id ?? Guid.Empty;
            try { store.Save(settings); } catch (Exception ex) { Log.Error("Enregistrement de la configuration", ex); }
        }
        return settings;
    }

    static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Exe portable lancé sans droits : on se relance via l'invite UAC.</summary>
    static void RelaunchElevated(string[] args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, string.Join(" ", args.Select(a => $"\"{a}\"")))
            {
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show(Tr("Corral a besoin des droits administrateur pour agir sur les processus.", "Corral needs administrator rights to act on processes."),
                "Corral", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
