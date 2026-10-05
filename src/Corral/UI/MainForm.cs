using System.Collections;
using System.Diagnostics;
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

    // Processus
    readonly BufferedListView procList = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    readonly Dictionary<string, ListViewItem> procItems = new();
    readonly ProcessSorter sorter = new();
    readonly TextBox search = new() { PlaceholderText = "Rechercher un processus", Width = 240, Margin = new Padding(4, 5, 8, 0) };
    readonly ModernButton createRule = new("Créer une règle") { Enabled = false };
    EngineSnapshot? lastSnapshot;

    // Graphique
    readonly CpuHistory cpuHistory = new(TimeSpan.FromMinutes(15));
    readonly CpuChart chart;
    int snapshotCount;

    // Règles
    readonly BufferedListView ruleList = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    readonly Label rulesEmpty = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Tag = Theme.HintTag,
        Text = "Aucune règle pour l'instant.\nCliquez sur « Ajouter une règle », ou faites un clic droit sur un processus.",
    };
    readonly ModernButton editRule = new("Modifier") { Enabled = false };
    readonly ModernButton deleteRule = new("Supprimer") { Enabled = false };
    readonly ModernButton moveUp = new("↑") { Enabled = false };
    readonly ModernButton moveDown = new("↓") { Enabled = false };
    IReadOnlyList<PowerPlanInfo>? plans;

    // ProBalance
    readonly ToggleSwitch pbEnabled = new();
    readonly NumericUpDown pbSystem = Num(10, 100);
    readonly NumericUpDown pbProcess = Num(1, 100);
    readonly NumericUpDown pbRestore = Num(0, 100);
    readonly NumericUpDown pbTrigger = Num(1, 60);
    readonly NumericUpDown pbRestoreSec = Num(1, 60);
    readonly TextBox pbExclusions = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 110, Dock = DockStyle.Fill };

    // Options
    readonly ComboBox themeChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    readonly ToggleSwitch autoStart = new();
    bool updatingAutoStart;

    // Journal
    readonly Panel logPage = new();
    readonly TextBox logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9.5f), Tag = Theme.FlatTag };

    volatile bool shown;
    bool allowClose;

    /// <summary>« Vérifier maintenant » : traité par l'icône de notification, qui gère les mises à jour.</summary>
    public event EventHandler? CheckUpdatesRequested;

    /// <summary>Bouton Pause de la barre latérale ; l'icône de notification garde l'état de référence.</summary>
    public event Action<bool>? PauseRequested;

    public MainForm(Engine engine, RuleStore store, Settings settings)
    {
        this.engine = engine;
        this.store = store;
        this.settings = settings;

        Text = "Corral";
        Font = Ui.Base;
        Icon = AppIcon.Load(new Size(32, 32));
        Size = new Size(1120, 720);
        MinimumSize = new Size(880, 580);
        StartPosition = FormStartPosition.CenterScreen;

        chart = new CpuChart(cpuHistory);
        // Icônes : codes communs à Segoe Fluent Icons et Segoe MDL2 Assets
        AddPage("", "Processus", BuildProcessPage());
        AddPage("", "Graphique", BuildChartPage());
        AddPage("", "Règles", BuildRulesPage());
        AddPage("", "ProBalance", BuildProBalancePage());
        AddPage("", "Options", BuildOptionsPage());
        AddPage("", "Journal", BuildLogPage());
        nav.SelectedChanged += SelectPage;
        nav.PauseRequested += paused => PauseRequested?.Invoke(paused);
        SelectPage(0);

        Controls.Add(pageHost);
        Controls.Add(nav);

        VisibleChanged += (_, _) => shown = Visible;
        engine.SnapshotReady += OnSnapshot;
        Theme.Changed += OnThemeChanged;
        RefreshRules();
        LoadProBalance();
        Theme.Apply(this);
    }

    /// <summary>Historique CPU affiché par l'onglet Graphique (exposé pour les captures de test).</summary>
    public CpuHistory History => cpuHistory;

    public void ShowPage(string title) => nav.Selected = pages.FindIndex(x => x.Title == title);

    void AddPage(string glyph, string title, Control page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        pageHost.Controls.Add(page);
        pages.Add((title, page));
        nav.Add(glyph, title);
    }

    void SelectPage(int index)
    {
        for (int i = 0; i < pages.Count; i++)
            pages[i].Page.Visible = i == index;
        if (pages[index].Page == logPage)
            RefreshLog();
        if (pages[index].Page.Contains(chart))
            chart.Invalidate();
    }

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
            Theme.Changed -= OnThemeChanged; // événement statique : éviter une fuite
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

    // ---------- Processus ----------

    Control BuildProcessPage()
    {
        procList.Columns.Add("Nom", 240);
        procList.Columns.Add("PID", 70, HorizontalAlignment.Right);
        procList.Columns.Add("CPU", 90, HorizontalAlignment.Right);
        procList.Columns.Add("Mémoire", 110, HorizontalAlignment.Right);
        procList.Columns.Add("Règle", 160);
        procList.Columns.Add("ProBalance", 120);
        Theme.StyleList(procList, ProcessCell, col => col == sorter.Column ? (sorter.Descending ? SortOrder.Descending : SortOrder.Ascending) : SortOrder.None);
        procList.ColumnClick += (_, e) =>
        {
            if (sorter.Column == e.Column)
                sorter.Descending = !sorter.Descending;
            else
                (sorter.Column, sorter.Descending) = (e.Column, e.Column is 2 or 3);
            procList.Sort();
            Theme.InvalidateHeader(procList);
        };
        procList.SelectedIndexChanged += (_, _) => createRule.Enabled = procList.SelectedItems.Count == 1;
        procList.DoubleClick += (_, _) => CreateRuleFromSelection();
        createRule.Click += (_, _) => CreateRuleFromSelection();
        search.TextChanged += (_, _) =>
        {
            if (lastSnapshot != null)
                ApplySnapshot(lastSnapshot);
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Créer une règle pour ce processus…", null, (_, _) => CreateRuleFromSelection());
        procList.ContextMenuStrip = menu;

        return MakePage("Processus", "Processus en cours, triés par usage du processeur. Double-cliquez sur un processus pour lui créer une règle.",
            new Card(procList, fill: true), search, createRule);
    }

    Theme.CellStyle? ProcessCell(ListViewItem item, int column)
    {
        if (item.Tag is not ProcessRow r)
            return null;
        var p = Theme.Current;
        return column switch
        {
            // Chaleur : la cellule CPU se teinte avec la charge (comme le Gestionnaire des tâches)
            2 when r.Cpu >= 0.5 => new Theme.CellStyle(Back: Ui.Blend(p.Accent, p.Surface, Math.Min(r.Cpu / 40, 1) * (Theme.IsDark ? 0.55 : 0.35))),
            2 or 3 => null,
            4 when r.Rule != null => new Theme.CellStyle(Fore: p.Accent),
            5 when r.Restrained => new Theme.CellStyle(Text: "▼ abaissé", Fore: p.Warning),
            _ => null,
        };
    }

    void CreateRuleFromSelection()
    {
        if (procList.SelectedItems.Count == 1 && procList.SelectedItems[0].Tag is ProcessRow row)
            AddRule(new Rule { Pattern = row.Name + ".exe" });
    }

    void OnSnapshot(EngineSnapshot snapshot)
    {
        // Historique alimenté même fenêtre cachée ; la toute première mesure (sans référence, donc 0) est ignorée.
        if (Interlocked.Increment(ref snapshotCount) > 1)
            cpuHistory.Add(DateTime.UtcNow, snapshot.SystemCpu);
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
        nav.SetStatus(snap.SystemCpu, snap.Rows.Count, snap.Paused);
        if (chart.Visible)
            chart.Invalidate();

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
                item = new ListViewItem(Enumerable.Repeat("", 6).ToArray());
                procItems[key] = item;
                procList.Items.Add(item);
                SetText(item, 0, r.Name);
                SetText(item, 1, r.Pid.ToString());
            }
            var old = item.Tag as ProcessRow;
            item.Tag = r;
            SetText(item, 2, $"{r.Cpu:0.0} %");
            SetText(item, 3, $"{r.MemoryBytes / (1024 * 1024):N0} Mo");
            SetText(item, 4, r.Rule ?? "");
            SetText(item, 5, r.Restrained ? "abaissé" : "");
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
        createRule.Enabled = procList.SelectedItems.Count == 1;
    }

    static void SetText(ListViewItem item, int index, string text)
    {
        if (item.SubItems[index].Text != text)
            item.SubItems[index].Text = text;
    }

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
                chart.Range = TimeSpan.FromMinutes(minutes);
                chart.Invalidate();
            };
            ranges.Add(b);
        }
        return MakePage("Graphique", "Usage total du processeur. Survolez la courbe pour lire une valeur.",
            new Card(chart, fill: true), ranges.ToArray());
    }

    // ---------- Règles ----------

    Control BuildRulesPage()
    {
        ruleList.Columns.Add("Processus", 170);
        ruleList.Columns.Add("État", 80);
        ruleList.Columns.Add("Priorité", 160);
        ruleList.Columns.Add("Affinité", 90);
        ruleList.Columns.Add("Plan d'alimentation", 160);
        ruleList.Columns.Add("CPU max", 80);
        ruleList.Columns.Add("RAM max", 90);
        Theme.StyleList(ruleList, (item, col) =>
            col == 1 && item.SubItems[1].Text == "Inactive" ? new Theme.CellStyle(Fore: Theme.Current.Muted) : null);
        ruleList.DoubleClick += (_, _) => EditRule();
        ruleList.SelectedIndexChanged += (_, _) => UpdateRuleButtons();

        editRule.Click += (_, _) => EditRule();
        deleteRule.Click += (_, _) => DeleteRule();
        moveUp.Click += (_, _) => MoveRule(-1);
        moveDown.Click += (_, _) => MoveRule(1);
        var add = new ModernButton("Ajouter une règle", primary: true);
        add.Click += (_, _) => AddRule(null);

        var host = new Panel();
        host.Controls.Add(ruleList);
        host.Controls.Add(rulesEmpty);
        ruleList.Dock = DockStyle.Fill;

        return MakePage("Règles", "La première règle active qui correspond s'applique, y compris aux processus déjà lancés.",
            new Card(host, fill: true), moveUp, moveDown, editRule, deleteRule, add);
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
                r.Priority == null ? "—" : RuleDialog.Priorities.FirstOrDefault(p => p.Value == r.Priority).Label ?? r.Priority.ToString()!,
                r.AffinityMask is { } m ? $"{System.Numerics.BitOperations.PopCount((ulong)m)} cœurs" : "—",
                r.PowerPlan is { } g ? PlanName(g) : "—",
                r.CpuLimitPercent is { } c ? $"{c} %" : "—",
                r.MemoryLimitMB is { } mem ? $"{mem} Mo" : "—",
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
    }

    string PlanName(Guid id) =>
        (plans ??= PowerCfg.List()).FirstOrDefault(p => p.Id == id)?.Name ?? id.ToString();

    int SelectedRule => ruleList.SelectedIndices.Count == 1 ? ruleList.SelectedIndices[0] : -1;

    void AddRule(Rule? template)
    {
        plans = PowerCfg.List();
        using var dlg = new RuleDialog(template, plans, isNew: true);
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
        using var dlg = new RuleDialog(settings.Rules[i], plans, isNew: false);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        settings.Rules[i] = dlg.Result;
        SaveAndApply(i);
    }

    void DeleteRule()
    {
        int i = SelectedRule;
        if (i < 0)
            return;
        if (MessageBox.Show(this, $"Supprimer la règle « {settings.Rules[i].Pattern} » ?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        settings.Rules.RemoveAt(i);
        SaveAndApply(Math.Min(i, settings.Rules.Count - 1));
    }

    void MoveRule(int delta)
    {
        int i = SelectedRule, j = i + delta;
        if (i < 0 || j < 0 || j >= settings.Rules.Count)
            return;
        (settings.Rules[i], settings.Rules[j]) = (settings.Rules[j], settings.Rules[i]);
        SaveAndApply(j);
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
            MessageBox.Show(this, "Impossible d'enregistrer la configuration : " + ex.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- ProBalance ----------

    Control BuildProBalancePage()
    {
        var stack = new CardStack();
        stack.Controls.Add(new Card(Rows(
            SettingRow("Activer ProBalance", "Abaisse temporairement la priorité des processus qui saturent le processeur.", pbEnabled),
            SettingRow("Charge totale du processeur", "ProBalance n'agit qu'au-delà de ce seuil.", WithUnit(pbSystem, "%")),
            SettingRow("Charge d'un processus", "Part du processeur total utilisée par un processus pour être visé.", WithUnit(pbProcess, "%")),
            SettingRow("Durée avant d'agir", "Les pics plus courts sont ignorés.", WithUnit(pbTrigger, "s"))), "Déclenchement"));
        stack.Controls.Add(new Card(Rows(
            SettingRow("Charge du processus sous", "La priorité d'origine est rendue en dessous de ce seuil, ou quand le système se calme.", WithUnit(pbRestore, "%")),
            SettingRow("Durée avant de restaurer", null, WithUnit(pbRestoreSec, "s"))), "Restauration"));
        var exclusions = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        exclusions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        exclusions.Controls.Add(new Label
        {
            AutoSize = true,
            Tag = Theme.HintTag,
            MaximumSize = new Size(640, 0),
            Margin = new Padding(0, 0, 0, 8),
            Text = "Un nom d'exécutable par ligne. La fenêtre au premier plan, les processus système et ceux dont une règle fixe la priorité sont toujours exclus.",
        });
        exclusions.Controls.Add(pbExclusions);
        stack.Controls.Add(new Card(exclusions, "Exclusions"));

        var save = new ModernButton("Enregistrer", primary: true);
        save.Click += (_, _) => ApplyProBalance();
        return MakePage("ProBalance", "Garde le système réactif quand un programme accapare le processeur.", stack, save);
    }

    void LoadProBalance()
    {
        var pb = settings.ProBalance;
        pbEnabled.Checked = pb.Enabled;
        chart.Threshold = pb.Enabled ? pb.SystemThreshold : null;
        chart.Invalidate();
        SetNum(pbSystem, pb.SystemThreshold);
        SetNum(pbProcess, pb.ProcessThreshold);
        SetNum(pbRestore, pb.RestoreThreshold);
        SetNum(pbTrigger, pb.TriggerSeconds);
        SetNum(pbRestoreSec, pb.RestoreSeconds);
        pbExclusions.Text = string.Join(Environment.NewLine, pb.Exclusions);
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

    static NumericUpDown Num(int min, int max) => new() { Minimum = min, Maximum = max, Width = 72, TextAlign = HorizontalAlignment.Right };

    static void SetNum(NumericUpDown n, double value) =>
        n.Value = Math.Clamp((decimal)value, n.Minimum, n.Maximum);

    // ---------- Options ----------

    Control BuildOptionsPage()
    {
        themeChoice.Items.AddRange(new object[] { "Système", "Clair", "Sombre" }); // même ordre que ThemeMode
        themeChoice.SelectedIndex = (int)settings.Theme;
        themeChoice.SelectedIndexChanged += (_, _) =>
        {
            settings.Theme = (ThemeMode)themeChoice.SelectedIndex;
            SaveSettings();
            Theme.Set(settings.Theme);
        };
        autoStart.CheckedChanged += (_, _) => ToggleAutoStart();
        var updates = new ToggleSwitch { Checked = settings.CheckUpdates };
        updates.CheckedChanged += (_, _) =>
        {
            settings.CheckUpdates = updates.Checked;
            SaveSettings();
        };
        var check = new ModernButton("Vérifier maintenant");
        check.Click += (_, _) => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
        var openFolder = new ModernButton("Ouvrir le dossier");
        openFolder.Click += (_, _) => OpenConfigFolder();

        var stack = new CardStack();
        stack.Controls.Add(new Card(Rows(
            SettingRow("Thème", "« Système » suit le mode clair ou sombre de Windows.", themeChoice)), "Apparence"));
        stack.Controls.Add(new Card(Rows(
            SettingRow("Lancer Corral à l'ouverture de session", "Démarre réduit dans la zone de notification, avec les droits administrateur.", autoStart)), "Démarrage"));
        stack.Controls.Add(new Card(Rows(
            SettingRow("Rechercher les mises à jour automatiquement", "Au démarrage, puis toutes les 6 heures.", updates),
            SettingRow($"Version {Updater.CurrentVersion.ToString(3)}",
                Updater.IsSupported ? "Mises à jour publiées sur GitHub." : $"Mises à jour indisponibles : {Updater.UnsupportedReason}.", check)), "Mises à jour"));
        stack.Controls.Add(new Card(Rows(
            SettingRow("Configuration et journal", store.ConfigDirectory, openFolder),
            new Label
            {
                AutoSize = true,
                Tag = Theme.HintTag,
                MaximumSize = new Size(640, 0),
                Margin = new Padding(0, 8, 0, 0),
                Text = "Fermer la fenêtre laisse Corral tourner dans la zone de notification. « Quitter » dans son menu l'arrête et " +
                       "restaure les priorités, affinités et le plan d'alimentation d'origine.",
            }), "Données"));
        return MakePage("Options", "Apparence, démarrage et mises à jour.", stack);
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
            MessageBox.Show(this, "Échec : " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        var refresh = new ModernButton("Rafraîchir");
        refresh.Click += (_, _) => RefreshLog();
        var open = new ModernButton("Ouvrir le dossier");
        open.Click += (_, _) => OpenConfigFolder();
        var page = MakePage("Journal", "Actions de Corral : règles appliquées, interventions de ProBalance, erreurs.",
            new Card(logBox, fill: true), refresh, open);
        logPage.Controls.Add(page);
        page.Dock = DockStyle.Fill;
        return logPage;
    }

    void RefreshLog()
    {
        logBox.Lines = Log.Recent();
        logBox.SelectionStart = logBox.TextLength;
        logBox.ScrollToCaret();
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
            4 => string.Compare(a.Rule, b.Rule, StringComparison.OrdinalIgnoreCase),
            5 => a.Restrained.CompareTo(b.Restrained),
            _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        if (c == 0 && Column != 0)
            c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return Descending ? -c : c;
    }
}
