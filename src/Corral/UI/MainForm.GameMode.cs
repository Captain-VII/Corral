using Corral.Core;
using Corral.Models;

namespace Corral.UI;

// Page « Mode Jeu ».
public sealed partial class MainForm
{
    readonly Label gameState = new() { AutoSize = true, Font = Ui.Title, Margin = new Padding(0, 2, 0, 0) };
    readonly Label gameEffects = new() { AutoSize = true, MaximumSize = new Size(620, 0), Margin = new Padding(0, 6, 0, 0) };
    readonly MiniChart[] gameCharts =
    {
        new() { Title = Tr("Processeur", "CPU"), Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0) },
        new() { Title = Tr("Carte graphique", "GPU"), Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0) },
        new() { Title = Tr("Mémoire", "Memory"), Dock = DockStyle.Fill, Margin = new Padding(0) },
    };
    readonly TopList gameSessionList = new() { EmptyText = Tr("Aucune session pour l'instant : elles s'ajoutent ici à chaque partie.", "No session yet: they show up here after each game.") };
    readonly GameSessions gameSessions;
    Card gameChartsCard = null!;
    readonly Label gameDetail = new() { AutoSize = true, Tag = Theme.HintTag, MaximumSize = new Size(620, 0), Margin = new Padding(0, 4, 0, 0) };
    readonly Label gameList = new() { AutoSize = true, Tag = Theme.HintTag, MaximumSize = new Size(520, 0), Margin = new Padding(0, 2, 0, 0) };
    readonly ModernButton gameToggle = new(Tr("Activer maintenant", "Turn on now"));
    readonly ToggleSwitch gmAuto = new();
    readonly ToggleSwitch gmDetect = new();
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
        texts.Controls.Add(gameEffects);
        gameToggle.Anchor = AnchorStyles.Right;
        status.Controls.Add(texts, 0, 0);
        status.Controls.Add(gameToggle, 1, 0);

        var apps = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        apps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        apps.Controls.Add(SettingRow(Tr("Calmer les programmes de fond", "Calm background programs"),
            Tr("Priorité basse et mode efficacité pour les programmes ci-dessous (navigateurs, synchronisation…), sauf s'ils ont leur propre règle.", "Low priority and efficiency mode for the programs below (browsers, sync…), unless they have their own rule."), gmLower));
        apps.Controls.Add(gmApps);

        // La session en cours d'abord (jeu, effets, charge du PC), l'historique, puis les réglages
        var charts = new TableLayoutPanel { ColumnCount = 3, RowCount = 1, Height = 110, Margin = new Padding(0) };
        for (int i = 0; i < 3; i++)
            charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        charts.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        charts.Controls.AddRange(gameCharts);
        var statusCard = new Card(status, Tr("État", "Status"));
        gameChartsCard = new Card(charts, Tr("Le PC pendant la session", "The PC during the session"));
        var stack = new CardStack();
        stack.Wide.UnionWith(new[] { statusCard, gameChartsCard });
        stack.Controls.Add(statusCard);
        stack.Controls.Add(gameChartsCard);
        stack.Controls.Add(new Card(gameSessionList, Tr("Sessions récentes", "Recent sessions")));
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Activer automatiquement quand un jeu tourne", "Turn on automatically when a game runs"),
                Tr("Pour un jeu reconnu (ci-dessous) ou un programme dont la règle est marquée « C'est un jeu » (le modèle Jeu le fait).", "For a recognized game (below) or a program whose rule is marked “This is a game” (the Game template does it)."), gmAuto),
            SettingRow(Tr("Reconnaître les jeux installés", "Recognize installed games"),
                Tr("Jeux de Steam, Epic, GOG, EA, Ubisoft, Xbox, Riot… et ceux que Windows reconnaît, dès qu'ils passent au premier plan. Un programme qui a sa propre règle n'est un jeu que si elle est marquée « C'est un jeu ».",
                    "Steam, Epic, GOG, EA, Ubisoft, Xbox, Riot… games and those Windows recognizes, once they come to the foreground. A program with its own rule is a game only if that rule is marked “This is a game”."), gmDetect),
            SettingRow(Tr("Jeux ajoutés", "Added games"), null, addGame),
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
        gmDetect.Checked = gm.DetectGames;
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
            ? Tr("Aucun jeu ajouté. Les jeux installés sont reconnus tout seuls ; « Ajouter un jeu… » sert pour les autres.", "No games added. Installed games are recognized on their own; use “Add a game…” for the others.")
            : string.Join(", ", games);
    }

    void ApplyGameSettings()
    {
        var gm = settings.GameMode;
        gm.Automatic = gmAuto.Checked;
        gm.DetectGames = gmDetect.Checked;
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

    /// <summary>Session en cours : nom du jeu, durée, effets, courbes du PC depuis son début ; historique des sessions.</summary>
    void RefreshGameSession(EngineSnapshot snap)
    {
        var current = gameSessions.Current;
        var gm = settings.GameMode;
        if (snap.GameMode && current is { } c)
        {
            gameState.Text = c.Name.Length > 0 ? c.Name : Tr("Mode Jeu actif", "Game Mode on");
            var effects = new List<string> { Tr($"actif depuis {Duration(DateTime.Now - c.Since)}", $"on for {Duration(DateTime.Now - c.Since)}") };
            if (gm.PowerPlan is { } plan && plan != Guid.Empty)
                effects.Add(Tr("plan ", "plan ") + PlanName(plan));
            if (gm.ReactiveProBalance)
                effects.Add(Tr("ProBalance réactif", "responsive ProBalance"));
            if (gm.LowerBackground)
            {
                int calmed = snap.Rows.Count(r => r.Rule == null && gm.BackgroundApps.Any(a => RuleMatcher.Matches(a, r.Name)));
                effects.Add(Tr($"{calmed} programme(s) de fond calmé(s)", $"{calmed} background program(s) calmed"));
            }
            gameEffects.Text = string.Join("  ·  ", effects);
            gameEffects.ForeColor = Theme.Current.Accent;
        }
        else
            gameEffects.Text = "";
        // Courbes : depuis le début de la session (15 min au plus), sinon les 5 dernières minutes
        var range = current is { } s && snap.GameMode ? TimeSpan.FromMinutes(Math.Clamp((DateTime.Now - s.Since).TotalMinutes, 1, 15)) : TimeSpan.FromMinutes(5);
        var from = DateTime.UtcNow - range;
        foreach (var (chart, history) in gameCharts.Zip(new[] { cpuHistory, gpuHistory, memHistory }))
        {
            chart.Range = range;
            chart.SetData(history.Since(from));
        }
        gameChartsCard.Title = snap.GameMode ? Tr("Le PC pendant la session", "The PC during the session") : Tr("Le PC ces 5 dernières minutes", "The PC over the last 5 minutes");
        gameSessionList.SetItems(gameSessions.Recent().Take(8).Select(x => (x.Name, x.Duration.TotalMinutes,
            $"{Duration(x.Duration)} · {x.Start:dd/MM HH:mm}")).ToList());
    }

    static string Duration(TimeSpan d) => d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes:00}" : $"{Math.Max(1, (int)d.TotalMinutes)} min";
}
