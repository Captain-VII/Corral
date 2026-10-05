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

        using var engine = new Engine(RuleStore.Clone(settings), powerApi, powerStateFile);
        // Restaure tout si Windows ferme la session sans passer par « Quitter ».
        SystemEvents.SessionEnding += (_, _) => engine.Stop();
        Application.ApplicationExit += (_, _) => engine.Stop();

        Application.Run(new TrayContext(engine, store, settings, startHidden: args.Contains("--minimized") || justUpdated, justUpdated));
    }
}
