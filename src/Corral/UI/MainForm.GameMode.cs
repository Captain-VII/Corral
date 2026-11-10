using Corral.Core;
using Corral.Models;

namespace Corral.UI;

// Page « Mode Jeu ».
public sealed partial class MainForm
{
    readonly Label gameState = new() { AutoSize = true, Font = Ui.Section, Margin = new Padding(0, 2, 0, 0) };
    readonly Label gameDetail = new() { AutoSize = true, Tag = Theme.HintTag, MaximumSize = new Size(620, 0), Margin = new Padding(0, 4, 0, 0) };
    readonly Label gameList = new() { AutoSize = true, Tag = Theme.HintTag, MaximumSize = new Size(520, 0), Margin = new Padding(0, 2, 0, 0) };
    readonly ModernButton gameToggle = new(Tr("Activer maintenant", "Turn on now"));
    readonly ToggleSwitch gmAuto = new();
    readonly ToggleSwitch gmReactive = new();
    readonly ToggleSwitch gmLower = new();
    readonly ToggleSwitch gmNotify = new();
    readonly ComboBox gmPlan = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    readonly TextBox gmApps = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 110, Dock = DockStyle.Fill, AccessibleName = Tr("Programmes de fond calmés par le Mode Jeu, un par ligne", "Background programs calmed by Game Mode, one per line") };
    readonly List<Guid> gmPlanIds = new();
    (bool, bool, string?, bool)? lastGameState;

    Control BuildGamePage()
    {
        gameToggle.Click += (_, _) => OnGameButton();
        var addGame = new ModernButton(Tr("Ajouter un jeu…", "Add a game…"));
        addGame.Click += (_, _) => AddRule(new Rule { IsGame = true, Priority = System.Diagnostics.ProcessPriorityClass.High });
        tips.SetToolTip(addGame, Tr("Crée une règle marquée « C'est un jeu » : le Mode Jeu s'activera quand ce programme tourne.", "Creates a rule marked “This is a game”: Game Mode will turn on while this program runs."));

        var status = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        status.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var texts = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill, Margin = new Padding(0) };
        texts.Controls.Add(gameState);
        texts.Controls.Add(gameDetail);
        gameToggle.Anchor = AnchorStyles.Right;
        status.Controls.Add(texts, 0, 0);
        status.Controls.Add(gameToggle, 1, 0);

        var apps = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        apps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        apps.Controls.Add(SettingRow(Tr("Calmer les programmes de fond", "Calm background programs"),
            Tr("Priorité basse et mode efficacité pour les programmes ci-dessous (navigateurs, synchronisation…), sauf s'ils ont leur propre règle.", "Low priority and efficiency mode for the programs below (browsers, sync…), unless they have their own rule."), gmLower));
        apps.Controls.Add(gmApps);

        var stack = new CardStack();
        stack.Controls.Add(new Card(status, Tr("État", "Status")));
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Activer automatiquement quand un jeu tourne", "Turn on automatically when a game runs"),
                Tr("Un programme est un jeu si sa règle est marquée « C'est un jeu » (le modèle Jeu le fait).", "A program is a game if its rule is marked “This is a game” (the Game template does it)."), gmAuto),
            SettingRow(Tr("Jeux reconnus", "Known games"), null, addGame),
            gameList), Tr("Déclenchement", "Trigger")));
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Plan d'alimentation", "Power plan"), Tr("Activé pendant le Mode Jeu, puis le plan d'origine revient.", "Active during Game Mode, then the original plan comes back."), gmPlan),
            SettingRow(Tr("ProBalance réactif", "Responsive ProBalance"), Tr("Utilise le préréglage Réactif pour que le jeu garde la main.", "Uses the Responsive preset so the game stays in control."), gmReactive),
            SettingRow(Tr("Me prévenir", "Notify me"), Tr("Une bulle à l'activation et à la désactivation.", "A balloon when it turns on and off."), gmNotify)), Tr("Pendant le Mode Jeu", "During Game Mode")));
        stack.Controls.Add(new Card(apps, Tr("Programmes de fond", "Background programs")));

        var save = new ModernButton(Tr("Enregistrer", "Save"), primary: true);
        save.Click += (_, _) => ApplyGameSettings();
        tips.SetToolTip(gameToggle, Tr("Activer ou désactiver le Mode Jeu manuellement (aussi depuis la barre latérale et l'icône de notification)", "Turn Game Mode on or off manually (also from the sidebar and the notification icon)"));
        LoadGameSettings();
        UpdateGameStatus(new EngineSnapshot(0, false, Array.Empty<ProcessRow>()));
        return MakePage(Tr("Mode Jeu", "Game Mode"), Tr("Tout pour le jeu, le temps de jouer : plan Performances, ProBalance réactif et programmes de fond calmés. Tout revient à la normale ensuite.",
                "Everything for the game while you play: Performance plan, responsive ProBalance and calmed background programs. Everything returns to normal afterwards."),
            stack, save);
    }

    void LoadGameSettings()
    {
        var gm = settings.GameMode;
        gmAuto.Checked = gm.Automatic;
        gmReactive.Checked = gm.ReactiveProBalance;
        gmLower.Checked = gm.LowerBackground;
        gmNotify.Checked = gm.Notify;
        gmApps.Text = string.Join(Environment.NewLine, gm.BackgroundApps);

        gmPlan.Items.Clear();
        gmPlanIds.Clear();
        gmPlan.Items.Add(Tr("(inchangé)", "(unchanged)"));
        gmPlanIds.Add(Guid.Empty);
        foreach (var p in plans ??= PowerCfg.List())
        {
            gmPlan.Items.Add(p.Name);
            gmPlanIds.Add(p.Id);
        }
        gmPlan.SelectedIndex = Math.Max(0, gmPlanIds.IndexOf(gm.PowerPlan ?? Guid.Empty));
        UpdateGameList();
    }

    void UpdateGameList()
    {
        var games = settings.Rules.Where(r => r.IsGame).Select(r => r.Pattern + (r.Enabled ? "" : Tr(" (règle inactive)", " (rule disabled)"))).ToList();
        gameList.Text = games.Count == 0
            ? Tr("Aucun jeu pour l'instant. Utilisez « Ajouter un jeu… » ou le modèle Jeu d'une règle.", "No games yet. Use “Add a game…” or the Game template of a rule.")
            : string.Join(", ", games);
    }

    void ApplyGameSettings()
    {
        var gm = settings.GameMode;
        gm.Automatic = gmAuto.Checked;
        gm.ReactiveProBalance = gmReactive.Checked;
        gm.LowerBackground = gmLower.Checked;
        gm.Notify = gmNotify.Checked;
        gm.PowerPlan = gmPlanIds[Math.Max(0, gmPlan.SelectedIndex)];
        gm.BackgroundApps = gmApps.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SaveAndApply();
        LoadGameSettings();
    }

    /// <summary>Bouton Mode Jeu (barre latérale, page, icône de notification).</summary>
    /// <param name="quiet">Depuis un raccourci clavier (peut-être en plein jeu) : pas de boîte de dialogue,
    /// le message est renvoyé pour être affiché en bulle.</param>
    public string? ToggleGameMode(bool quiet = false)
    {
        if (Policies.StandardUser)
        {
            var refused = Tr("Le Mode Jeu est réservé aux administrateurs de ce PC.", "Game Mode is restricted to administrators of this PC.");
            if (!quiet)
                MessageBox.Show(this, refused, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return refused;
        }
        var snap = latestSnapshot;
        if (snap?.GameMode == true && !engine.GameModeManual)
        {
            var message = Tr($"Le Mode Jeu est actif automatiquement parce que « {snap.GameTrigger} » tourne. Il se désactivera tout seul à sa fermeture.",
                $"Game Mode is on automatically because “{snap.GameTrigger}” is running. It will turn off by itself when it closes.");
            if (!quiet)
                MessageBox.Show(this, message + Tr("\n\nPour ne plus le déclencher automatiquement, désactivez l'option dans la page Mode Jeu.", "\n\nTo stop triggering it automatically, turn off the option on the Game Mode page."),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return message;
        }
        bool enable = !engine.GameModeManual;
        engine.SetGameMode(enable);
        return enable ? Tr("Mode Jeu activé.", "Game Mode on.") : Tr("Mode Jeu désactivé.", "Game Mode off.");
    }

    void OnGameButton() => ToggleGameMode(quiet: false);

    void UpdateGameStatus(EngineSnapshot snap)
    {
        var key = (snap.GameMode, engine.GameModeManual, snap.GameTrigger, settings.GameMode.Automatic);
        if (lastGameState == key)
            return;
        lastGameState = key;
        gameState.Text = snap.GameMode ? Tr("Mode Jeu actif", "Game Mode on") : Tr("Mode Jeu inactif", "Game Mode off");
        gameState.ForeColor = snap.GameMode ? Theme.Current.Accent : Theme.Current.Fore;
        gameDetail.Text = snap.GameMode
            ? (snap.GameTrigger != null ? Tr($"Déclenché par « {snap.GameTrigger} » : il se désactivera à sa fermeture.", $"Triggered by “{snap.GameTrigger}”: it will turn off when it closes.") : Tr("Activé manuellement.", "Turned on manually."))
            : settings.GameMode.Automatic ? Tr("Il s'activera tout seul au lancement d'un jeu reconnu.", "It will turn on by itself when a known game starts.") : Tr("Activation manuelle uniquement.", "Manual activation only.");
        gameToggle.Text = snap.GameMode && engine.GameModeManual ? Tr("Désactiver", "Turn off") : Tr("Activer maintenant", "Turn on now");
        gameToggle.Enabled = !(snap.GameMode && !engine.GameModeManual) && !Policies.StandardUser;
    }
}
