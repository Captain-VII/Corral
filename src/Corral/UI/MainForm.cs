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

    readonly BufferedListView procList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    readonly Dictionary<string, ListViewItem> procItems = new();
    readonly ProcessSorter sorter = new();
    readonly ListView ruleList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    readonly ToolStripStatusLabel statusLabel = new();
    // Barre de navigation maison : la barre d'onglets du TabControl natif ne se laisse pas colorer en sombre.
    readonly ToolStrip nav = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 4, 6, 0), CanOverflow = false };
    readonly Panel pageHost = new() { Dock = DockStyle.Fill, Padding = new Padding(6) };
    readonly List<(ToolStripButton Button, Control Page)> pages = new();
    readonly Panel logPage = new();
    readonly ComboBox themeChoice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    readonly TextBox logBox = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9f) };

    readonly CheckBox pbEnabled = new() { Text = "Activer ProBalance", AutoSize = true };
    readonly NumericUpDown pbSystem = Num(10, 100);
    readonly NumericUpDown pbProcess = Num(1, 100);
    readonly NumericUpDown pbRestore = Num(0, 100);
    readonly NumericUpDown pbTrigger = Num(1, 60);
    readonly NumericUpDown pbRestoreSec = Num(1, 60);
    readonly TextBox pbExclusions = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Width = 260, Height = 140 };

    readonly CheckBox autoStart = new() { Text = "Lancer Corral à l'ouverture de session", AutoSize = true };
    bool updatingAutoStart;
    volatile bool shown;
    bool allowClose;

    /// <summary>« Vérifier maintenant » : traité par l'icône de notification, qui gère les mises à jour.</summary>
    public event EventHandler? CheckUpdatesRequested;
    IReadOnlyList<PowerPlanInfo>? plans;

    public MainForm(Engine engine, RuleStore store, Settings settings)
    {
        this.engine = engine;
        this.store = store;
        this.settings = settings;

        Text = "Corral";
        Icon = AppIcon.Load(new Size(32, 32));
        Size = new Size(950, 620);
        MinimumSize = new Size(700, 450);
        StartPosition = FormStartPosition.CenterScreen;

        AddPage("Processus", BuildProcessTab());
        AddPage("Règles", BuildRulesTab());
        AddPage("ProBalance", BuildProBalanceTab());
        AddPage("Options", BuildOptionsTab());
        AddPage("Journal", BuildLogTab());
        SelectPage(0);

        var status = new StatusStrip { SizingGrip = false };
        status.Items.Add(statusLabel);
        Controls.Add(pageHost);
        Controls.Add(nav);
        Controls.Add(status);

        VisibleChanged += (_, _) => shown = Visible;
        engine.SnapshotReady += OnSnapshot;
        Theme.Changed += OnThemeChanged;
        RefreshRules();
        LoadProBalance();
        Theme.Apply(this);
    }

    void AddPage(string title, Control page)
    {
        page.Dock = DockStyle.Fill;
        page.Visible = false;
        pageHost.Controls.Add(page);
        int index = pages.Count;
        var button = new ToolStripButton(title) { Padding = new Padding(10, 5, 10, 7) };
        button.Click += (_, _) => SelectPage(index);
        nav.Items.Add(button);
        pages.Add((button, page));
    }

    void SelectPage(int index)
    {
        for (int i = 0; i < pages.Count; i++)
        {
            pages[i].Button.Checked = i == index;
            pages[i].Page.Visible = i == index;
        }
        if (pages[index].Page == logPage)
            RefreshLog();
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

    // ---------- Onglet Processus ----------

    Control BuildProcessTab()
    {
        var page = new Panel();
        procList.Columns.Add("PID", 70, HorizontalAlignment.Right);
        procList.Columns.Add("Nom", 260);
        procList.Columns.Add("CPU %", 80, HorizontalAlignment.Right);
        procList.Columns.Add("Mémoire (Mo)", 110, HorizontalAlignment.Right);
        procList.Columns.Add("Règle", 180);
        procList.Columns.Add("ProBalance", 100);
        Theme.EnableThemedHeaders(procList);
        procList.ColumnClick += (_, e) =>
        {
            if (sorter.Column == e.Column)
                sorter.Descending = !sorter.Descending;
            else
                (sorter.Column, sorter.Descending) = (e.Column, e.Column is 2 or 3);
            procList.Sort();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Créer une règle pour ce processus…", null, (_, _) =>
        {
            if (procList.SelectedItems.Count == 1 && procList.SelectedItems[0].Tag is ProcessRow row)
                AddRule(new Rule { Pattern = row.Name + ".exe" });
        });
        procList.ContextMenuStrip = menu;
        page.Controls.Add(procList);
        return page;
    }

    void OnSnapshot(EngineSnapshot snapshot)
    {
        if (!shown)
            return;
        try { BeginInvoke(() => ApplySnapshot(snapshot)); }
        catch (InvalidOperationException) { } // fenêtre en cours de fermeture
    }

    void ApplySnapshot(EngineSnapshot snap)
    {
        if (IsDisposed)
            return;
        statusLabel.Text = $"CPU système : {snap.SystemCpu:0} %   ·   {snap.Rows.Count} processus" + (snap.Paused ? "   ·   EN PAUSE" : "");

        procList.BeginUpdate();
        procList.ListViewItemSorter = null; // évite un tri à chaque ajout
        var seen = new HashSet<string>();
        foreach (var r in snap.Rows)
        {
            var key = $"{r.Pid}:{r.Name}";
            seen.Add(key);
            if (!procItems.TryGetValue(key, out var item))
            {
                item = new ListViewItem(Enumerable.Repeat("", 6).ToArray());
                procItems[key] = item;
                procList.Items.Add(item);
                SetText(item, 0, r.Pid.ToString());
                SetText(item, 1, r.Name);
            }
            item.Tag = r;
            SetText(item, 2, r.Cpu.ToString("0.0"));
            SetText(item, 3, (r.MemoryBytes / (1024 * 1024)).ToString("N0"));
            SetText(item, 4, r.Rule ?? "");
            SetText(item, 5, r.Restrained ? "abaissé" : "");
        }
        foreach (var key in procItems.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            procList.Items.Remove(procItems[key]);
            procItems.Remove(key);
        }
        procList.ListViewItemSorter = sorter; // déclenche le tri
        procList.EndUpdate();
    }

    static void SetText(ListViewItem item, int index, string text)
    {
        if (item.SubItems[index].Text != text)
            item.SubItems[index].Text = text;
    }

    // ---------- Onglet Règles ----------

    Control BuildRulesTab()
    {
        var page = new Panel();
        ruleList.Columns.Add("Active", 60);
        ruleList.Columns.Add("Processus", 170);
        ruleList.Columns.Add("Priorité", 150);
        ruleList.Columns.Add("Affinité", 110);
        ruleList.Columns.Add("Plan d'alimentation", 170);
        ruleList.Columns.Add("CPU max", 70);
        ruleList.Columns.Add("RAM max", 80);
        Theme.EnableThemedHeaders(ruleList);
        ruleList.DoubleClick += (_, _) => EditRule();

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(3) };
        bar.Controls.Add(MakeButton("Ajouter", () => AddRule(null)));
        bar.Controls.Add(MakeButton("Modifier", EditRule));
        bar.Controls.Add(MakeButton("Supprimer", DeleteRule));
        bar.Controls.Add(MakeButton("Monter", () => MoveRule(-1)));
        bar.Controls.Add(MakeButton("Descendre", () => MoveRule(1)));

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(6),
            Tag = Theme.HintTag,
            Text = "La première règle active qui correspond s'applique. Les règles s'appliquent aussi aux processus déjà lancés.",
        };

        page.Controls.Add(ruleList);
        page.Controls.Add(bar);
        page.Controls.Add(hint);
        return page;
    }

    void RefreshRules(int select = -1)
    {
        ruleList.BeginUpdate();
        ruleList.Items.Clear();
        foreach (var r in settings.Rules)
        {
            ruleList.Items.Add(new ListViewItem(new[]
            {
                r.Enabled ? "Oui" : "Non",
                r.Pattern,
                r.Priority == null ? "" : RuleDialog.Priorities.FirstOrDefault(p => p.Value == r.Priority).Label ?? r.Priority.ToString()!,
                r.AffinityMask is { } m ? $"0x{m:X}" : "",
                r.PowerPlan is { } g ? PlanName(g) : "",
                r.CpuLimitPercent is { } c ? $"{c} %" : "",
                r.MemoryLimitMB is { } mem ? $"{mem} Mo" : "",
            }));
        }
        if (select >= 0 && select < ruleList.Items.Count)
        {
            ruleList.Items[select].Selected = true;
            ruleList.Items[select].EnsureVisible();
        }
        ruleList.EndUpdate();
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
        SelectPage(1);
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

    // ---------- Onglet ProBalance ----------

    Control BuildProBalanceTab()
    {
        var page = new Panel();
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(10), Dock = DockStyle.Top };
        void Row(string label, Control c)
        {
            int r = grid.RowCount++;
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) }, 0, r);
            grid.Controls.Add(c, 1, r);
        }
        Row("", pbEnabled);
        Row("Agir quand le CPU système dépasse (%) :", pbSystem);
        Row("… et qu'un processus dépasse (% du total) :", pbProcess);
        Row("… pendant (secondes) :", pbTrigger);
        Row("Restaurer quand le processus passe sous (%) :", pbRestore);
        Row("… ou le système se calme, pendant (secondes) :", pbRestoreSec);
        Row("Exclusions (un par ligne) :", pbExclusions);
        Row("", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Tag = Theme.HintTag,
            Text = "ProBalance abaisse temporairement la priorité des processus gourmands. Il ne touche jamais " +
                   "à la fenêtre au premier plan, aux processus système, ni aux processus dont une règle fixe la priorité.",
        });
        Row("", MakeButton("Appliquer", ApplyProBalance));
        page.Controls.Add(grid);
        return page;
    }

    void LoadProBalance()
    {
        var pb = settings.ProBalance;
        pbEnabled.Checked = pb.Enabled;
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

    static NumericUpDown Num(int min, int max) => new() { Minimum = min, Maximum = max, Width = 80 };

    static void SetNum(NumericUpDown n, double value) =>
        n.Value = Math.Clamp((decimal)value, n.Minimum, n.Maximum);

    // ---------- Onglet Options ----------

    Control BuildOptionsTab()
    {
        var page = new Panel();
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(10), WrapContents = false };
        autoStart.CheckedChanged += (_, _) => ToggleAutoStart();
        panel.Controls.Add(autoStart);

        themeChoice.Items.AddRange(new object[] { "Système", "Clair", "Sombre" }); // même ordre que ThemeMode
        themeChoice.SelectedIndex = (int)settings.Theme;
        themeChoice.SelectedIndexChanged += (_, _) =>
        {
            settings.Theme = (ThemeMode)themeChoice.SelectedIndex;
            SaveSettings();
            Theme.Set(settings.Theme);
        };
        var themeRow = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 6) };
        themeRow.Controls.Add(new Label { Text = "Thème :", AutoSize = true, Margin = new Padding(3, 7, 6, 3) });
        themeRow.Controls.Add(themeChoice);
        panel.Controls.Add(themeRow);
        panel.Controls.Add(MakeButton("Ouvrir le dossier de configuration", OpenConfigFolder));

        var updates = new CheckBox { Text = "Rechercher les mises à jour automatiquement", AutoSize = true, Checked = settings.CheckUpdates, Margin = new Padding(3, 16, 3, 3) };
        updates.CheckedChanged += (_, _) =>
        {
            settings.CheckUpdates = updates.Checked;
            SaveSettings();
        };
        var updateRow = new FlowLayoutPanel { AutoSize = true };
        updateRow.Controls.Add(MakeButton("Vérifier maintenant", () => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty)));
        updateRow.Controls.Add(new Label
        {
            AutoSize = true,
            Margin = new Padding(6, 8, 3, 3),
            Tag = Theme.HintTag,
            Text = $"Version {Updater.CurrentVersion.ToString(3)}" +
                   (Updater.IsSupported ? "" : $" · mises à jour indisponibles : {Updater.UnsupportedReason}"),
        });
        panel.Controls.Add(updates);
        panel.Controls.Add(updateRow);
        panel.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Margin = new Padding(3, 12, 3, 3),
            Tag = Theme.HintTag,
            Text = "Fermer la fenêtre laisse Corral tourner dans la zone de notification. Utilisez « Quitter » dans son menu " +
                   "pour l'arrêter : les priorités, affinités et le plan d'alimentation d'origine sont alors restaurés.",
        });
        page.Controls.Add(panel);
        return page;
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

    // ---------- Onglet Journal ----------

    Control BuildLogTab()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(3) };
        bar.Controls.Add(MakeButton("Rafraîchir", RefreshLog));
        bar.Controls.Add(MakeButton("Ouvrir le dossier", OpenConfigFolder));
        logPage.Controls.Add(logBox);
        logPage.Controls.Add(bar);
        return logPage;
    }

    void RefreshLog()
    {
        logBox.Lines = Log.Recent();
        logBox.SelectionStart = logBox.TextLength;
        logBox.ScrollToCaret();
    }

    static Button MakeButton(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => onClick();
        return b;
    }
}

sealed class BufferedListView : ListView
{
    public BufferedListView() => DoubleBuffered = true;
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
            0 => a.Pid.CompareTo(b.Pid),
            2 => a.Cpu.CompareTo(b.Cpu),
            3 => a.MemoryBytes.CompareTo(b.MemoryBytes),
            4 => string.Compare(a.Rule, b.Rule, StringComparison.OrdinalIgnoreCase),
            5 => a.Restrained.CompareTo(b.Restrained),
            _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        if (c == 0 && Column != 1)
            c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return Descending ? -c : c;
    }
}
