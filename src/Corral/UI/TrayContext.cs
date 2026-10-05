using System.Diagnostics;
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
    readonly Icon logo = AppIcon.Load(SystemInformation.SmallIconSize);
    Icon? cpuIcon;    // icône dessinée avec la charge du processeur (à libérer)
    int cpuIconValue = -1;
    OverlayWindow? overlay;
    readonly MainForm form;
    readonly ToolStripMenuItem pauseItem;
    readonly ToolStripMenuItem updateItem = new() { Visible = false };
    readonly System.Windows.Forms.Timer updateTimer = new();
    readonly ToolStripMenuItem proBalanceItem = new("ProBalance");
    readonly ToolStripMenuItem profileItem = new(Tr("Profil", "Profile"));
    readonly ToolStripMenuItem overlayItem = new(Tr("Mini-fenêtre", "Mini window"));
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
        form.DisplayChanged += ApplyDisplay;
        form.RestartRequested += Restart;
        // La fenêtre (créée ci-dessus) a installé le contexte de synchronisation de l'interface.
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip();
        menu.Items.Add(Tr("Ouvrir", "Open"), null, (_, _) => ShowForm());
        updateItem.Click += (_, _) => OfferUpdate();
        menu.Items.Add(updateItem);
        pauseItem = new ToolStripMenuItem("Pause") { CheckOnClick = true, ToolTipText = Tr("Suspend toutes les règles et ProBalance", "Suspends all rules and ProBalance") };
        pauseItem.CheckedChanged += (_, _) => engine.SetPaused(pauseItem.Checked);
        menu.Items.Add(pauseItem);
        proBalanceItem.ToolTipText = Tr("Activer ou désactiver ProBalance", "Turn ProBalance on or off");
        proBalanceItem.Click += (_, _) => form.SetProBalanceEnabled(!settings.ProBalance.Enabled);
        menu.Items.Add(proBalanceItem);
        var gameItem = new ToolStripMenuItem(Tr("Mode Jeu", "Game Mode")) { ToolTipText = Tr("Plan Performances, ProBalance réactif, programmes de fond calmés", "Performance plan, responsive ProBalance, calmed background programs") };
        gameItem.Click += (_, _) => form.ToggleGameMode();
        menu.Items.Add(gameItem);
        profileItem.DropDownItems.Add("-"); // rempli à l'ouverture ; un élément pour afficher la flèche
        menu.Items.Add(profileItem);
        overlayItem.ToolTipText = Tr("Petite fenêtre toujours visible avec le processeur et la mémoire", "Small always-visible window with CPU and memory");
        overlayItem.Click += (_, _) => form.SetOverlayEnabled(!settings.Overlay.Enabled);
        menu.Items.Add(overlayItem);
        menu.Opening += (_, _) =>
        {
            proBalanceItem.Checked = settings.ProBalance.Enabled;
            gameItem.Checked = gameActive;
            overlayItem.Checked = settings.Overlay.Enabled;
            FillProfiles();
        };
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Tr("Quitter", "Exit"), null, (_, _) => Exit());
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
            Icon = logo,
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

        // Infobulle, icône CPU et mini-fenêtre (mises à jour sur le thread de l'interface)
        engine.SnapshotReady += snap => ui.Post(_ =>
        {
            if (exiting)
                return;
            var text = $"Corral — CPU {snap.SystemCpu:0} %" + (snap.Paused ? Tr(" (en pause)", " (paused)") : "")
                       + (snap.GameMode ? Tr(" · Mode Jeu", " · Game Mode") : "") + (engine.KeepAwakeReason != null ? Tr(" · veille bloquée", " · sleep blocked") : "");
            if (text.Length > 63)
                text = text[..63]; // limite de Windows
            if (tray.Text != text)
                tray.Text = text;
            if (settings.TrayCpuIcon)
                UpdateCpuIcon(snap.SystemCpu);
            overlay?.SetSnapshot(snap);
        }, null);
        engine.ProBalanceActed += (names, cpu) => ui.Post(_ => NotifyProBalance(names), null);
        engine.GameModeChanged += (active, trigger) => ui.Post(_ =>
        {
            gameActive = active;
            if (exiting || !settings.GameMode.Notify)
                return;
            lastBalloonIsUpdate = false;
            tray.ShowBalloonTip(4000, Tr("Mode Jeu", "Game Mode"),
                active
                    ? (trigger != null
                        ? Tr($"Activé pour « {trigger} » : plan Performances et programmes de fond calmés.", $"On for “{trigger}”: Performance plan and calmed background programs.")
                        : Tr("Activé : plan Performances et programmes de fond calmés.", "On: Performance plan and calmed background programs."))
                    : Tr("Désactivé : tout est revenu à la normale.", "Off: everything is back to normal."),
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
        ApplyDisplay();
        if (!startHidden)
            ShowForm();

        if (justUpdated)
            tray.ShowBalloonTip(5000, "Corral", Tr($"Corral a été mis à jour en version {Updater.CurrentVersion.ToString(3)}.", $"Corral was updated to version {Updater.CurrentVersion.ToString(3)}."), ToolTipIcon.Info);

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

    void FillProfiles()
    {
        profileItem.DropDownItems.Clear();
        foreach (var p in settings.Profiles)
        {
            var name = p.Name;
            var item = new ToolStripMenuItem(name) { Checked = name == settings.ActiveProfile };
            item.Click += (_, _) => form.SwitchProfile(name);
            profileItem.DropDownItems.Add(item);
        }
        profileItem.DropDownItems.Add(new ToolStripSeparator());
        profileItem.DropDownItems.Add(Tr("Gérer les profils…", "Manage profiles…"), null, (_, _) =>
        {
            ShowForm();
            form.ShowPage("Règles");
        });
        Theme.ApplyTo(profileItem.DropDown);
    }

    /// <summary>Applique les options d'affichage : icône CPU ou logo, mini-fenêtre.</summary>
    void ApplyDisplay()
    {
        if (exiting)
            return;
        if (!settings.TrayCpuIcon && cpuIcon != null)
        {
            tray.Icon = logo;
            AppIcon.Free(cpuIcon);
            cpuIcon = null;
            cpuIconValue = -1;
        }
        if (settings.Overlay.Enabled && overlay == null)
        {
            overlay = new OverlayWindow(settings.Overlay);
            overlay.OpenRequested += ShowForm;
            overlay.HideRequested += () => form.SetOverlayEnabled(false);
            overlay.Moved += form.SaveOverlayPosition;
            overlay.Show();
        }
        else if (!settings.Overlay.Enabled && overlay != null)
        {
            overlay.Close();
            overlay.Dispose();
            overlay = null;
        }
    }

    void UpdateCpuIcon(double cpu)
    {
        int value = (int)Math.Round(cpu);
        if (value == cpuIconValue)
            return;
        cpuIconValue = value;
        var old = cpuIcon;
        cpuIcon = AppIcon.CpuIcon(cpu, SystemInformation.SmallIconSize);
        tray.Icon = cpuIcon;
        if (old != null)
            AppIcon.Free(old);
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
                Log.Warn(Tr($"Raccourci {GlobalHotkeys.Format((Keys)k)} ({name}) déjà utilisé par une autre application", $"Shortcut {GlobalHotkeys.Format((Keys)k)} ({name}) already used by another application"));
            }
        }
        Add(settings.Hotkeys.GameMode, Tr("Mode Jeu", "Game Mode"), () =>
        {
            var wasAuto = latestGameAuto();
            var message = form.ToggleGameMode(quiet: true);
            // Les changements d'état ont leur propre bulle ; on n'explique que le cas « actif automatiquement »
            if (message != null && wasAuto)
                Balloon(Tr("Mode Jeu", "Game Mode"), message);
        });
        Add(settings.Hotkeys.Pause, "Pause", () =>
        {
            pauseItem.Checked = !pauseItem.Checked;
            Balloon("Corral", pauseItem.Checked
                ? Tr("En pause : règles et ProBalance suspendus.", "Paused: rules and ProBalance suspended.")
                : Tr("Reprise : règles et ProBalance de nouveau actifs.", "Resumed: rules and ProBalance active again."));
        });
        Add(settings.Hotkeys.ShowWindow, Tr("Afficher Corral", "Show Corral"), ShowForm);
        return failed;
    }

    /// <summary>Mode Jeu actif sans demande manuelle (déclenché par un jeu).</summary>
    bool latestGameAuto() => gameActive && !engine.GameModeManual;

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
        var list = string.Join(", ", names.Take(3)) + (names.Count > 3 ? "…" : "");
        var text = names.Count == 1
            ? Tr($"« {names[0]} » saturait le processeur : sa priorité a été baissée un moment pour garder le PC réactif.", $"“{names[0]}” was saturating the CPU: its priority was lowered for a moment to keep the PC responsive.")
            : Tr($"{names.Count} programmes abaissés pour garder le PC réactif : {list}.", $"{names.Count} programs lowered to keep the PC responsive: {list}.");
        lastBalloonIsUpdate = false;
        tray.ShowBalloonTip(6000, "ProBalance", text, ToolTipIcon.Info);
        lastNotify = DateTime.UtcNow;
    }

    async void CheckForUpdates(bool manual)
    {
        if (!Updater.IsSupported)
        {
            if (manual)
                Message(Tr($"Mise à jour automatique indisponible : {Updater.UnsupportedReason}.", $"Automatic update unavailable: {Updater.UnsupportedReason}."), MessageBoxIcon.Information);
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
                    Message(Tr($"Corral est à jour (version {Updater.CurrentVersion.ToString(3)}).", $"Corral is up to date (version {Updater.CurrentVersion.ToString(3)})."), MessageBoxIcon.Information);
                return;
            }

            pendingUpdate = info;
            updateItem.Text = Tr($"Installer la mise à jour {info.Version.ToString(3)}…", $"Install update {info.Version.ToString(3)}…");
            updateItem.Visible = true;
            Log.Info(Tr($"Mise à jour disponible : {info.Version.ToString(3)}", $"Update available: {info.Version.ToString(3)}"), LogCategory.Update);

            if (manual)
                OfferUpdate();
            else if (settings.SkippedVersion != info.Version.ToString(3))
            {
                lastBalloonIsUpdate = true;
                tray.ShowBalloonTip(10_000, Tr("Mise à jour de Corral", "Corral update"),
                    Tr($"La version {info.Version.ToString(3)} est disponible. Cliquez ici pour l'installer.", $"Version {info.Version.ToString(3)} is available. Click here to install it."), ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            // Hors ligne, GitHub indisponible, limite d'API… : on réessaiera au prochain passage.
            Log.Warn(Tr($"Vérification des mises à jour : {ex.Message}", $"Update check: {ex.Message}"), LogCategory.Update);
            if (manual)
                Message(Tr("Impossible de vérifier les mises à jour : ", "Unable to check for updates: ") + ex.Message, MessageBoxIcon.Warning);
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

    /// <summary>Relance Corral (changement de langue) : la nouvelle instance attend que celle-ci se ferme.</summary>
    void Restart()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--restart") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error("Redémarrage", ex);
            Message(Tr("Impossible de redémarrer Corral : ", "Unable to restart Corral: ") + ex.Message, MessageBoxIcon.Warning);
            return;
        }
        Exit();
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
        overlay?.Close();
        overlay?.Dispose();
        tray.Visible = false;
        tray.Dispose();
        if (cpuIcon != null)
            AppIcon.Free(cpuIcon);
        form.CloseForReal();
        ExitThread();
    }
}
