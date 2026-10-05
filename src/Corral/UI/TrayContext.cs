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
    readonly ToolStripMenuItem proBalanceItem = new("ProBalance");
    readonly GlobalHotkeys hotkeys = new();
    readonly SynchronizationContext ui;
    // Bulles ProBalance : au plus une toutes les 30 s, les suivantes sont regroupées
    static readonly TimeSpan NotifyCooldown = TimeSpan.FromSeconds(30);
    readonly System.Windows.Forms.Timer notifyTimer = new();
    readonly List<string> pendingNotify = new();
    DateTime lastNotify = DateTime.MinValue;
    bool lastBalloonIsUpdate;
    bool gameActive;
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
        form.PauseRequested += paused => pauseItem!.Checked = paused; // l'élément de menu applique la pause au moteur
        // La fenêtre (créée ci-dessus) a installé le contexte de synchronisation de l'interface.
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Ouvrir", null, (_, _) => ShowForm());
        updateItem.Click += (_, _) => OfferUpdate();
        menu.Items.Add(updateItem);
        pauseItem = new ToolStripMenuItem("Pause") { CheckOnClick = true, ToolTipText = "Suspend toutes les règles et ProBalance" };
        pauseItem.CheckedChanged += (_, _) => engine.SetPaused(pauseItem.Checked);
        menu.Items.Add(pauseItem);
        proBalanceItem.ToolTipText = "Activer ou désactiver ProBalance";
        proBalanceItem.Click += (_, _) => form.SetProBalanceEnabled(!settings.ProBalance.Enabled);
        menu.Items.Add(proBalanceItem);
        var gameItem = new ToolStripMenuItem("Mode Jeu") { ToolTipText = "Plan Performances, ProBalance réactif, programmes de fond calmés" };
        gameItem.Click += (_, _) => form.ToggleGameMode();
        menu.Items.Add(gameItem);
        menu.Opening += (_, _) =>
        {
            proBalanceItem.Checked = settings.ProBalance.Enabled;
            gameItem.Checked = gameActive;
        };
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
            if (lastBalloonIsUpdate && pendingUpdate != null)
                OfferUpdate();
            else
                ShowForm();
        };

        // Infobulle de l'icône : charge du processeur (mise à jour sur le thread de l'interface)
        engine.SnapshotReady += snap => ui.Post(_ =>
        {
            if (exiting)
                return;
            var text = $"Corral — CPU {snap.SystemCpu:0} %" + (snap.Paused ? " (en pause)" : "")
                       + (snap.GameMode ? " · Mode Jeu" : "") + (engine.KeepAwakeReason != null ? " · veille bloquée" : "");
            if (tray.Text != text)
                tray.Text = text;
        }, null);
        engine.ProBalanceActed += (names, cpu) => ui.Post(_ => NotifyProBalance(names), null);
        engine.GameModeChanged += (active, trigger) => ui.Post(_ =>
        {
            gameActive = active;
            if (exiting || !settings.GameMode.Notify)
                return;
            lastBalloonIsUpdate = false;
            tray.ShowBalloonTip(4000, "Mode Jeu",
                active
                    ? (trigger != null ? $"Activé pour « {trigger} » : plan Performances et programmes de fond calmés." : "Activé : plan Performances et programmes de fond calmés.")
                    : "Désactivé : tout est revenu à la normale.",
                ToolTipIcon.Info);
        }, null);
        notifyTimer.Tick += (_, _) =>
        {
            notifyTimer.Stop();
            FlushNotify();
        };
        // Programme bloqué, alerte de surveillance : toujours signalés
        engine.Notification += (title, message) => ui.Post(_ => Balloon(title, message), null);

        form.HotkeysChanged += () => form.SetHotkeyStatus(RegisterHotkeys());
        form.SetHotkeyStatus(RegisterHotkeys());

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

    void Balloon(string title, string message)
    {
        if (exiting)
            return;
        lastBalloonIsUpdate = false;
        tray.ShowBalloonTip(6000, title, message, ToolTipIcon.Info);
    }

    /// <summary>(Ré)enregistre les raccourcis globaux ; renvoie ceux qu'une autre application utilise déjà.</summary>
    List<string> RegisterHotkeys()
    {
        hotkeys.UnregisterAll();
        var failed = new List<string>();
        void Add(int? keys, string name, Action action)
        {
            if (keys is { } k && !hotkeys.Register((Keys)k, action))
            {
                failed.Add($"{name} ({GlobalHotkeys.Format((Keys)k)})");
                Log.Warn($"Raccourci {GlobalHotkeys.Format((Keys)k)} ({name}) déjà utilisé par une autre application");
            }
        }
        Add(settings.Hotkeys.GameMode, "Mode Jeu", () =>
        {
            var message = form.ToggleGameMode(quiet: true);
            // Les changements d'état ont leur propre bulle ; on n'explique que le cas « actif automatiquement »
            if (message != null && message.Contains("automatiquement"))
                Balloon("Mode Jeu", message);
        });
        Add(settings.Hotkeys.Pause, "Pause", () =>
        {
            pauseItem.Checked = !pauseItem.Checked;
            Balloon("Corral", pauseItem.Checked ? "En pause : règles et ProBalance suspendus." : "Reprise : règles et ProBalance de nouveau actifs.");
        });
        Add(settings.Hotkeys.ShowWindow, "Afficher Corral", ShowForm);
        return failed;
    }

    void NotifyProBalance(IReadOnlyList<string> names)
    {
        if (exiting || !settings.NotifyProBalance)
            return;
        pendingNotify.AddRange(names);
        var wait = lastNotify + NotifyCooldown - DateTime.UtcNow;
        if (wait <= TimeSpan.Zero)
            FlushNotify();
        else if (!notifyTimer.Enabled)
        {
            notifyTimer.Interval = Math.Max(100, (int)wait.TotalMilliseconds);
            notifyTimer.Start();
        }
    }

    void FlushNotify()
    {
        if (exiting || pendingNotify.Count == 0 || !settings.NotifyProBalance)
        {
            pendingNotify.Clear();
            return;
        }
        var names = pendingNotify.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        pendingNotify.Clear();
        var text = names.Count == 1
            ? $"« {names[0]} » saturait le processeur : sa priorité a été baissée un moment pour garder le PC réactif."
            : $"{names.Count} programmes abaissés pour garder le PC réactif : {string.Join(", ", names.Take(3))}{(names.Count > 3 ? "…" : "")}.";
        lastBalloonIsUpdate = false;
        tray.ShowBalloonTip(6000, "ProBalance", text, ToolTipIcon.Info);
        lastNotify = DateTime.UtcNow;
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
            Log.Info($"Mise à jour disponible : {info.Version.ToString(3)}", LogCategory.Update);

            if (manual)
                OfferUpdate();
            else if (settings.SkippedVersion != info.Version.ToString(3))
            {
                lastBalloonIsUpdate = true;
                tray.ShowBalloonTip(10_000, "Mise à jour de Corral",
                    $"La version {info.Version.ToString(3)} est disponible. Cliquez ici pour l'installer.", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            // Hors ligne, GitHub indisponible, limite d'API… : on réessaiera au prochain passage.
            Log.Warn($"Vérification des mises à jour : {ex.Message}", LogCategory.Update);
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
        notifyTimer.Stop();
        hotkeys.Dispose();
        engine.Stop();
        tray.Visible = false;
        tray.Dispose();
        form.CloseForReal();
        ExitThread();
    }
}
