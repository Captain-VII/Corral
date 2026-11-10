using System.Diagnostics;
using Corral.Core;
using Corral.Models;

namespace Corral.UI;

// Page « Options » : apparence, démarrage, mises à jour, raccourcis globaux.
public sealed partial class MainForm
{
    readonly ComboBox themeChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    readonly ToggleSwitch autoStart = new();
    bool updatingAutoStart;

    readonly Label hotkeyStatus = new() { AutoSize = true, Tag = Theme.HintTag, MaximumSize = new Size(640, 0), Margin = new Padding(0, 6, 0, 0) };

    /// <summary>Affiche les raccourcis qui n'ont pas pu être enregistrés (déjà pris par une autre application).</summary>
    public void SetHotkeyStatus(IReadOnlyList<string> failed)
    {
        hotkeyStatus.Text = failed.Count == 0
            ? Tr("Actifs partout, même quand Corral est caché ou qu'un jeu est au premier plan.", "Active everywhere, even when Corral is hidden or a game is in the foreground.")
            : Tr($"Déjà utilisé par une autre application : {string.Join(", ", failed)}. Choisissez une autre combinaison.",
                $"Already used by another application: {string.Join(", ", failed)}. Choose another combination.");
        hotkeyStatus.ForeColor = failed.Count == 0 ? Theme.Current.Muted : Theme.Current.Warning;
    }

    Control BuildOptionsPage()
    {
        themeChoice.Items.AddRange(new object[] { Tr("Système", "System"), Tr("Clair", "Light"), Tr("Sombre", "Dark") }); // même ordre que ThemeMode
        themeChoice.SelectedIndex = (int)settings.Theme;
        themeChoice.SelectedIndexChanged += (_, _) =>
        {
            settings.Theme = (ThemeMode)themeChoice.SelectedIndex;
            SaveSettings();
            Theme.Set(settings.Theme);
        };
        autoStart.CheckedChanged += (_, _) => ToggleAutoStart();
        bool msi = Installation.IsMsi;
        var startMenu = new ToggleSwitch { Checked = settings.StartMenuShortcut || msi, Enabled = Updater.IsPublishedBuild && !msi };
        if (msi)
            tips.SetToolTip(startMenu, Tr("Raccourci géré par l'installeur.", "Shortcut managed by the installer."));
        else if (!Updater.IsPublishedBuild)
            tips.SetToolTip(startMenu, Tr("Disponible uniquement avec l'exe publié (pas en développement).", "Only available with the published exe (not in development)."));
        startMenu.CheckedChanged += (_, _) =>
        {
            settings.StartMenuShortcut = startMenu.Checked;
            SaveSettings();
            if (Updater.IsPublishedBuild)
                StartMenu.Sync(startMenu.Checked, Environment.ProcessPath!);
        };
        if (msi)
        {
            autoStart.Enabled = false;
            tips.SetToolTip(autoStart, Tr("Lancement à l'ouverture de session géré par l'installeur.", "Start at sign-in managed by the installer."));
        }
        var updates = new ToggleSwitch { Checked = settings.CheckUpdates, Enabled = !Policies.Current.DisableUpdates };
        updates.CheckedChanged += (_, _) =>
        {
            settings.CheckUpdates = updates.Checked;
            SaveSettings();
        };
        var check = new ModernButton(Tr("Vérifier maintenant", "Check now"));
        check.Click += (_, _) => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
        var openFolder = new ModernButton(Tr("Ouvrir le dossier", "Open folder"));
        openFolder.Click += (_, _) => OpenConfigFolder();
        var report = new ModernButton(Tr("Signaler un problème", "Report a problem"));
        report.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(BugReportUrl()) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Ouverture de la page de signalement", ex); }
        };
        var showWelcome = new ModernButton(Tr("Revoir l'accueil", "Show welcome again"));
        showWelcome.Click += (_, _) =>
        {
            settings.WelcomeDismissed = false;
            SaveSettings();
            if (welcome != null)
                welcome.Visible = true;
            else
                MessageBox.Show(this, Tr("L'accueil s'affichera au prochain lancement de Corral.", "The welcome card will show the next time Corral starts."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            ShowPage("Processus");
        };

        var shortcuts = new Label
        {
            AutoSize = true,
            Tag = Theme.HintTag,
            MaximumSize = new Size(640, 0),
            Text = Tr("Ctrl+F : rechercher un processus · Ctrl+N : nouvelle règle · Ctrl+1 à 9 : changer de page · " +
                      "F5 : actualiser · Suppr / Entrée / Espace : supprimer, modifier, activer la règle sélectionnée",
                "Ctrl+F: search processes · Ctrl+N: new rule · Ctrl+1 to 9: switch page · " +
                "F5: refresh · Del / Enter / Space: delete, edit, toggle the selected rule"),
        };

        // Langue : appliquée au prochain démarrage (proposé tout de suite)
        var available = Lang.Available();
        var languages = new[] { "auto" }.Concat(available.Select(l => l.Code)).ToArray();
        var language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        language.Items.Add(Tr("Automatique (Windows)", "Automatic (Windows)"));
        language.Items.AddRange(available.Select(l => (object)l.Name).ToArray());
        language.SelectedIndex = Math.Max(0, Array.IndexOf(languages, settings.Language));
        var restart = new ModernButton(Tr("Redémarrer Corral", "Restart Corral")) { Visible = false };
        restart.Click += (_, _) => RestartRequested?.Invoke();
        language.SelectedIndexChanged += (_, _) =>
        {
            settings.Language = languages[language.SelectedIndex];
            SaveSettings();
            restart.Visible = true;
        };
        tips.SetToolTip(restart, Tr("La langue change au redémarrage de Corral. Vos réglages sont conservés.", "The language changes when Corral restarts. Your settings are kept."));
        trayCpuSwitch.Checked = settings.TrayCpuIcon;
        trayCpuSwitch.CheckedChanged += (_, _) => SetTrayCpuIcon(trayCpuSwitch.Checked);
        overlaySwitch.Checked = settings.Overlay.Enabled;
        overlaySwitch.CheckedChanged += (_, _) => SetOverlayEnabled(overlaySwitch.Checked);
        var languageRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        languageRow.Controls.Add(restart);
        languageRow.Controls.Add(language);

        var stack = new CardStack();
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Thème", "Theme"), Tr("« Système » suit le mode clair ou sombre de Windows.", "“System” follows Windows' light or dark mode."), themeChoice),
            SettingRow(Tr("Langue", "Language"), Tr("« Automatique » suit la langue de Windows si Corral la connaît, sinon l'anglais.", "“Automatic” follows the Windows language if Corral knows it, otherwise English."), languageRow),
            SettingRow(Tr("Charge du processeur dans l'icône", "CPU load in the tray icon"),
                Tr("L'icône près de l'horloge affiche le pourcentage du processeur : vert, orange au-delà de 60 %, rouge au-delà de 85 %.", "The icon near the clock shows the CPU percentage: green, orange above 60 %, red above 85 %."), trayCpuSwitch),
            SettingRow(Tr("Mini-fenêtre toujours visible", "Always-on-top mini window"),
                Tr("Petite fenêtre au-dessus de tout, même d'un jeu en mode fenêtré, avec le processeur et la mémoire. Déplacez-la à la souris, clic droit pour la masquer.",
                    "Small window above everything, even a windowed game, with CPU and memory. Drag it with the mouse, right-click to hide it."), overlaySwitch)), Tr("Affichage", "Display")));
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Lancer Corral à l'ouverture de session", "Start Corral when I sign in"), Tr("Démarre réduit dans la zone de notification, avec les droits administrateur.", "Starts minimized in the notification area, with administrator rights."), autoStart),
            SettingRow(Tr("Afficher Corral dans le menu Démarrer", "Show Corral in the Start menu"), Tr("Pour le retrouver avec la recherche Windows. Le raccourci suit l'exe s'il est déplacé.", "To find it with Windows search. The shortcut follows the exe if it is moved."), startMenu)), Tr("Démarrage", "Startup")));
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Rechercher les mises à jour automatiquement", "Check for updates automatically"), Tr("Au démarrage, puis toutes les 6 heures.", "At startup, then every 6 hours."), updates),
            SettingRow($"Version {Updater.CurrentVersion.ToString(3)}",
                Updater.IsSupported ? Tr("Mises à jour publiées sur GitHub.", "Updates published on GitHub.") : Tr($"Mises à jour indisponibles : {Updater.UnsupportedReason}.", $"Updates unavailable: {Updater.UnsupportedReason}."), check)), Tr("Mises à jour", "Updates")));
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Message d'accueil", "Welcome message"), Tr("Les trois choses à savoir pour bien démarrer.", "The three things to know to get started."), showWelcome),
            SettingRow(Tr("Un bug, une idée ?", "A bug, an idea?"), Tr("Ouvre la page de signalement sur GitHub, avec votre version de Corral et de Windows déjà remplies.", "Opens the report page on GitHub, with your Corral and Windows versions already filled in."), report),
            shortcuts), Tr("Aide", "Help")));
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Mode Jeu", "Game Mode"), Tr("Activer ou désactiver le Mode Jeu.", "Turn Game Mode on or off."), HotkeyField(h => h.GameMode, (h, v) => h.GameMode = v)),
            SettingRow("Pause", Tr("Suspendre ou reprendre toutes les règles et ProBalance.", "Suspend or resume all rules and ProBalance."), HotkeyField(h => h.Pause, (h, v) => h.Pause = v)),
            SettingRow(Tr("Afficher Corral", "Show Corral"), Tr("Ouvrir la fenêtre de Corral.", "Open the Corral window."), HotkeyField(h => h.ShowWindow, (h, v) => h.ShowWindow = v)),
            hotkeyStatus), Tr("Raccourcis clavier globaux", "Global keyboard shortcuts")));
        SetHotkeyStatus(Array.Empty<string>()); // corrigé par l'icône de notification après l'enregistrement réel
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Configuration et journal", "Settings and log"), store.ConfigDirectory, openFolder),
            new Label
            {
                AutoSize = true,
                Tag = Theme.HintTag,
                MaximumSize = new Size(640, 0),
                Margin = new Padding(0, 8, 0, 0),
                Text = Tr("Fermer la fenêtre laisse Corral tourner dans la zone de notification. « Quitter » dans son menu l'arrête et " +
                          "restaure les priorités, affinités et le plan d'alimentation d'origine.",
                    "Closing the window keeps Corral running in the notification area. “Exit” in its menu stops it and " +
                    "restores the original priorities, affinities and power plan."),
            }), Tr("Données", "Data")));
        return MakePage("Options", Tr("Apparence, démarrage, mises à jour et aide.", "Appearance, startup, updates and help."), stack);
    }

    readonly ToggleSwitch trayCpuSwitch = new();
    readonly ToggleSwitch overlaySwitch = new();

    /// <summary>Icône CPU ou mini-fenêtre activée/désactivée (l'icône de notification applique).</summary>
    public event Action? DisplayChanged;

    /// <summary>« Redémarrer Corral » après un changement de langue.</summary>
    public event Action? RestartRequested;

    public void SetOverlayEnabled(bool enabled)
    {
        if (overlaySwitch.Checked != enabled)
            overlaySwitch.Checked = enabled; // rappelle cette méthode
        if (settings.Overlay.Enabled == enabled)
            return;
        settings.Overlay.Enabled = enabled;
        SaveSettings();
        DisplayChanged?.Invoke();
    }

    public void SetTrayCpuIcon(bool enabled)
    {
        if (trayCpuSwitch.Checked != enabled)
            trayCpuSwitch.Checked = enabled;
        if (settings.TrayCpuIcon == enabled)
            return;
        settings.TrayCpuIcon = enabled;
        SaveSettings();
        DisplayChanged?.Invoke();
    }

    /// <summary>Enregistre la position de la mini-fenêtre.</summary>
    public void SaveOverlayPosition() => SaveSettings();

    HotkeyBox HotkeyField(Func<HotkeySettings, int?> get, Action<HotkeySettings, int?> set)
    {
        var box = new HotkeyBox { Value = get(settings.Hotkeys) is { } k ? (Keys)k : null };
        tips.SetToolTip(box, Tr("Cliquez puis appuyez sur la combinaison (Ctrl ou Alt + une touche). Retour arrière pour aucun.", "Click, then press the combination (Ctrl or Alt + a key). Backspace for none."));
        box.ValueChanged += (_, _) =>
        {
            set(settings.Hotkeys, box.Value is { } v ? (int)v : null);
            SaveSettings();
            HotkeysChanged?.Invoke();
        };
        return box;
    }

    void ToggleAutoStart()
    {
        if (updatingAutoStart)
            return;
        try
        {
            if (autoStart.Checked)
                AutoStart.Enable(Environment.ProcessPath!);
            else
                AutoStart.Disable();
        }
        catch (Exception ex)
        {
            Log.Error("Démarrage automatique", ex);
            MessageBox.Show(this, Tr("Échec : ", "Failed: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            updatingAutoStart = true;
            autoStart.Checked = !autoStart.Checked;
            updatingAutoStart = false;
        }
    }

    void OpenConfigFolder()
    {
        try
        {
            Directory.CreateDirectory(store.ConfigDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{store.ConfigDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Ouverture du dossier", ex);
        }
    }
}
