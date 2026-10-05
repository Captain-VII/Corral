using System.Collections;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using Corral.Core;
using Corral.Models;

namespace Corral.UI;

public sealed class MainForm : Form
{
    readonly Engine engine;
    readonly RuleStore store;
    readonly Settings settings; // copie détenue par l'interface ; le moteur reçoit des clones

    readonly NavBar nav = new();
    readonly Panel pageHost = new() { Dock = DockStyle.Fill };
    readonly List<(string Title, Control Page)> pages = new();
    readonly ToolTip tips = Theme.CreateToolTip();

    // Processus
    readonly BufferedListView procList = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true };
    readonly Dictionary<string, ListViewItem> procItems = new();
    readonly ProcessSorter sorter = new();
    readonly TextBox search = new() { PlaceholderText = Tr("Rechercher un processus", "Search processes"), Width = 240, Margin = new Padding(4, 5, 8, 0) };
    readonly ModernButton createRule = new(Tr("Créer une règle", "Create a rule")) { Enabled = false };
    readonly ModernButton detailsButton = new(Tr("Détails", "Details")) { Enabled = false };
    readonly UndoBanner processBanner = new();
    readonly Dictionary<string, Image> iconCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Image genericIcon = ScaleIcon(SystemIcons.Application);
    Panel? welcome;
    EngineSnapshot? lastSnapshot;        // dernier affiché (thread de l'interface)
    volatile EngineSnapshot? latestSnapshot; // dernier reçu, même fenêtre cachée

    // Graphique
    readonly CpuHistory cpuHistory = new(TimeSpan.FromMinutes(15));
    readonly CpuChart chart;
    readonly CpuHistory memHistory = new(TimeSpan.FromMinutes(15));
    readonly CpuChart memChart;
    readonly TopTracker topTracker = new(TimeSpan.FromMinutes(15));
    readonly ProcessHistory processHistory = new(TimeSpan.FromMinutes(5));
    readonly TopList topList = new();
    TopTracker.Metric topMetric = TopTracker.Metric.Cpu;
    readonly ProBalanceStats stats;
    int snapshotCount;

    // Règles
    readonly BufferedListView ruleList = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    readonly Label rulesEmpty = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Tag = Theme.HintTag,
        Text = Tr("Aucune règle pour l'instant.\nCliquez sur « Ajouter une règle », ou double-cliquez sur un processus.",
            "No rules yet.\nClick “Add a rule”, or double-click a process."),
    };
    readonly ModernButton editRule = new(Tr("Modifier", "Edit")) { Enabled = false };
    readonly ModernButton deleteRule = new(Tr("Supprimer", "Delete")) { Enabled = false };
    readonly ModernButton moveUp = new("↑") { Enabled = false };
    readonly ModernButton moveDown = new("↓") { Enabled = false };
    readonly UndoBanner rulesBanner = new();
    IReadOnlyList<PowerPlanInfo>? plans;

    // ProBalance
    readonly ToggleSwitch pbEnabled = new();
    readonly ToggleSwitch pbNotify = new();
    readonly NumericUpDown pbSystem = Num(10, 100);
    readonly NumericUpDown pbProcess = Num(1, 100);
    readonly NumericUpDown pbRestore = Num(0, 100);
    readonly NumericUpDown pbTrigger = Num(1, 60);
    readonly NumericUpDown pbRestoreSec = Num(1, 60);
    readonly TextBox pbExclusions = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 110, Dock = DockStyle.Fill };
    readonly List<(ModernButton Button, ProBalanceSettings.Preset Preset)> presetButtons = new();
    readonly Label presetState = new() { AutoSize = true, Tag = Theme.HintTag, Margin = new Padding(8, 8, 0, 0) };

    // Options
    readonly ComboBox themeChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    readonly ToggleSwitch autoStart = new();
    bool updatingAutoStart;

    // Journal
    readonly BufferedListView logList = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true };
    readonly List<(ModernButton Button, Func<LogEntry, bool> Filter)> logFilters = new();
    Func<LogEntry, bool> logFilter = _ => true;
    LogEntry? lastLogShown;

    volatile bool shown;
    bool allowClose;
    bool loading = true;

    /// <summary>« Vérifier maintenant » : traité par l'icône de notification, qui gère les mises à jour.</summary>
    public event EventHandler? CheckUpdatesRequested;

    /// <summary>Bouton Pause de la barre latérale ; l'icône de notification garde l'état de référence.</summary>
    public event Action<bool>? PauseRequested;

    /// <summary>Les raccourcis globaux ont changé : l'icône de notification les réenregistre.</summary>
    public event Action? HotkeysChanged;

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

    public MainForm(Engine engine, RuleStore store, Settings settings)
    {
        this.engine = engine;
        this.store = store;
        this.settings = settings;
        Profiles.Ensure(settings, Tr("Principal", "Main"));

        Text = "Corral";
        Font = Ui.Base;
        Icon = AppIcon.Load(new Size(32, 32));
        Size = new Size(1120, 760);
        MinimumSize = new Size(880, 690);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        chart = new CpuChart(cpuHistory);
        memChart = new CpuChart(memHistory);
        stats = new ProBalanceStats(Path.Combine(store.ConfigDirectory, "stats.json"));
        // Interventions comptées même fenêtre cachée (événement levé sur le thread du moteur)
        engine.ProBalanceActed += (names, _) =>
        {
            stats.Record(names);
            if (shown && IsHandleCreated)
                BeginInvoke(RefreshStats);
        };
        // Icônes : codes communs à Segoe Fluent Icons et Segoe MDL2 Assets
        AddPage("", "Processus", BuildProcessPage());
        AddPage("", "Graphique", BuildChartPage());
        AddPage("", "Règles", BuildRulesPage());
        AddPage("", "ProBalance", BuildProBalancePage());
        AddPage("", "Mode Jeu", BuildGamePage());
        AddPage("\uE945", "Optimisations", BuildOptimizationPage());
        AddPage("", "Démarrage", BuildStartupPage());
        AddPage("", "Options", BuildOptionsPage());
        AddPage("", "Journal", BuildLogPage());
        nav.SelectedChanged += SelectPage;
        nav.PauseRequested += paused => PauseRequested?.Invoke(paused);
        nav.GameRequested += OnGameButton;
        tips.SetToolTip(nav.GameButton, Tr("Plan Performances, ProBalance réactif et programmes de fond calmés le temps de jouer", "Performance plan, responsive ProBalance and background programs calmed while you play"));

        Controls.Add(pageHost);
        Controls.Add(nav);

        VisibleChanged += (_, _) =>
        {
            shown = Visible;
            if (!Visible)
                SaveWindow();
        };
        engine.SnapshotReady += OnSnapshot;
        Theme.Changed += OnThemeChanged;
        RefreshRules();
        LoadProBalance();
        RestoreWindow();
        Theme.Apply(this);
        loading = false;
    }

    /// <summary>Historique CPU affiché par l'onglet Graphique (exposé pour les captures de test).</summary>
    public CpuHistory History => cpuHistory;
    public CpuHistory MemoryHistory => memHistory;
    public TopTracker TopUsage => topTracker;

    public void ShowPage(string title)
    {
        int i = pages.FindIndex(x => x.Title == title);
        if (i >= 0)
            nav.Selected = i;
    }

    void AddPage(string glyph, string title, Control page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        pageHost.Controls.Add(page);
        pages.Add((title, page));
        nav.Add(glyph, PageLabel(title));
    }

    void SelectPage(int index)
    {
        for (int i = 0; i < pages.Count; i++)
            pages[i].Page.Visible = i == index;
        settings.Window.LastPage = pages[index].Title;
        if (pages[index].Title == "Journal")
            RefreshLog(force: true);
        else if (pages[index].Title == "Démarrage")
            RefreshStartup();
        if (pages[index].Page.Contains(chart))
            chart.Invalidate();
    }

    /// <summary>Nom affiché d'une page (les titres français servent d'identifiants).</summary>
    static string PageLabel(string id) => !English ? id : id switch
    {
        "Processus" => "Processes",
        "Graphique" => "Charts",
        "Règles" => "Rules",
        "Mode Jeu" => "Game Mode",
        "Optimisations" => "Optimizations",
        "Démarrage" => "Startup",
        "Journal" => "Log",
        _ => id,
    };

    bool IsPageVisible(string title) => pages.FirstOrDefault(p => p.Title == title).Page?.Visible == true;

    /// <summary>Page type : titre, phrase d'explication, actions à droite, contenu dessous.</summary>
    static Panel MakePage(string title, string description, Control content, params Control[] actions)
    {
        var page = new Panel { Padding = new Padding(28, 22, 28, 22) };
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 0, 0, 16) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label { Text = title, Font = Ui.Title, AutoSize = true, Margin = new Padding(0) }, 0, 0);
        header.Controls.Add(new Label { Text = description, AutoSize = true, Tag = Theme.HintTag, MaximumSize = new Size(560, 0), Margin = new Padding(2, 4, 0, 0) }, 0, 1);
        var bar = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Top | AnchorStyles.Right, Margin = new Padding(0, 6, 0, 0) };
        bar.Controls.AddRange(actions);
        header.Controls.Add(bar, 1, 0);
        header.SetRowSpan(bar, 2);

        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        page.Controls.Add(header);
        return page;
    }

    /// <summary>Ligne de réglage : libellé (et description) à gauche, contrôle à droite.</summary>
    static Control SettingRow(string label, string? description, Control control)
    {
        var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 8) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var text = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill, Margin = new Padding(0) };
        text.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 2, 0, 0) });
        if (description != null)
            text.Controls.Add(new Label { Text = description, AutoSize = true, Tag = Theme.HintTag, MaximumSize = new Size(520, 0), Margin = new Padding(0, 2, 0, 0) });
        control.Anchor = AnchorStyles.Right;
        row.Controls.Add(text, 0, 0);
        row.Controls.Add(control, 1, 0);
        return row;
    }

    static TableLayoutPanel Rows(params Control[] rows)
    {
        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var r in rows)
            t.Controls.Add(r);
        return t;
    }

    /// <summary>Champ numérique suivi de son unité.</summary>
    static Control WithUnit(NumericUpDown n, string unit)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        f.Controls.Add(n);
        f.Controls.Add(new Label { Text = unit, AutoSize = true, Tag = Theme.HintTag, Margin = new Padding(4, 6, 0, 0) });
        return f;
    }

    void OnThemeChanged()
    {
        if (IsDisposed)
            return;
        if (InvokeRequired)
        {
            BeginInvoke(OnThemeChanged);
            return;
        }
        Theme.Apply(this);
        Invalidate(true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChanged; // événement statique : éviter une fuite
            tips.Dispose();
            foreach (var img in iconCache.Values)
                img.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Fermer la fenêtre la cache seulement ; on quitte via l'icône de notification.</summary>
    public void CloseForReal()
    {
        allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!allowClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        SaveWindow();
        engine.SnapshotReady -= OnSnapshot;
        base.OnFormClosing(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        updatingAutoStart = true;
        try { autoStart.Checked = AutoStart.IsEnabled(); }
        catch (Exception ex) { Log.Error("Lecture du démarrage automatique", ex); }
        finally { updatingAutoStart = false; }
    }

    // ---------- Fenêtre : position, taille, dernière page ----------

    void RestoreWindow()
    {
        var w = settings.Window;
        if (w.HasBounds)
        {
            var bounds = new Rectangle(w.X, w.Y, Math.Max(w.Width, MinimumSize.Width), Math.Max(w.Height, MinimumSize.Height));
            // Seulement si la fenêtre reste visible (écran débranché depuis, par exemple)
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(bounds.X, bounds.Y, bounds.Width, 40))))
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = bounds;
            }
            if (w.Maximized)
                WindowState = FormWindowState.Maximized;
        }
        int page = pages.FindIndex(p => p.Title == w.LastPage);
        nav.Selected = page >= 0 ? page : 0;
    }

    void SaveWindow()
    {
        if (loading || WindowState == FormWindowState.Minimized)
            return;
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        var w = settings.Window;
        (w.X, w.Y, w.Width, w.Height, w.Maximized) = (b.X, b.Y, b.Width, b.Height, WindowState == FormWindowState.Maximized);
        SaveSettings();
    }

    // ---------- Raccourcis clavier ----------

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.F:
                ShowPage("Processus");
                search.Focus();
                search.SelectAll();
                return true;
            case Keys.Control | Keys.N:
                AddRule(null);
                return true;
            case Keys.F5:
                if (IsPageVisible("Journal"))
                    RefreshLog(force: true);
                else if (IsPageVisible("Démarrage"))
                    RefreshStartup();
                else if (lastSnapshot != null)
                    ApplySnapshot(lastSnapshot);
                return true;
            case Keys.Escape when search.Focused && search.TextLength > 0:
                search.Clear();
                return true;
        }
        if ((keyData & Keys.Control) == Keys.Control)
        {
            int n = (int)(keyData & Keys.KeyCode) - (int)Keys.D1;
            if (n >= 0 && n < pages.Count)
            {
                nav.Selected = n;
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------- Processus ----------

    Control BuildProcessPage()
    {
        procList.Columns.Add(Tr("Nom", "Name"), 200);
        procList.Columns.Add("PID", 70, HorizontalAlignment.Right);
        procList.Columns.Add("CPU", 80, HorizontalAlignment.Right);
        procList.Columns.Add(Tr("Mémoire", "Memory"), 86, HorizontalAlignment.Right);
        procList.Columns.Add(Tr("Disque", "Disk"), 80, HorizontalAlignment.Right);
        procList.Columns.Add("GPU", 60, HorizontalAlignment.Right);
        procList.Columns.Add(Tr("Règle", "Rule"), 120);
        procList.Columns.Add("ProBalance", 100);
        Theme.StyleList(procList, ProcessCell, col => col == sorter.Column ? (sorter.Descending ? SortOrder.Descending : SortOrder.Ascending) : SortOrder.None);
        procList.ColumnClick += (_, e) =>
        {
            if (sorter.Column == e.Column)
                sorter.Descending = !sorter.Descending;
            else
                (sorter.Column, sorter.Descending) = (e.Column, e.Column is >= 2 and <= 5);
            procList.Sort();
            Theme.InvalidateHeader(procList);
        };
        procList.SelectedIndexChanged += (_, _) => createRule.Enabled = SelectedProcess != null;
        procList.DoubleClick += (_, _) => CreateRuleFromSelection();
        procList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { ShowDetails(); e.Handled = true; }
        };
        detailsButton.Click += (_, _) => ShowDetails();
        procList.SelectedIndexChanged += (_, _) => detailsButton.Enabled = SelectedProcess != null;
        tips.SetToolTip(detailsButton, Tr("Fiche du processus sélectionné : éditeur, emplacement, ligne de commande, parent… (Entrée)", "Selected process details: publisher, location, command line, parent… (Enter)"));
        createRule.Click += (_, _) => CreateRuleFromSelection();
        search.TextChanged += (_, _) =>
        {
            if (lastSnapshot != null)
                ApplySnapshot(lastSnapshot);
        };
        tips.SetToolTip(search, Tr("Filtrer par nom (Ctrl+F). Échap pour effacer.", "Filter by name (Ctrl+F). Esc to clear."));
        tips.SetToolTip(createRule, Tr("Régler durablement le processus sélectionné : priorité, cœurs, plan d'alimentation, limites.", "Set lasting options for the selected process: priority, cores, power plan, limits."));
        procList.ContextMenuStrip = BuildProcessMenu();

        var body = new Panel();
        body.Controls.Add(new Card(procList, fill: true) { Dock = DockStyle.Fill });
        if (!settings.WelcomeDismissed)
        {
            var card = BuildWelcome();
            card.Dock = DockStyle.Fill;
            // Enveloppe : la marge sous la carte reste transparente
            welcome = new Panel { Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 14) };
            welcome.Controls.Add(card);
            body.Controls.Add(welcome);
            // Hauteur recalculée à chaque redimensionnement, même page cachée
            // (Visible vaut false tant qu'un parent est caché : on ne s'y fie pas).
            body.Resize += (_, _) =>
            {
                if (body.ClientSize.Width > 0)
                    welcome.Height = card.GetPreferredSize(new Size(body.ClientSize.Width, 0)).Height + 14;
            };
        }
        body.Controls.Add(processBanner);

        return MakePage(Tr("Processus", "Processes"), Tr("Processus en cours, triés par usage du processeur. Double-cliquez sur un processus pour lui créer une règle, clic droit pour plus d'actions.",
            "Running processes, sorted by CPU usage. Double-click a process to create a rule for it, right-click for more actions."),
            body, search, detailsButton, createRule);
    }

    Card BuildWelcome()
    {
        var points = new[]
        {
            ("", Tr("Corral tourne en arrière-plan : fermer cette fenêtre le laisse actif dans la zone de notification, à côté de l'horloge.", "Corral runs in the background: closing this window keeps it active in the notification area, next to the clock.")),
            ("", Tr("Pour régler un programme, double-cliquez dessus ci-dessous. Des modèles prêts à l'emploi (Jeu, Tâche de fond…) remplissent la règle pour vous.", "To tune a program, double-click it below. Ready-made templates (Game, Background task…) fill in the rule for you.")),
            ("", Tr("ProBalance est déjà actif : quand un programme sature le processeur, il le calme un instant pour que le PC reste réactif.", "ProBalance is already on: when a program saturates the CPU, it calms it for a moment so the PC stays responsive.")),
        };
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (glyph, text) in points)
        {
            grid.Controls.Add(new Label { Text = glyph, Font = Ui.Icons, AutoSize = true, Margin = new Padding(2, 4, 0, 6) });
            grid.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(820, 0), Margin = new Padding(0, 3, 0, 6) });
        }
        var ok = new ModernButton(Tr("Compris", "Got it"), primary: true) { Margin = new Padding(0, 8, 0, 0) };
        grid.Controls.Add(new Label { AutoSize = true });
        grid.Controls.Add(ok);
        var card = new Card(grid, Tr("Bienvenue dans Corral", "Welcome to Corral"));
        ok.Click += (_, _) =>
        {
            if (welcome != null)
                welcome.Visible = false;
            settings.WelcomeDismissed = true;
            SaveSettings();
        };
        return card;
    }

    ContextMenuStrip BuildProcessMenu()
    {
        var menu = new ContextMenuStrip();
        var details = new ToolStripMenuItem(Tr("Détails…", "Details…"), null, (_, _) => ShowDetails()) { ToolTipText = Tr("Emplacement, éditeur, ligne de commande, parent… (Entrée)", "Location, publisher, command line, parent… (Enter)") };
        menu.Items.Add(details);
        var create = new ToolStripMenuItem(Tr("Créer une règle…", "Create a rule…"), null, (_, _) => CreateRuleFromSelection());
        var now = new ToolStripMenuItem(Tr("Priorité maintenant", "Priority now"));
        foreach (var (label, value) in RuleDialog.Priorities.Where(p => p.Value != null))
        {
            var prio = value!.Value;
            now.DropDownItems.Add(label, null, (_, _) => SetPriorityNow(prio));
        }
        var exclude = new ToolStripMenuItem(Tr("Exclure de ProBalance", "Exclude from ProBalance"), null, (_, _) => ExcludeFromProBalance());
        var open = new ToolStripMenuItem(Tr("Ouvrir l'emplacement du fichier", "Open file location"), null, (_, _) => OpenProcessLocation());
        var kill = new ToolStripMenuItem(Tr("Terminer le processus…", "End process…"), null, (_, _) => KillSelected());
        now.ToolTipText = Tr("Change la priorité tout de suite, sans créer de règle. Elle sera perdue à la fermeture du programme.", "Changes the priority right away, without creating a rule. It is lost when the program closes.");
        menu.Items.AddRange(new ToolStripItem[] { create, now, new ToolStripSeparator(), exclude, open, new ToolStripSeparator(), kill });
        menu.Opening += (_, e) =>
        {
            var row = SelectedProcess;
            if (row == null)
            {
                e.Cancel = true;
                return;
            }
            bool isProtected = Exclusions.IsProtected(row.Name, row.Pid, Environment.ProcessId);
            now.Enabled = kill.Enabled = !isProtected;
            open.Enabled = row.Path != null;
            bool excluded = settings.ProBalance.Exclusions.Any(x => RuleMatcher.Matches(x, row.Name));
            exclude.Enabled = !isProtected && !excluded;
            exclude.Text = excluded ? Tr("Déjà exclu de ProBalance", "Already excluded from ProBalance") : Tr("Exclure de ProBalance", "Exclude from ProBalance");
        };
        Theme.ApplyTo(menu);
        return menu;
    }

    ProcessRow? SelectedProcess => procList.SelectedItems.Count == 1 ? procList.SelectedItems[0].Tag as ProcessRow : null;

    void SetPriorityNow(System.Diagnostics.ProcessPriorityClass priority)
    {
        if (SelectedProcess is not { } row)
            return;
        var error = engine.SetPriorityOnce(row.Pid, row.Name, priority);
        if (error != null)
            MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else
            processBanner.Show(Tr($"Priorité de « {row.Name} » passée à « {Engine.PriorityLabel(priority)} » jusqu'à sa fermeture.", $"“{row.Name}” priority set to “{Engine.PriorityLabel(priority)}” until it closes."));
    }

    void ExcludeFromProBalance()
    {
        if (SelectedProcess is not { } row)
            return;
        var name = row.Name + ".exe";
        settings.ProBalance.Exclusions.Add(name);
        SaveAndApply();
        LoadProBalance();
        processBanner.Show(Tr($"« {row.Name} » ne sera plus abaissé par ProBalance.", $"“{row.Name}” will no longer be lowered by ProBalance."), () =>
        {
            settings.ProBalance.Exclusions.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            SaveAndApply();
            LoadProBalance();
        });
    }

    void OpenProcessLocation()
    {
        if (SelectedProcess?.Path is not { } path)
            return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Ouverture de l'emplacement", ex);
        }
    }

    void KillSelected()
    {
        if (SelectedProcess is not { } row || Exclusions.IsProtected(row.Name, row.Pid, Environment.ProcessId))
            return;
        if (MessageBox.Show(this, Tr($"Terminer « {row.Name} » (PID {row.Pid}) ?\n\nLe programme se fermera immédiatement : ce qui n'est pas enregistré sera perdu.",
                    $"End “{row.Name}” (PID {row.Pid})?\n\nThe program will close immediately: anything unsaved will be lost."),
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try
        {
            using var p = Process.GetProcessById(row.Pid);
            if (!string.Equals(p.ProcessName, row.Name, StringComparison.OrdinalIgnoreCase))
                return; // PID réutilisé entre-temps
            p.Kill();
            Log.Info(Tr($"{row.Name} ({row.Pid}) terminé par l'utilisateur", $"{row.Name} ({row.Pid}) ended by the user"));
            processBanner.Show(Tr($"« {row.Name} » a été terminé.", $"“{row.Name}” was ended."));
        }
        catch (ArgumentException)
        {
            // déjà fermé
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Tr("Impossible de terminer ce processus : ", "Unable to end this process: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    Theme.CellStyle? ProcessCell(ListViewItem item, int column)
    {
        if (item.Tag is not ProcessRow r)
            return null;
        var p = Theme.Current;
        return column switch
        {
            0 => new Theme.CellStyle(Icon: IconFor(r.Path)),
            // Chaleur : la cellule CPU se teinte avec la charge (comme le Gestionnaire des tâches)
            2 when r.Cpu >= 0.5 => new Theme.CellStyle(Back: Ui.Blend(p.Accent, p.Surface, Math.Min(r.Cpu / 40, 1) * (Theme.IsDark ? 0.55 : 0.35))),
            4 when r.IoBytesPerSec >= 100 << 10 => new Theme.CellStyle(Back: Ui.Blend(p.Accent, p.Surface, Math.Min(r.IoBytesPerSec / (20.0 * (1 << 20)), 1) * (Theme.IsDark ? 0.55 : 0.35))),
            5 when r.Gpu >= 0.5 => new Theme.CellStyle(Back: Ui.Blend(p.Accent, p.Surface, Math.Min(r.Gpu / 40, 1) * (Theme.IsDark ? 0.55 : 0.35))),
            6 when r.Rule != null => new Theme.CellStyle(Fore: p.Accent),
            7 when r.Restrained => new Theme.CellStyle(Text: Tr("▼ abaissé", "▼ lowered"), Fore: p.Warning),
            _ => null,
        };
    }

    /// <summary>Icône 16 px de l'exécutable, extraite au premier affichage puis mise en cache.</summary>
    Image IconFor(string? path)
    {
        if (path == null)
            return genericIcon;
        if (iconCache.TryGetValue(path, out var img))
            return img;
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            img = icon != null ? ScaleIcon(icon) : genericIcon;
        }
        catch
        {
            img = genericIcon;
        }
        iconCache[path] = img;
        return img;
    }

    static Image ScaleIcon(Icon icon)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        using var source = icon.ToBitmap();
        g.DrawImage(source, 0, 0, 16, 16);
        return bmp;
    }

    void ShowDetails()
    {
        if (SelectedProcess is not { } row)
            return;
        ProcessDetails details;
        try
        {
            details = ProcessDetails.Read(row.Pid);
        }
        catch (ArgumentException)
        {
            processBanner.Show(Tr($"« {row.Name} » s'est fermé entre-temps.", $"“{row.Name}” has closed in the meantime."));
            return;
        }
        bool canRule = !Exclusions.IsProtected(row.Name, row.Pid, Environment.ProcessId);
        bool createRuleAfter;
        using (var dlg = new ProcessDetailsDialog(details, row, canRule, processHistory))
        {
            dlg.ShowDialog(this);
            createRuleAfter = dlg.CreateRuleRequested;
        }
        if (createRuleAfter)
            AddRule(new Rule { Pattern = row.Name + ".exe" });
    }

    void CreateRuleFromSelection()
    {
        if (SelectedProcess is { } row)
            AddRule(new Rule { Pattern = row.Name + ".exe" });
    }

    void OnSnapshot(EngineSnapshot snapshot)
    {
        // Historique alimenté même fenêtre cachée ; la toute première mesure (sans référence, donc 0) est ignorée.
        if (Interlocked.Increment(ref snapshotCount) > 1)
        {
            var t = DateTime.UtcNow;
            cpuHistory.Add(t, snapshot.SystemCpu);
            if (snapshot.MemoryTotal > 0)
                memHistory.Add(t, snapshot.MemoryPercent);
            topTracker.Add(t, snapshot.Rows);
            processHistory.Add(t, snapshot.Rows);
        }
        latestSnapshot = snapshot; // à jour même fenêtre cachée (raccourcis, icône de notification)
        if (!shown)
            return;
        try { BeginInvoke(() => ApplySnapshot(snapshot)); }
        catch (InvalidOperationException) { } // fenêtre en cours de fermeture
    }

    void ApplySnapshot(EngineSnapshot snap)
    {
        if (IsDisposed)
            return;
        lastSnapshot = snap;
        nav.SetStatus(snap.SystemCpu, snap.Rows.Count, snap.Paused, snap.GameMode, snap.GameTrigger);
        UpdateGameStatus(snap);
        if (IsPageVisible("Optimisations"))
            optMemNow.Text = snap.MemoryTotal > 0
                ? Tr($"Mémoire utilisée en ce moment : {snap.MemoryPercent:0} % ({FormatBytes(snap.MemoryUsed)} sur {FormatBytes(snap.MemoryTotal)})",
                $"Memory in use right now: {snap.MemoryPercent:0} % ({FormatBytes(snap.MemoryUsed)} of {FormatBytes(snap.MemoryTotal)})")
                : "";
        if (chart.Visible)
        {
            chart.Invalidate();
            memChart.Caption = snap.MemoryTotal > 0 ? Tr($"{FormatBytes(snap.MemoryUsed)} utilisés sur {FormatBytes(snap.MemoryTotal)}", $"{FormatBytes(snap.MemoryUsed)} used of {FormatBytes(snap.MemoryTotal)}") : null;
            memChart.Invalidate();
            RefreshTop();
        }
        if (IsPageVisible("Journal"))
            RefreshLog(force: false);

        var filter = search.Text.Trim();
        procList.BeginUpdate();
        procList.ListViewItemSorter = null; // évite un tri à chaque ajout
        var seen = new HashSet<string>();
        foreach (var r in snap.Rows)
        {
            if (filter.Length > 0 && !r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var key = $"{r.Pid}:{r.Name}";
            seen.Add(key);
            if (!procItems.TryGetValue(key, out var item))
            {
                item = new ListViewItem(Enumerable.Repeat("", 8).ToArray());
                procItems[key] = item;
                procList.Items.Add(item);
                SetText(item, 0, r.Name);
                SetText(item, 1, r.Pid.ToString());
            }
            var old = item.Tag as ProcessRow;
            item.Tag = r;
            SetText(item, 2, $"{r.Cpu:0.0} %");
            SetText(item, 3, $"{r.MemoryBytes / (1024 * 1024):N0} {Units.MB}");
            SetText(item, 4, Units.Rate(r.IoBytesPerSec));
            SetText(item, 5, $"{r.Gpu:0} %");
            SetText(item, 6, r.Rule ?? "");
            SetText(item, 7, r.Restrained ? Tr("abaissé", "lowered") : "");
            var tip = ProcessTip(r);
            if (item.ToolTipText != tip)
                item.ToolTipText = tip;
            if (old != null && old.Restrained != r.Restrained)
                procList.Invalidate(item.Bounds);
        }
        foreach (var key in procItems.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            procList.Items.Remove(procItems[key]);
            procItems.Remove(key);
        }
        procList.ListViewItemSorter = sorter; // déclenche le tri
        procList.EndUpdate();
        Theme.FitColumns(procList);
        createRule.Enabled = SelectedProcess != null;
    }

    static string ProcessTip(ProcessRow r)
    {
        var lines = new List<string> { r.Path ?? Tr($"{r.Name} (emplacement inaccessible : processus protégé)", $"{r.Name} (location unavailable: protected process)") };
        if (r.Rule != null)
            lines.Add(Tr($"Règle « {r.Rule} » : {r.RuleSummary}", $"Rule “{r.Rule}”: {r.RuleSummary}"));
        if (r.Restrained)
            lines.Add(Tr("ProBalance l'a abaissé temporairement : il utilisait beaucoup le processeur pendant que le système était chargé.", "ProBalance lowered it temporarily: it was using a lot of CPU while the system was busy."));
        return string.Join("\n", lines);
    }

    static void SetText(ListViewItem item, int index, string text)
    {
        if (item.SubItems[index].Text != text)
            item.SubItems[index].Text = text;
    }

    List<string> RunningNames() => latestSnapshot?.Rows.Select(r => r.Name).ToList() ?? new();

    // ---------- Graphique ----------

    Control BuildChartPage()
    {
        var ranges = new List<ModernButton>();
        foreach (var minutes in new[] { 1, 5, 15 })
        {
            var b = new ModernButton($"{minutes} min") { Toggled = minutes == 5 };
            b.Click += (_, _) =>
            {
                foreach (var other in ranges)
                    other.Toggled = other == b;
                chart.Range = memChart.Range = TimeSpan.FromMinutes(minutes);
                chart.Invalidate();
                memChart.Invalidate();
                RefreshTop();
            };
            tips.SetToolTip(b, Tr($"Afficher les {minutes} dernières minutes", $"Show the last {minutes} minutes"));
            ranges.Add(b);
        }

        memChart.Title = Tr("Mémoire", "Memory");
        memChart.Threshold = null;

        // Colonne de droite : les plus gourmands sur la période, en CPU ou en mémoire
        var byCpu = new ModernButton(Tr("Processeur", "CPU")) { Toggled = true, Height = 28 };
        var byMem = new ModernButton(Tr("Mémoire", "Memory")) { Height = 28 };
        byCpu.Click += (_, _) => { topMetric = TopTracker.Metric.Cpu; byCpu.Toggled = true; byMem.Toggled = false; RefreshTop(); };
        byMem.Click += (_, _) => { topMetric = TopTracker.Metric.Memory; byMem.Toggled = true; byCpu.Toggled = false; RefreshTop(); };
        tips.SetToolTip(byCpu, Tr("Classement par usage moyen du processeur sur la période (toutes les instances d'un programme additionnées)", "Ranked by average CPU usage over the period (all instances of a program added up)"));
        tips.SetToolTip(byMem, Tr("Classement par pic de mémoire sur la période", "Ranked by peak memory over the period"));
        var topHeader = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0, 0, 0, 12) };
        topHeader.Controls.Add(byCpu);
        topHeader.Controls.Add(byMem);
        var topBody = new Panel();
        topList.Dock = DockStyle.Fill;
        topBody.Controls.Add(topList);
        topBody.Controls.Add(topHeader);

        var grid = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 300));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var cpuCard = new Card(chart, fill: true) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 14, 7) };
        var memCard = new Card(memChart, fill: true) { Dock = DockStyle.Fill, Margin = new Padding(0, 7, 14, 0) };
        var topCard = new Card(topBody, Tr("Les plus gourmands", "Top consumers"), fill: true) { Dock = DockStyle.Fill, Margin = new Padding(0) };
        topCard.Padding = new Padding(18, 50, 18, 18);
        grid.Controls.Add(cpuCard, 0, 0);
        grid.Controls.Add(memCard, 0, 1);
        grid.Controls.Add(topCard, 1, 0);
        grid.SetRowSpan(topCard, 2);

        return MakePage(Tr("Graphique", "Charts"), Tr("Processeur et mémoire de tout le PC. Survolez une courbe pour lire une valeur ; la ligne pointillée est le seuil de ProBalance.",
            "CPU and memory of the whole PC. Hover a curve to read a value; the dotted line is the ProBalance threshold."),
            grid, ranges.ToArray());
    }

    void RefreshTop()
    {
        var from = DateTime.UtcNow - chart.Range;
        var top = topTracker.Top(from, topMetric);
        topList.SetItems(top.Select(e => (e.Name, e.Value,
            topMetric == TopTracker.Metric.Cpu ? $"{e.Value:0.0} %" : FormatBytes((long)e.Value))).ToList());
    }

    static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} {Units.GB}" : $"{bytes / (1024 * 1024):N0} {Units.MB}";

    // ---------- Démarrage ----------

    readonly BufferedListView startupList = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true };
    readonly ModernButton startupToggle = new(Tr("Désactiver", "Disable")) { Enabled = false };
    readonly ModernButton startupOpen = new(Tr("Ouvrir l'emplacement", "Open location")) { Enabled = false };
    readonly UndoBanner startupBanner = new();
    readonly StartupManager startup = StartupManager.ForSystem();

    Control BuildStartupPage()
    {
        startupList.Columns.Add(Tr("Programme", "Program"), 200);
        startupList.Columns.Add(Tr("État", "Status"), 126);
        startupList.Columns.Add(Tr("Éditeur", "Publisher"), 160);
        startupList.Columns.Add("Source", 190);
        startupList.Columns.Add(Tr("Commande", "Command"), 300);
        Theme.StyleList(startupList, (item, col) => item.Tag is not StartupItem s ? null : col switch
        {
            0 => new Theme.CellStyle(Icon: IconFor(s.Path)),
            1 => new Theme.CellStyle(Text: s.Enabled ? Tr("● Activé", "● Enabled") : Tr("○ Désactivé", "○ Disabled"), Fore: s.Enabled ? Theme.Current.Accent : Theme.Current.Muted),
            _ => null,
        });
        startupList.SelectedIndexChanged += (_, _) => UpdateStartupButtons();
        startupList.MouseClick += (_, e) =>
        {
            // Un clic sur l'état active ou désactive, comme pour les règles
            var hit = startupList.HitTest(e.Location);
            if (e.Button == MouseButtons.Left && hit.Item?.Tag is StartupItem s && hit.Item.SubItems.IndexOf(hit.SubItem) == 1)
                ToggleStartup(s);
        };
        startupList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space && SelectedStartup is { } s) { ToggleStartup(s); e.Handled = true; }
        };
        startupToggle.Click += (_, _) => ToggleStartup(SelectedStartup);
        startupOpen.Click += (_, _) =>
        {
            if (SelectedStartup?.Path is not { } path)
                return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Ouverture de l'emplacement", ex); }
        };
        var refresh = new ModernButton(Tr("Actualiser", "Refresh"));
        refresh.Click += (_, _) => RefreshStartup();
        tips.SetToolTip(startupToggle, Tr("Empêcher ce programme de se lancer avec Windows, ou le réautoriser (Espace)", "Stop this program from starting with Windows, or allow it again (Space)"));
        tips.SetToolTip(startupOpen, Tr("Afficher le fichier dans l'Explorateur", "Show the file in Explorer"));
        tips.SetToolTip(refresh, Tr("Relire la liste (F5)", "Reload the list (F5)"));

        var body = new Panel();
        body.Controls.Add(new Card(startupList, fill: true) { Dock = DockStyle.Fill });
        body.Controls.Add(startupBanner);
        return MakePage(Tr("Démarrage", "Startup"), Tr("Programmes lancés à l'ouverture de votre session. Désactiver un programme l'empêche seulement de démarrer avec Windows : rien n'est supprimé, et vous pouvez le réactiver à tout moment.",
            "Programs launched when you sign in. Disabling a program only stops it from starting with Windows: nothing is deleted, and you can enable it again at any time."),
            body, refresh, startupOpen, startupToggle);
    }

    StartupItem? SelectedStartup => startupList.SelectedItems.Count == 1 ? startupList.SelectedItems[0].Tag as StartupItem : null;

    void UpdateStartupButtons()
    {
        var s = SelectedStartup;
        startupToggle.Enabled = s != null;
        startupToggle.Text = s?.Enabled == false ? Tr("Activer", "Enable") : Tr("Désactiver", "Disable");
        startupOpen.Enabled = s?.Path != null;
    }

    void RefreshStartup()
    {
        var selected = SelectedStartup;
        var items = startup.List();
        startupList.BeginUpdate();
        startupList.Items.Clear();
        foreach (var s in items)
        {
            var item = new ListViewItem(new[] { s.Name, "", s.Publisher ?? "", s.SourceLabel, s.Command }) { Tag = s, ToolTipText = s.Command };
            startupList.Items.Add(item);
            if (selected != null && s.Source == selected.Source && s.Entry == selected.Entry)
                item.Selected = true;
        }
        startupList.EndUpdate();
        Theme.FitColumns(startupList);
        UpdateStartupButtons();
    }

    void ToggleStartup(StartupItem? s)
    {
        if (s == null)
            return;
        try
        {
            startup.SetEnabled(s, !s.Enabled);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Tr("Impossible de modifier ce programme : ", "Unable to change this program: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        RefreshStartup();
        startupBanner.Show(s.Enabled
            ? Tr($"« {s.Name} » ne se lancera plus avec Windows.", $"“{s.Name}” will no longer start with Windows.")
            : Tr($"« {s.Name} » se lancera de nouveau avec Windows.", $"“{s.Name}” will start with Windows again."), () =>
        {
            try { startup.SetEnabled(s, s.Enabled); }
            catch (Exception ex) { Log.Error("Démarrage", ex); }
            RefreshStartup();
        });
    }

    // ---------- Règles ----------

    Control BuildRulesPage()
    {
        ruleList.Columns.Add(Tr("Processus", "Process"), 170);
        ruleList.Columns.Add(Tr("État", "Status"), 90);
        ruleList.Columns.Add(Tr("Priorité", "Priority"), 170);
        ruleList.Columns.Add(Tr("Cœurs", "Cores"), 80);
        ruleList.Columns.Add(Tr("Autres réglages", "Other settings"), 330);
        Theme.StyleList(ruleList, (item, col) => col == 1
            ? new Theme.CellStyle(Text: item.SubItems[1].Text == "Inactive" ? "○ Inactive" : "● Active",
                Fore: item.SubItems[1].Text == "Inactive" ? Theme.Current.Muted : Theme.Current.Accent)
            : null);
        ruleList.DoubleClick += (_, _) => EditRule();
        ruleList.SelectedIndexChanged += (_, _) => UpdateRuleButtons();
        ruleList.MouseClick += (_, e) =>
        {
            // Un clic sur l'état active ou désactive la règle
            var hit = ruleList.HitTest(e.Location);
            if (e.Button == MouseButtons.Left && hit.Item != null && hit.Item.SubItems.IndexOf(hit.SubItem) == 1)
                ToggleRule(hit.Item.Index);
        };
        ruleList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) { DeleteRule(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { EditRule(); e.Handled = true; }
            else if (e.KeyCode == Keys.Space && SelectedRule >= 0) { ToggleRule(SelectedRule); e.Handled = true; }
        };
        ruleList.ContextMenuStrip = BuildRuleMenu();

        editRule.Click += (_, _) => EditRule();
        deleteRule.Click += (_, _) => DeleteRule();
        moveUp.Click += (_, _) => MoveRule(-1);
        moveDown.Click += (_, _) => MoveRule(1);
        var add = new ModernButton(Tr("Ajouter une règle", "Add a rule"), primary: true);
        add.Click += (_, _) => AddRule(null);
        var more = new ModernButton("⋯");
        var moreMenu = new ContextMenuStrip();
        moreMenu.Items.Add(Tr("Importer des règles…", "Import rules…"), null, (_, _) => ImportRules());
        moreMenu.Items.Add(Tr("Exporter les règles…", "Export rules…"), null, (_, _) => ExportRules());
        profileButton.Text = ProfileButtonText();
        profileButton.Click += (_, _) => ShowProfileMenu();
        tips.SetToolTip(profileButton, Tr("Profils : plusieurs jeux de règles (Travail, Jeu, Silencieux…) entre lesquels basculer en un clic, ici ou depuis l'icône de notification.",
            "Profiles: several rule sets (Work, Gaming, Quiet…) you can switch between in one click, here or from the notification icon."));
        more.ContextMenuStrip = moreMenu; // ainsi le thème lui est appliqué avec le reste
        more.Click += (_, _) => moreMenu.Show(more, new Point(0, more.Height + 2));

        tips.SetToolTip(add, Tr("Nouvelle règle (Ctrl+N)", "New rule (Ctrl+N)"));
        tips.SetToolTip(editRule, Tr("Modifier la règle sélectionnée (Entrée ou double-clic)", "Edit the selected rule (Enter or double-click)"));
        tips.SetToolTip(deleteRule, Tr("Supprimer la règle sélectionnée (Suppr). Vous pourrez annuler.", "Delete the selected rule (Del). You can undo."));
        tips.SetToolTip(moveUp, Tr("Monter : la première règle qui correspond l'emporte", "Move up: the first matching rule wins"));
        tips.SetToolTip(moveDown, Tr("Descendre", "Move down"));
        tips.SetToolTip(more, Tr("Importer ou exporter les règles", "Import or export rules"));

        var host = new Panel();
        host.Controls.Add(ruleList);
        host.Controls.Add(rulesEmpty);
        ruleList.Dock = DockStyle.Fill;
        var body = new Panel();
        body.Controls.Add(new Card(host, fill: true) { Dock = DockStyle.Fill });
        body.Controls.Add(rulesBanner);

        return MakePage(Tr("Règles", "Rules"), Tr("La première règle active qui correspond s'applique, y compris aux processus déjà lancés. Cliquez sur l'état pour activer ou désactiver une règle.",
                "The first matching active rule applies, including to processes already running. Click the status to enable or disable a rule."),
            body, profileButton, moveUp, moveDown, editRule, deleteRule, more, add);
    }

    // ---------- Profils ----------

    readonly ModernButton profileButton = new("");

    /// <summary>Le profil actif a changé (l'icône de notification met son menu à jour).</summary>
    public event Action? ProfileChanged;

    string ProfileButtonText() => Tr($"Profil : {settings.ActiveProfile} ▾", $"Profile: {settings.ActiveProfile} ▾");

    void ShowProfileMenu()
    {
        var menu = new ContextMenuStrip();
        foreach (var p in settings.Profiles)
        {
            var name = p.Name;
            var item = new ToolStripMenuItem(name) { Checked = name == settings.ActiveProfile };
            item.Click += (_, _) => SwitchProfile(name);
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Tr("Nouveau profil…", "New profile…"), null, (_, _) => NewProfile());
        menu.Items.Add(Tr("Renommer ce profil…", "Rename this profile…"), null, (_, _) => RenameProfile());
        var delete = new ToolStripMenuItem(Tr("Supprimer un profil", "Delete a profile")) { Enabled = settings.Profiles.Count > 1 };
        foreach (var p in settings.Profiles.Where(p => p.Name != settings.ActiveProfile))
        {
            var name = p.Name;
            delete.DropDownItems.Add(name, null, (_, _) => DeleteProfile(name));
        }
        menu.Items.Add(delete);
        Theme.ApplyTo(menu);
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(profileButton, new Point(0, profileButton.Height + 2));
    }

    /// <summary>Active un profil : ses règles remplacent les règles actuelles (rangées dans l'ancien profil).</summary>
    public void SwitchProfile(string name)
    {
        if (!Profiles.Switch(settings, name))
            return;
        SaveAndApply();
        profileButton.Text = ProfileButtonText();
        Log.Info(Tr($"Profil « {settings.ActiveProfile} » activé", $"Profile “{settings.ActiveProfile}” activated"), LogCategory.Rule);
        rulesBanner.Show(Tr($"Profil « {settings.ActiveProfile} » actif : {settings.Rules.Count} règle(s).", $"Profile “{settings.ActiveProfile}” active: {settings.Rules.Count} rule(s)."));
        ProfileChanged?.Invoke();
    }

    void NewProfile()
    {
        var name = InputBox.Ask(this, Tr("Nouveau profil", "New profile"), Tr("Nom du profil (par exemple Travail, Jeu, Silencieux) :", "Profile name (for example Work, Gaming, Quiet):"), "");
        if (name == null)
            return;
        if (Profiles.Find(settings, name) != null || name.Trim().Length == 0)
        {
            MessageBox.Show(this, Tr("Ce nom est vide ou déjà utilisé.", "This name is empty or already used."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var copy = settings.Rules.Count > 0 && MessageBox.Show(this,
            Tr("Copier les règles actuelles dans le nouveau profil ?\n\nNon : il commence vide.", "Copy the current rules into the new profile?\n\nNo: it starts empty."),
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        var created = Profiles.Create(settings, name, copy);
        if (created != null)
            SwitchProfile(created.Name);
    }

    void RenameProfile()
    {
        var old = settings.ActiveProfile;
        var name = InputBox.Ask(this, Tr("Renommer le profil", "Rename profile"), Tr("Nouveau nom :", "New name:"), old);
        if (name == null || name.Trim() == old)
            return;
        if (!Profiles.Rename(settings, old, name))
        {
            MessageBox.Show(this, Tr("Ce nom est vide ou déjà utilisé.", "This name is empty or already used."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        SaveSettings();
        profileButton.Text = ProfileButtonText();
        ProfileChanged?.Invoke();
    }

    void DeleteProfile(string name)
    {
        var p = Profiles.Find(settings, name);
        if (p == null || MessageBox.Show(this, Tr($"Supprimer le profil « {name} » et ses {p.Rules.Count} règle(s) ?", $"Delete profile “{name}” and its {p.Rules.Count} rule(s)?"),
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        Profiles.Delete(settings, name);
        SaveSettings();
        ProfileChanged?.Invoke();
    }

    ContextMenuStrip BuildRuleMenu()
    {
        var menu = new ContextMenuStrip();
        var edit = new ToolStripMenuItem(Tr("Modifier…", "Edit…"), null, (_, _) => EditRule());
        var duplicate = new ToolStripMenuItem(Tr("Dupliquer", "Duplicate"), null, (_, _) => DuplicateRule());
        var toggle = new ToolStripMenuItem(Tr("Désactiver", "Disable"), null, (_, _) => ToggleRule(SelectedRule));
        var delete = new ToolStripMenuItem(Tr("Supprimer", "Delete"), null, (_, _) => DeleteRule());
        menu.Items.AddRange(new ToolStripItem[] { edit, duplicate, toggle, new ToolStripSeparator(), delete });
        menu.Opening += (_, e) =>
        {
            int i = SelectedRule;
            if (i < 0)
            {
                e.Cancel = true;
                return;
            }
            toggle.Text = settings.Rules[i].Enabled ? Tr("Désactiver", "Disable") : Tr("Activer", "Enable");
        };
        Theme.ApplyTo(menu);
        return menu;
    }

    void UpdateRuleButtons()
    {
        int i = SelectedRule;
        editRule.Enabled = deleteRule.Enabled = i >= 0;
        moveUp.Enabled = i > 0;
        moveDown.Enabled = i >= 0 && i < settings.Rules.Count - 1;
    }

    void RefreshRules(int select = -1)
    {
        ruleList.BeginUpdate();
        ruleList.Items.Clear();
        foreach (var r in settings.Rules)
        {
            ruleList.Items.Add(new ListViewItem(new[]
            {
                r.Pattern,
                r.Enabled ? "Active" : "Inactive",
                r.Priority is { } prio ? Engine.PriorityLabel(prio) : "—",
                r.AffinityMask is { } m ? $"{System.Numerics.BitOperations.PopCount((ulong)m)}" : Tr("Tous", "All"),
                RuleOptions(r),
            }));
        }
        if (select >= 0 && select < ruleList.Items.Count)
        {
            ruleList.Items[select].Selected = true;
            ruleList.Items[select].EnsureVisible();
        }
        ruleList.EndUpdate();
        bool empty = settings.Rules.Count == 0;
        ruleList.Visible = !empty;
        rulesEmpty.Visible = empty;
        Theme.FitColumns(ruleList);
        UpdateRuleButtons();
        UpdateGameList();
    }

    string PlanName(Guid id) =>
        (plans ??= PowerCfg.List()).FirstOrDefault(p => p.Id == id)?.Name ?? id.ToString();

    int SelectedRule => ruleList.SelectedIndices.Count == 1 ? ruleList.SelectedIndices[0] : -1;

    void AddRule(Rule? template)
    {
        plans = PowerCfg.List();
        using var dlg = new RuleDialog(template, plans, isNew: true, RunningNames());
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        settings.Rules.Add(dlg.Result);
        SaveAndApply(settings.Rules.Count - 1);
        ShowPage("Règles");
    }

    void EditRule()
    {
        int i = SelectedRule;
        if (i < 0)
            return;
        plans = PowerCfg.List();
        using var dlg = new RuleDialog(settings.Rules[i], plans, isNew: false, RunningNames());
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        settings.Rules[i] = dlg.Result;
        SaveAndApply(i);
    }

    void DuplicateRule()
    {
        int i = SelectedRule;
        if (i < 0)
            return;
        var copy = RuleStore.Clone(new Settings { Rules = { settings.Rules[i] } }).Rules[0];
        settings.Rules.Insert(i + 1, copy);
        SaveAndApply(i + 1);
    }

    void ToggleRule(int i)
    {
        if (i < 0 || i >= settings.Rules.Count)
            return;
        settings.Rules[i].Enabled = !settings.Rules[i].Enabled;
        SaveAndApply(i);
    }

    /// <summary>Suppression immédiate, avec « Annuler » pendant quelques secondes plutôt qu'une confirmation.</summary>
    void DeleteRule()
    {
        int i = SelectedRule;
        if (i < 0)
            return;
        var removed = settings.Rules[i];
        settings.Rules.RemoveAt(i);
        SaveAndApply(Math.Min(i, settings.Rules.Count - 1));
        rulesBanner.Show(Tr($"Règle « {removed.Pattern} » supprimée.", $"Rule “{removed.Pattern}” deleted."), () =>
        {
            settings.Rules.Insert(Math.Min(i, settings.Rules.Count), removed);
            SaveAndApply(i);
        });
    }

    void MoveRule(int delta)
    {
        int i = SelectedRule, j = i + delta;
        if (i < 0 || j < 0 || j >= settings.Rules.Count)
            return;
        (settings.Rules[i], settings.Rules[j]) = (settings.Rules[j], settings.Rules[i]);
        SaveAndApply(j);
    }

    void ExportRules()
    {
        if (settings.Rules.Count == 0)
        {
            MessageBox.Show(this, Tr("Il n'y a aucune règle à exporter.", "There are no rules to export."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new SaveFileDialog { Filter = Tr("Règles Corral (*.json)|*.json", "Corral rules (*.json)|*.json"), FileName = Tr("regles-corral.json", "corral-rules.json") };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            RuleStore.ExportRules(settings.Rules, dlg.FileName);
            rulesBanner.Show(Tr($"{settings.Rules.Count} règle(s) exportée(s).", $"{settings.Rules.Count} rule(s) exported."));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Tr("Export impossible : ", "Export failed: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ImportRules()
    {
        using var dlg = new OpenFileDialog { Filter = Tr("Règles Corral (*.json)|*.json|Tous les fichiers|*.*", "Corral rules (*.json)|*.json|All files|*.*") };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        List<Rule> imported;
        try
        {
            imported = RuleStore.ImportRules(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (imported.Count == 0)
        {
            MessageBox.Show(this, Tr("Ce fichier ne contient aucune règle utilisable.", "This file contains no usable rule."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        bool replace = false;
        if (settings.Rules.Count > 0)
        {
            var answer = MessageBox.Show(this,
                Tr($"{imported.Count} règle(s) trouvée(s).\n\nOui : les ajouter aux règles actuelles.\nNon : remplacer les règles actuelles.",
                    $"{imported.Count} rule(s) found.\n\nYes: add them to the current rules.\nNo: replace the current rules."),
                Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel)
                return;
            replace = answer == DialogResult.No;
        }
        var before = settings.Rules.ToList();
        if (replace)
            settings.Rules.Clear();
        settings.Rules.AddRange(imported);
        SaveAndApply();
        rulesBanner.Show(Tr($"{imported.Count} règle(s) importée(s).", $"{imported.Count} rule(s) imported."), () =>
        {
            settings.Rules.Clear();
            settings.Rules.AddRange(before);
            SaveAndApply();
        });
    }

    void SaveAndApply(int selectRule = -1)
    {
        SaveSettings();
        engine.UpdateSettings(RuleStore.Clone(settings));
        RefreshRules(selectRule);
    }

    /// <summary>Enregistre sans relancer le moteur (réglages purement visuels).</summary>
    void SaveSettings()
    {
        try
        {
            store.Save(settings);
        }
        catch (Exception ex)
        {
            Log.Error("Enregistrement de la configuration", ex);
            MessageBox.Show(this, Tr("Impossible d'enregistrer la configuration : ", "Unable to save the settings: ") + ex.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- ProBalance ----------

    Control BuildProBalancePage()
    {
        var presets = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        foreach (var preset in ProBalanceSettings.Presets)
        {
            var b = new ModernButton(preset == ProBalanceSettings.Default ? Tr($"{preset.Name} (défaut)", $"{preset.Name} (default)") : preset.Name);
            b.Click += (_, _) =>
            {
                SetNum(pbSystem, preset.System);
                SetNum(pbProcess, preset.Process);
                SetNum(pbRestore, preset.Restore);
                SetNum(pbTrigger, preset.Trigger);
                SetNum(pbRestoreSec, preset.RestoreAfter);
                UpdatePresetButtons();
            };
            presetButtons.Add((b, preset));
            presets.Controls.Add(b);
        }
        presets.Controls.Add(presetState);
        tips.SetToolTip(presetButtons[0].Button, Tr("Doux : n'intervient que sur un processeur très chargé.", "Gentle: only acts when the CPU is very busy."));
        tips.SetToolTip(presetButtons[1].Button, Tr("Équilibré : le bon compromis pour la plupart des PC.", "Balanced: the right trade-off for most PCs."));
        tips.SetToolTip(presetButtons[2].Button, Tr("Réactif : intervient tôt pour garder la fenêtre active toujours fluide.", "Responsive: acts early to keep the active window smooth."));
        foreach (var n in new[] { pbSystem, pbProcess, pbRestore, pbTrigger, pbRestoreSec })
            n.ValueChanged += (_, _) => UpdatePresetButtons();

        pbNotify.CheckedChanged += (_, _) =>
        {
            if (loading)
                return;
            settings.NotifyProBalance = pbNotify.Checked;
            SaveSettings();
        };

        var stack = new CardStack();
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Activer ProBalance", "Enable ProBalance"), Tr("Abaisse temporairement la priorité des processus qui saturent le processeur. La fenêtre que vous utilisez n'est jamais touchée.", "Temporarily lowers the priority of processes that saturate the CPU. The window you are using is never touched."), pbEnabled),
            SettingRow(Tr("Me prévenir quand ProBalance intervient", "Notify me when ProBalance acts"), Tr("Affiche une bulle près de l'horloge (au plus une toutes les 30 secondes).", "Shows a balloon near the clock (at most one every 30 seconds)."), pbNotify)), Tr("Fonctionnement", "Behavior")));

        var resetStats = new ModernButton(Tr("Remettre à zéro", "Reset")) { Height = 28 };
        resetStats.Click += (_, _) =>
        {
            if (MessageBox.Show(this, Tr("Effacer les statistiques de ProBalance ?", "Clear ProBalance statistics?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                stats.Reset();
                RefreshStats();
            }
        };
        var statsGrid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 8) };
        for (int i = 0; i < 3; i++)
            statsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        foreach (var (label, value) in new[] { (Tr("Aujourd'hui", "Today"), statsToday), (Tr("7 derniers jours", "Last 7 days"), statsWeek), (Tr("30 derniers jours", "Last 30 days"), statsMonth) })
        {
            var cell = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
            cell.Controls.Add(new Label { Text = label, AutoSize = true, Tag = Theme.HintTag, Margin = new Padding(0) });
            cell.Controls.Add(value);
            statsGrid.Controls.Add(cell);
        }
        stack.Controls.Add(new Card(Rows(
            statsGrid,
            SettingRow(Tr("Les plus souvent abaissés (7 jours)", "Most often lowered (7 days)"), null, resetStats),
            statsTop), Tr("Statistiques", "Statistics")));
        RefreshStats();

        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Préréglage", "Preset"), Tr("Remplit les seuils ci-dessous. Pensez à enregistrer.", "Fills in the thresholds below. Remember to save."), presets)), Tr("Sensibilité", "Sensitivity")));
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Charge totale du processeur", "Total CPU load"), Tr("ProBalance n'agit qu'au-delà de ce seuil.", "ProBalance only acts above this threshold."), WithUnit(pbSystem, "%")),
            SettingRow(Tr("Charge d'un processus", "Load of one process"), Tr("Part du processeur total utilisée par un processus pour être visé.", "Share of the total CPU a process must use to be targeted."), WithUnit(pbProcess, "%")),
            SettingRow(Tr("Durée avant d'agir", "Delay before acting"), Tr("Les pics plus courts sont ignorés.", "Shorter spikes are ignored."), WithUnit(pbTrigger, "s"))), Tr("Déclenchement", "Trigger")));
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Charge du processus sous", "Process load below"), Tr("La priorité d'origine est rendue en dessous de ce seuil, ou quand le système se calme.", "The original priority is restored below this threshold, or when the system calms down."), WithUnit(pbRestore, "%")),
            SettingRow(Tr("Durée avant de restaurer", "Delay before restoring"), null, WithUnit(pbRestoreSec, "s"))), Tr("Restauration", "Restore")));
        var exclusions = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        exclusions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        exclusions.Controls.Add(new Label
        {
            AutoSize = true,
            Tag = Theme.HintTag,
            MaximumSize = new Size(640, 0),
            Margin = new Padding(0, 0, 0, 8),
            Text = Tr("Un nom d'exécutable par ligne (jokers acceptés). Astuce : clic droit sur un processus → « Exclure de ProBalance ». " +
                      "Les processus système et ceux dont une règle fixe la priorité sont toujours exclus.",
                "One executable name per line (wildcards allowed). Tip: right-click a process → “Exclude from ProBalance”. " +
                "System processes and those whose priority is set by a rule are always excluded."),
        });
        exclusions.Controls.Add(pbExclusions);
        stack.Controls.Add(new Card(exclusions, "Exclusions"));

        var save = new ModernButton(Tr("Enregistrer", "Save"), primary: true);
        save.Click += (_, _) => ApplyProBalance();
        tips.SetToolTip(save, Tr("Appliquer les réglages de ProBalance", "Apply the ProBalance settings"));
        return MakePage("ProBalance", Tr("Garde le système réactif quand un programme accapare le processeur.", "Keeps the system responsive when a program hogs the CPU."), stack, save);
    }

    void UpdatePresetButtons()
    {
        var current = new ProBalanceSettings
        {
            SystemThreshold = (double)pbSystem.Value,
            ProcessThreshold = (double)pbProcess.Value,
            RestoreThreshold = (double)pbRestore.Value,
            TriggerSeconds = (int)pbTrigger.Value,
            RestoreSeconds = (int)pbRestoreSec.Value,
        }.CurrentPreset();
        foreach (var (button, preset) in presetButtons)
            button.Toggled = preset == current;
        presetState.Text = current == null ? Tr("Personnalisé", "Custom") : "";
    }

    readonly Label statsToday = StatValue();
    readonly Label statsWeek = StatValue();
    readonly Label statsMonth = StatValue();
    readonly Label statsTop = new() { AutoSize = true, Tag = Theme.HintTag, MaximumSize = new Size(640, 0), Margin = new Padding(0, 0, 0, 4) };

    static Label StatValue() => new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 18f), Margin = new Padding(0, 2, 0, 0) };

    void RefreshStats()
    {
        static string Interventions(int n) => n == 0 ? "0" : n.ToString("N0");
        statsToday.Text = Interventions(stats.CountToday());
        statsWeek.Text = Interventions(stats.Count(7));
        statsMonth.Text = Interventions(stats.Count(30));
        var top = stats.TopPrograms(7);
        statsTop.Text = top.Count == 0
            ? Tr("Aucune intervention ces 7 derniers jours : le processeur n'a pas été saturé, ou ProBalance est désactivé.", "No action in the last 7 days: the CPU was not saturated, or ProBalance is off.")
            : string.Join("   ·   ", top.Select(t => Tr($"{t.Name} ({t.Count} fois)", $"{t.Name} ({t.Count}×)")));
    }

    void LoadProBalance()
    {
        var pb = settings.ProBalance;
        pbEnabled.Checked = pb.Enabled;
        pbNotify.Checked = settings.NotifyProBalance;
        chart.Threshold = pb.Enabled ? pb.SystemThreshold : null;
        chart.Invalidate();
        SetNum(pbSystem, pb.SystemThreshold);
        SetNum(pbProcess, pb.ProcessThreshold);
        SetNum(pbRestore, pb.RestoreThreshold);
        SetNum(pbTrigger, pb.TriggerSeconds);
        SetNum(pbRestoreSec, pb.RestoreSeconds);
        pbExclusions.Text = string.Join(Environment.NewLine, pb.Exclusions);
        UpdatePresetButtons();
    }

    void ApplyProBalance()
    {
        var pb = settings.ProBalance;
        pb.Enabled = pbEnabled.Checked;
        pb.SystemThreshold = (double)pbSystem.Value;
        pb.ProcessThreshold = (double)pbProcess.Value;
        pb.RestoreThreshold = (double)pbRestore.Value;
        pb.TriggerSeconds = (int)pbTrigger.Value;
        pb.RestoreSeconds = (int)pbRestoreSec.Value;
        pb.Exclusions = pbExclusions.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SaveAndApply();
        LoadProBalance();
    }

    /// <summary>Activer ou désactiver ProBalance depuis le menu de l'icône de notification.</summary>
    public void SetProBalanceEnabled(bool enabled)
    {
        settings.ProBalance.Enabled = enabled;
        SaveAndApply();
        LoadProBalance();
    }

    static NumericUpDown Num(int min, int max) => new() { Minimum = min, Maximum = max, Width = 72, TextAlign = HorizontalAlignment.Right };

    static void SetNum(NumericUpDown n, double value) =>
        n.Value = Math.Clamp((decimal)value, n.Minimum, n.Maximum);

    string RuleOptions(Rule r)
    {
        var parts = new List<string>();
        if (r.IsGame) parts.Add(Tr("Jeu", "Game"));
        if (r.GpuPreference is { } gpu) parts.Add("GPU " + GpuPreferences.Label(gpu).ToLowerInvariant());
        if (r.PowerPlan is { } g) parts.Add(Tr("Plan ", "Plan ") + PlanName(g));
        if (r.CpuLimitPercent is { } c) parts.Add($"CPU max {c} %");
        if (r.MemoryLimitMB is { } mem) parts.Add($"RAM max {mem} {Units.MB}");
        if (r.EfficiencyMode == true) parts.Add(Tr("Efficacité", "Efficiency"));
        if (r.EfficiencyMode == false) parts.Add(Tr("Jamais efficacité", "Never efficiency"));
        if (r.IoPriority is { } io) parts.Add(Tr("Disque ", "Disk ") + Engine.IoLabel(io).ToLowerInvariant());
        if (r.MemoryPriority is { } m) parts.Add(Tr("Mémoire ", "Memory ") + Engine.MemoryLabel(m).ToLowerInvariant());
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    // ---------- Mode Jeu ----------

    readonly Label gameState = new() { AutoSize = true, Font = Ui.Section, Margin = new Padding(0, 2, 0, 0) };
    readonly Label gameDetail = new() { AutoSize = true, Tag = Theme.HintTag, MaximumSize = new Size(620, 0), Margin = new Padding(0, 4, 0, 0) };
    readonly Label gameList = new() { AutoSize = true, Tag = Theme.HintTag, MaximumSize = new Size(520, 0), Margin = new Padding(0, 2, 0, 0) };
    readonly ModernButton gameToggle = new(Tr("Activer maintenant", "Turn on now"));
    readonly ToggleSwitch gmAuto = new();
    readonly ToggleSwitch gmReactive = new();
    readonly ToggleSwitch gmLower = new();
    readonly ToggleSwitch gmNotify = new();
    readonly ComboBox gmPlan = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    readonly TextBox gmApps = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 110, Dock = DockStyle.Fill };
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
        gameToggle.Enabled = !(snap.GameMode && !engine.GameModeManual);
    }

    // ---------- Optimisations ----------

    readonly ToggleSwitch optBoost = new();
    readonly ToggleSwitch optIdle = new();
    readonly NumericUpDown optIdleMinutes = Num(1, 240);
    readonly ComboBox optIdlePlan = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    readonly List<Guid> optIdlePlanIds = new();
    readonly ToggleSwitch optMem = new();
    readonly NumericUpDown optMemThreshold = Num(50, 98);
    readonly ToggleSwitch optMemPurge = new();
    readonly ToggleSwitch optMemTrim = new();
    readonly Label optMemNow = new() { AutoSize = true, Tag = Theme.HintTag, Margin = new Padding(0, 6, 0, 0) };

    Control BuildOptimizationPage()
    {
        var cleanNow = new ModernButton(Tr("Nettoyer maintenant", "Clean now"));
        cleanNow.Click += (_, _) =>
        {
            engine.CleanMemoryNow();
            cleanNow.Enabled = false; // le résultat arrive en bulle ; on évite les clics répétés
            var t = new System.Windows.Forms.Timer { Interval = 5000 };
            t.Tick += (_, _) => { cleanNow.Enabled = true; t.Dispose(); };
            t.Start();
        };
        tips.SetToolTip(cleanNow, Tr("Vide le cache mémoire inutilisé et allège les programmes inactifs, tout de suite", "Empties the unused memory cache and trims idle programs, right now"));

        var stack = new CardStack();
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Boost du premier plan", "Foreground boost"),
                Tr("La fenêtre que vous utilisez passe en priorité « supérieure à la normale », et retrouve sa priorité quand vous changez de fenêtre. " +
                   "Sans effet sur les programmes dont une règle fixe la priorité.",
                   "The window you are using gets “above normal” priority, and gets its priority back when you switch windows. " +
                   "No effect on programs whose priority is set by a rule."), optBoost)), Tr("Premier plan", "Foreground")));
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Économie au repos", "Idle saver"),
                Tr("Après un moment sans clavier ni souris, passe sur un plan économique ; le plan habituel revient dès votre retour. " +
                   "Jamais pendant le Mode Jeu, une vidéo, ou quand une règle impose un plan.",
                   "After a while without keyboard or mouse, switches to a power-saving plan; the usual plan comes back as soon as you return. " +
                   "Never during Game Mode, a video, or when a rule sets a plan."), optIdle),
            SettingRow(Tr("Après", "After"), null, WithUnit(optIdleMinutes, Tr("minutes d'inactivité", "idle minutes"))),
            SettingRow(Tr("Plan pendant l'absence", "Plan while away"), null, optIdlePlan)), Tr("Au repos", "When idle")));
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Nettoyage automatique", "Automatic cleanup"),
                Tr("Quand la mémoire utilisée dépasse le seuil (au plus toutes les 5 minutes). Utile en jeu avec 16 Go de RAM ou moins.", "When used memory exceeds the threshold (at most every 5 minutes). Useful for gaming with 16 GB of RAM or less."), optMem),
            SettingRow(Tr("Seuil", "Threshold"), null, WithUnit(optMemThreshold, Tr("% de mémoire utilisée", "% of memory in use"))),
            SettingRow(Tr("Vider le cache inutilisé", "Empty the unused cache"), Tr("La « liste de veille » de Windows : des fichiers gardés en mémoire au cas où.", "Windows' “standby list”: files kept in memory just in case."), optMemPurge),
            SettingRow(Tr("Alléger les programmes inactifs", "Trim idle programs"), Tr("Leurs données peu utilisées repartent sur le disque ; elles reviennent à la demande. " +
                "Jamais la fenêtre active, un jeu, ou un programme qui travaille.", "Their rarely used data goes back to disk; it returns on demand. " +
                "Never the active window, a game, or a busy program."), optMemTrim),
            SettingRow(Tr("Maintenant", "Now"), null, cleanNow),
            optMemNow), Tr("Mémoire", "Memory")));

        var save = new ModernButton(Tr("Enregistrer", "Save"), primary: true);
        save.Click += (_, _) => ApplyOptimizations();
        LoadOptimizations();
        return MakePage(Tr("Optimisations", "Optimizations"), Tr("Des automatismes pour un PC plus réactif et plus économe. Tous sont désactivés par défaut.", "Automations for a more responsive and efficient PC. All are off by default."), stack, save);
    }

    void LoadOptimizations()
    {
        optBoost.Checked = settings.ForegroundBoost.Enabled;
        var idle = settings.IdleSaver;
        optIdle.Checked = idle.Enabled;
        SetNum(optIdleMinutes, idle.Minutes);
        optIdlePlan.Items.Clear();
        optIdlePlanIds.Clear();
        foreach (var p in plans ??= PowerCfg.List())
        {
            optIdlePlan.Items.Add(p.Name);
            optIdlePlanIds.Add(p.Id);
        }
        if (!optIdlePlanIds.Contains(idle.Plan))
        {
            optIdlePlan.Items.Add(Tr($"(introuvable) {idle.Plan}", $"(not found) {idle.Plan}"));
            optIdlePlanIds.Add(idle.Plan);
        }
        optIdlePlan.SelectedIndex = optIdlePlanIds.IndexOf(idle.Plan);
        var mem = settings.MemoryCleanup;
        optMem.Checked = mem.Enabled;
        SetNum(optMemThreshold, mem.ThresholdPercent);
        optMemPurge.Checked = mem.PurgeStandby;
        optMemTrim.Checked = mem.TrimIdle;
    }

    void ApplyOptimizations()
    {
        settings.ForegroundBoost.Enabled = optBoost.Checked;
        settings.IdleSaver.Enabled = optIdle.Checked;
        settings.IdleSaver.Minutes = (int)optIdleMinutes.Value;
        if (optIdlePlan.SelectedIndex >= 0)
            settings.IdleSaver.Plan = optIdlePlanIds[optIdlePlan.SelectedIndex];
        settings.MemoryCleanup.Enabled = optMem.Checked;
        settings.MemoryCleanup.ThresholdPercent = (int)optMemThreshold.Value;
        settings.MemoryCleanup.PurgeStandby = optMemPurge.Checked;
        settings.MemoryCleanup.TrimIdle = optMemTrim.Checked;
        SaveAndApply();
        LoadOptimizations();
    }

    // ---------- Options ----------

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
        var startMenu = new ToggleSwitch { Checked = settings.StartMenuShortcut, Enabled = Updater.IsPublishedBuild };
        if (!Updater.IsPublishedBuild)
            tips.SetToolTip(startMenu, Tr("Disponible uniquement avec l'exe publié (pas en développement).", "Only available with the published exe (not in development)."));
        startMenu.CheckedChanged += (_, _) =>
        {
            settings.StartMenuShortcut = startMenu.Checked;
            SaveSettings();
            if (Updater.IsPublishedBuild)
                StartMenu.Sync(startMenu.Checked, Environment.ProcessPath!);
        };
        var updates = new ToggleSwitch { Checked = settings.CheckUpdates };
        updates.CheckedChanged += (_, _) =>
        {
            settings.CheckUpdates = updates.Checked;
            SaveSettings();
        };
        var check = new ModernButton(Tr("Vérifier maintenant", "Check now"));
        check.Click += (_, _) => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
        var openFolder = new ModernButton(Tr("Ouvrir le dossier", "Open folder"));
        openFolder.Click += (_, _) => OpenConfigFolder();
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
        var languages = new[] { "auto", "fr", "en" };
        var language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        language.Items.AddRange(new object[] { Tr("Automatique (Windows)", "Automatic (Windows)"), "Français", "English" });
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
            SettingRow(Tr("Langue", "Language"), Tr("« Automatique » suit la langue de Windows : français, sinon anglais.", "“Automatic” follows the Windows language: French, otherwise English."), languageRow),
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

    // ---------- Journal ----------

    Control BuildLogPage()
    {
        logList.Columns.Add(Tr("Heure", "Time"), 90);
        logList.Columns.Add("Type", 130);
        logList.Columns.Add("Message", 640);
        Theme.StyleList(logList, (item, col) =>
            col == 1 && item.Tag is LogEntry entry ? new Theme.CellStyle(Text: "● " + item.SubItems[1].Text, Fore: LogColor(entry)) : null);

        var filters = new (string Label, Func<LogEntry, bool> Filter)[]
        {
            (Tr("Tout", "All"), _ => true),
            (Tr("Règles", "Rules"), e => e.Category == LogCategory.Rule),
            ("ProBalance", e => e.Category == LogCategory.ProBalance),
            (Tr("Erreurs", "Errors"), e => e.Level != LogLevel.Info),
        };
        foreach (var (label, filter) in filters)
        {
            var b = new ModernButton(label) { Toggled = logFilters.Count == 0 };
            b.Click += (_, _) =>
            {
                foreach (var (other, _) in logFilters)
                    other.Toggled = other == b;
                logFilter = filter;
                RefreshLog(force: true);
            };
            logFilters.Add((b, filter));
        }
        var open = new ModernButton(Tr("Ouvrir le dossier", "Open folder"));
        open.Click += (_, _) => OpenConfigFolder();
        tips.SetToolTip(open, Tr("Le journal complet est enregistré dans corral.log", "The full log is saved in corral.log"));

        var actions = logFilters.Select(f => (Control)f.Button).Append(open).ToArray();
        return MakePage(Tr("Journal", "Log"), Tr("Ce que Corral a fait : règles appliquées, interventions de ProBalance, erreurs. Les plus récents en haut.", "What Corral did: rules applied, ProBalance actions, errors. Most recent first."),
            new Card(logList, fill: true), actions);
    }

    static string CategoryLabel(LogEntry e) => e.Level switch
    {
        LogLevel.Error => Tr("Erreur", "Error"),
        LogLevel.Warning when e.Category == LogCategory.General => Tr("Avertissement", "Warning"),
        _ => e.Category switch
        {
            LogCategory.Rule => Tr("Règle", "Rule"),
            LogCategory.ProBalance => "ProBalance",
            LogCategory.Power => Tr("Alimentation", "Power"),
            LogCategory.Update => Tr("Mise à jour", "Update"),
            _ => Tr("Général", "General"),
        },
    };

    static Color LogColor(LogEntry e)
    {
        var p = Theme.Current;
        if (e.Level == LogLevel.Error)
            return Theme.IsDark ? Color.FromArgb(240, 110, 100) : Color.FromArgb(196, 43, 28);
        if (e.Level == LogLevel.Warning || e.Category == LogCategory.ProBalance)
            return p.Warning;
        return e.Category is LogCategory.Rule or LogCategory.Update ? p.Accent : p.Muted;
    }

    /// <summary>Recharge le journal ; sans <paramref name="force"/>, seulement s'il y a du nouveau.</summary>
    void RefreshLog(bool force)
    {
        var entries = Log.Recent();
        var newest = entries.Length > 0 ? entries[^1] : null;
        if (!force && ReferenceEquals(newest, lastLogShown))
            return;
        lastLogShown = newest;
        logList.BeginUpdate();
        logList.Items.Clear();
        foreach (var e in entries.Reverse().Where(logFilter))
        {
            logList.Items.Add(new ListViewItem(new[] { e.Time.ToString("HH:mm:ss"), CategoryLabel(e), e.Message })
            {
                Tag = e,
                ToolTipText = $"{e.Time:dd/MM HH:mm:ss}\n{e.Message}",
            });
        }
        logList.EndUpdate();
        Theme.FitColumns(logList);
    }
}

sealed class ProcessSorter : IComparer
{
    public int Column = 2;
    public bool Descending = true;

    public int Compare(object? x, object? y)
    {
        if ((x as ListViewItem)?.Tag is not ProcessRow a || (y as ListViewItem)?.Tag is not ProcessRow b)
            return 0;
        int c = Column switch
        {
            1 => a.Pid.CompareTo(b.Pid),
            2 => a.Cpu.CompareTo(b.Cpu),
            3 => a.MemoryBytes.CompareTo(b.MemoryBytes),
            4 => a.IoBytesPerSec.CompareTo(b.IoBytesPerSec),
            5 => a.Gpu.CompareTo(b.Gpu),
            6 => string.Compare(a.Rule, b.Rule, StringComparison.OrdinalIgnoreCase),
            7 => a.Restrained.CompareTo(b.Restrained),
            _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        if (c == 0 && Column != 0)
            c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return Descending ? -c : c;
    }
}
