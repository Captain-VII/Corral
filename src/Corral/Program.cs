using Microsoft.Win32;
using Corral.Core;
using Corral.UI;

namespace Corral;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool justUpdated = args.Contains("--updated");
        using var mutex = new Mutex(true, @"Global\Corral_SingleInstance", out bool firstInstance);
        if (!firstInstance && justUpdated)
        {
            // Après une mise à jour, l'ancienne version est en train de se fermer : on attend qu'elle libère la place.
            try { firstInstance = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { firstInstance = true; }
        }
        if (!firstInstance)
        {
            MessageBox.Show("Corral est déjà lancé (icône dans la zone de notification).", "Corral",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error("Erreur interface", e.Exception);
            MessageBox.Show(e.Exception.Message, "Corral - erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Erreur fatale", e.ExceptionObject as Exception);

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Corral");
        Log.Init(dir);
        Log.Info("Démarrage de Corral");

        var powerApi = new PowerCfg();
        var powerStateFile = Path.Combine(dir, "powerplan.restore");
        PowerPlanManager.RecoverFromCrash(powerApi, powerStateFile);

        var store = new RuleStore(Path.Combine(dir, "config.json"));
        var settings = store.Load();
        UI.Theme.Set(settings.Theme);

        if (settings.GameMode.PowerPlan == null)
        {
            // Premier lancement du Mode Jeu : proposer le plan Performances de Windows s'il existe
            var perf = PowerCfg.List().FirstOrDefault(p => PowerCfg.PerformancePlans.Contains(p.Id));
            settings.GameMode.PowerPlan = perf?.Id ?? Guid.Empty;
            try { store.Save(settings); } catch (Exception ex) { Log.Error("Enregistrement de la configuration", ex); }
        }

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
            Gpu = new GpuPreferences(Path.Combine(dir, "gpu-managed.json")),
            GpuUsage = gpuSampler.Sample,
        };
        // Restaure tout si Windows ferme la session sans passer par « Quitter ».
        SystemEvents.SessionEnding += (_, _) => engine.Stop();
        Application.ApplicationExit += (_, _) => engine.Stop();

        Application.Run(new TrayContext(engine, store, settings, startHidden: args.Contains("--minimized") || justUpdated, justUpdated));
    }
}
