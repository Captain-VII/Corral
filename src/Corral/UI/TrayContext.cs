using Microsoft.Win32;
using Corral.Core;
using Corral.Models;

namespace Corral.UI;

/// <summary>Icône de notification ; l'application vit tant qu'on n'a pas cliqué sur « Quitter ».</summary>
public sealed class TrayContext : ApplicationContext
{
    static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    readonly Engine engine;
    readonly RuleStore store;
    readonly Settings settings;
    readonly NotifyIcon tray;
    readonly MainForm form;
    readonly ToolStripMenuItem pauseItem;
    readonly ToolStripMenuItem updateItem = new() { Visible = false };
    readonly System.Windows.Forms.Timer updateTimer = new();
    UpdateInfo? pendingUpdate;
    bool checking;
    bool updateDialogOpen;
    bool exiting;

    public TrayContext(Engine engine, RuleStore store, Settings settings, bool startHidden, bool justUpdated = false)
    {
        this.engine = engine;
        this.store = store;
        this.settings = settings;
        form = new MainForm(engine, store, settings);
        form.CheckUpdatesRequested += (_, _) => CheckForUpdates(manual: true);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Ouvrir", null, (_, _) => ShowForm());
        updateItem.Click += (_, _) => OfferUpdate();
        menu.Items.Add(updateItem);
        pauseItem = new ToolStripMenuItem("Pause") { CheckOnClick = true };
        pauseItem.CheckedChanged += (_, _) => engine.SetPaused(pauseItem.Checked);
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => Exit());
        Theme.ApplyTo(menu);
        Theme.Changed += () => Theme.ApplyTo(menu);
        // Suit le thème de Windows quand « Système » est choisi.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && settings.Theme == ThemeMode.System)
                Theme.Set(ThemeMode.System);
        };

        tray = new NotifyIcon
        {
            Icon = AppIcon.Load(SystemInformation.SmallIconSize),
            Text = "Corral",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => ShowForm();
        tray.BalloonTipClicked += (_, _) =>
        {
            if (pendingUpdate != null)
                OfferUpdate();
        };

        engine.Start();
        if (!startHidden)
            ShowForm();

        if (justUpdated)
            tray.ShowBalloonTip(5000, "Corral", $"Corral a été mis à jour en version {Updater.CurrentVersion.ToString(3)}.", ToolTipIcon.Info);

        if (Updater.IsSupported)
        {
            Updater.CleanupLeftovers();
            updateTimer.Interval = (int)FirstCheckDelay.TotalMilliseconds;
            updateTimer.Tick += (_, _) =>
            {
                updateTimer.Interval = (int)CheckInterval.TotalMilliseconds;
                if (settings.CheckUpdates)
                    CheckForUpdates(manual: false);
            };
            updateTimer.Start();
        }
    }

    async void CheckForUpdates(bool manual)
    {
        if (!Updater.IsSupported)
        {
            if (manual)
                Message($"Mise à jour automatique indisponible : {Updater.UnsupportedReason}.", MessageBoxIcon.Information);
            return;
        }
        if (checking || exiting)
            return;
        checking = true;
        try
        {
            var info = await Updater.CheckAsync();
            if (exiting)
                return;
            if (info == null)
            {
                if (manual)
                    Message($"Corral est à jour (version {Updater.CurrentVersion.ToString(3)}).", MessageBoxIcon.Information);
                return;
            }

            pendingUpdate = info;
            updateItem.Text = $"Installer la mise à jour {info.Version.ToString(3)}…";
            updateItem.Visible = true;
            Log.Info($"Mise à jour disponible : {info.Version.ToString(3)}");

            if (manual)
                OfferUpdate();
            else if (settings.SkippedVersion != info.Version.ToString(3))
                tray.ShowBalloonTip(10_000, "Mise à jour de Corral",
                    $"La version {info.Version.ToString(3)} est disponible. Cliquez ici pour l'installer.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            // Hors ligne, GitHub indisponible, limite d'API… : on réessaiera au prochain passage.
            Log.Warn($"Vérification des mises à jour : {ex.Message}");
            if (manual)
                Message("Impossible de vérifier les mises à jour : " + ex.Message, MessageBoxIcon.Warning);
        }
        finally
        {
            checking = false;
        }
    }

    void OfferUpdate()
    {
        if (pendingUpdate == null || updateDialogOpen || exiting)
            return;
        updateDialogOpen = true;
        try
        {
            using var dlg = new UpdateDialog(pendingUpdate);
            dlg.ShowDialog();
            switch (dlg.Choice)
            {
                case UpdateChoice.Installed:
                    Exit(); // la nouvelle version attend que celle-ci libère sa place
                    break;
                case UpdateChoice.Skip:
                    settings.SkippedVersion = pendingUpdate.Version.ToString(3);
                    try { store.Save(settings); }
                    catch (Exception ex) { Log.Error("Enregistrement de la configuration", ex); }
                    break;
            }
        }
        finally
        {
            updateDialogOpen = false;
        }
    }

    void Message(string text, MessageBoxIcon icon)
    {
        var owner = form.Visible ? form : null;
        MessageBox.Show(owner, text, "Corral", MessageBoxButtons.OK, icon);
    }

    void ShowForm()
    {
        form.Show();
        if (form.WindowState == FormWindowState.Minimized)
            form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    void Exit()
    {
        if (exiting)
            return;
        exiting = true;
        updateTimer.Stop();
        engine.Stop();
        tray.Visible = false;
        tray.Dispose();
        form.CloseForReal();
        ExitThread();
    }
}
