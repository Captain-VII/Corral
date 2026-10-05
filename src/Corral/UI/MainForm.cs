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
    readonly TextBox search = new() { PlaceholderText = "Rechercher un processus", Width = 240, Margin = new Padding(4, 5, 8, 0) };
    readonly ModernButton createRule = new("Créer une règle") { Enabled = false };
    readonly UndoBanner processBanner = new();
    readonly Dictionary<string, Image> iconCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Image genericIcon = ScaleIcon(SystemIcons.Application);
    Panel? welcome;
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
        Text = "Aucune règle pour l'instant.\nCliquez sur « Ajouter une règle », ou double-cliquez sur un processus.",
    };
    readonly ModernButton editRule = new("Modifier") { Enabled = false };
    readonly ModernButton deleteRule = new("Supprimer") { Enabled = false };
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
        KeyPreview = true;

        chart = new CpuChart(cpuHistory);
        // Icônes : codes communs à Segoe Fluent Icons et Segoe MDL2 Assets
        AddPage("", "Processus", BuildProcessPage());
        AddPage("", "Graphique", BuildChartPage());
        AddPage("", "Règles", BuildRulesPage());
        AddPage("", "ProBalance", BuildProBalancePage());
        AddPage("", "Mode Jeu", BuildGamePage());
        AddPage("", "Options", BuildOptionsPage());
        AddPage("", "Journal", BuildLogPage());
        nav.SelectedChanged += SelectPage;
        nav.PauseRequested += paused => PauseRequested?.Invoke(paused);
        nav.GameRequested += ToggleGameMode;
        tips.SetToolTip(nav.GameButton, "Plan Performances, ProBalance réactif et programmes de fond calmés le temps de jouer");

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
        nav.Add(glyph, title);
    }

    void SelectPage(int index)
    {
        for (int i = 0; i < pages.Count; i++)
            pages[i].Page.Visible = i == index;
        settings.Window.LastPage = pages[index].Title;
        if (pages[index].Title == "Journal")
            RefreshLog(force: true);
        if (pages[index].Page.Contains(chart))
            chart.Invalidate();
    }

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
        procList.SelectedIndexChanged += (_, _) => createRule.Enabled = SelectedProcess != null;
        procList.DoubleClick += (_, _) => CreateRuleFromSelection();
        createRule.Click += (_, _) => CreateRuleFromSelection();
        search.TextChanged += (_, _) =>
        {
            if (lastSnapshot != null)
                ApplySnapshot(lastSnapshot);
        };
        tips.SetToolTip(search, "Filtrer par nom (Ctrl+F). Échap pour effacer.");
        tips.SetToolTip(createRule, "Régler durablement le processus sélectionné : priorité, cœurs, plan d'alimentation, limites.");
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

        return MakePage("Processus", "Processus en cours, triés par usage du processeur. Double-cliquez sur un processus pour lui créer une règle, clic droit pour plus d'actions.",
            body, search, createRule);
    }

    Card BuildWelcome()
    {
        var points = new[]
        {
            ("", "Corral tourne en arrière-plan : fermer cette fenêtre le laisse actif dans la zone de notification, à côté de l'horloge."),
            ("", "Pour régler un programme, double-cliquez dessus ci-dessous. Des modèles prêts à l'emploi (Jeu, Tâche de fond…) remplissent la règle pour vous."),
            ("", "ProBalance est déjà actif : quand un programme sature le processeur, il le calme un instant pour que le PC reste réactif."),
        };
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (glyph, text) in points)
        {
            grid.Controls.Add(new Label { Text = glyph, Font = Ui.Icons, AutoSize = true, Margin = new Padding(2, 4, 0, 6) });
            grid.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new Size(820, 0), Margin = new Padding(0, 3, 0, 6) });
        }
        var ok = new ModernButton("Compris", primary: true) { Margin = new Padding(0, 8, 0, 0) };
        grid.Controls.Add(new Label { AutoSize = true });
        grid.Controls.Add(ok);
        var card = new Card(grid, "Bienvenue dans Corral");
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
        var create = new ToolStripMenuItem("Créer une règle…", null, (_, _) => CreateRuleFromSelection());
        var now = new ToolStripMenuItem("Priorité maintenant");
        foreach (var (label, value) in RuleDialog.Priorities.Where(p => p.Value != null))
        {
            var prio = value!.Value;
            now.DropDownItems.Add(label, null, (_, _) => SetPriorityNow(prio));
        }
        var exclude = new ToolStripMenuItem("Exclure de ProBalance", null, (_, _) => ExcludeFromProBalance());
        var open = new ToolStripMenuItem("Ouvrir l'emplacement du fichier", null, (_, _) => OpenProcessLocation());
        var kill = new ToolStripMenuItem("Terminer le processus…", null, (_, _) => KillSelected());
        now.ToolTipText = "Change la priorité tout de suite, sans créer de règle. Elle sera perdue à la fermeture du programme.";
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
            exclude.Text = excluded ? "Déjà exclu de ProBalance" : "Exclure de ProBalance";
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
            processBanner.Show($"Priorité de « {row.Name} » passée à « {Engine.PriorityLabel(priority)} » jusqu'à sa fermeture.");
    }

    void ExcludeFromProBalance()
    {
        if (SelectedProcess is not { } row)
            return;
        var name = row.Name + ".exe";
        settings.ProBalance.Exclusions.Add(name);
        SaveAndApply();
        LoadProBalance();
        processBanner.Show($"« {row.Name} » ne sera plus abaissé par ProBalance.", () =>
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
        if (MessageBox.Show(this, $"Terminer « {row.Name} » (PID {row.Pid}) ?\n\nLe programme se fermera immédiatement : ce qui n'est pas enregistré sera perdu.",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        try
        {
            using var p = Process.GetProcessById(row.Pid);
            if (!string.Equals(p.ProcessName, row.Name, StringComparison.OrdinalIgnoreCase))
                return; // PID réutilisé entre-temps
            p.Kill();
            Log.Info($"{row.Name} ({row.Pid}) terminé par l'utilisateur");
            processBanner.Show($"« {row.Name} » a été terminé.");
        }
        catch (ArgumentException)
        {
            // déjà fermé
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Impossible de terminer ce processus : " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            4 when r.Rule != null => new Theme.CellStyle(Fore: p.Accent),
            5 when r.Restrained => new Theme.CellStyle(Text: "▼ abaissé", Fore: p.Warning),
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

    void CreateRuleFromSelection()
    {
        if (SelectedProcess is { } row)
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
        nav.SetStatus(snap.SystemCpu, snap.Rows.Count, snap.Paused, snap.GameMode, snap.GameTrigger);
        UpdateGameStatus(snap);
        if (chart.Visible)
            chart.Invalidate();
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
        var lines = new List<string> { r.Path ?? $"{r.Name} (emplacement inaccessible : processus protégé)" };
        if (r.Rule != null)
            lines.Add($"Règle « {r.Rule} » : {r.RuleSummary}");
        if (r.Restrained)
            lines.Add("ProBalance l'a abaissé temporairement : il utilisait beaucoup le processeur pendant que le système était chargé.");
        return string.Join("\n", lines);
    }

    static void SetText(ListViewItem item, int index, string text)
    {
        if (item.SubItems[index].Text != text)
            item.SubItems[index].Text = text;
    }

    List<string> RunningNames() => lastSnapshot?.Rows.Select(r => r.Name).ToList() ?? new();

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
            tips.SetToolTip(b, $"Afficher les {minutes} dernières minutes");
            ranges.Add(b);
        }
        return MakePage("Graphique", "Usage total du processeur. Survolez la courbe pour lire une valeur ; la ligne pointillée est le seuil de ProBalance.",
            new Card(chart, fill: true), ranges.ToArray());
    }

    // ---------- Règles ----------

    Control BuildRulesPage()
    {
        ruleList.Columns.Add("Processus", 170);
        ruleList.Columns.Add("État", 90);
        ruleList.Columns.Add("Priorité", 170);
        ruleList.Columns.Add("Cœurs", 80);
        ruleList.Columns.Add("Autres réglages", 330);
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
        var add = new ModernButton("Ajouter une règle", primary: true);
        add.Click += (_, _) => AddRule(null);
        var more = new ModernButton("⋯");
        var moreMenu = new ContextMenuStrip();
        moreMenu.Items.Add("Importer des règles…", null, (_, _) => ImportRules());
        moreMenu.Items.Add("Exporter les règles…", null, (_, _) => ExportRules());
        more.ContextMenuStrip = moreMenu; // ainsi le thème lui est appliqué avec le reste
        more.Click += (_, _) => moreMenu.Show(more, new Point(0, more.Height + 2));

        tips.SetToolTip(add, "Nouvelle règle (Ctrl+N)");
        tips.SetToolTip(editRule, "Modifier la règle sélectionnée (Entrée ou double-clic)");
        tips.SetToolTip(deleteRule, "Supprimer la règle sélectionnée (Suppr). Vous pourrez annuler.");
        tips.SetToolTip(moveUp, "Monter : la première règle qui correspond l'emporte");
        tips.SetToolTip(moveDown, "Descendre");
        tips.SetToolTip(more, "Importer ou exporter les règles");

        var host = new Panel();
        host.Controls.Add(ruleList);
        host.Controls.Add(rulesEmpty);
        ruleList.Dock = DockStyle.Fill;
        var body = new Panel();
        body.Controls.Add(new Card(host, fill: true) { Dock = DockStyle.Fill });
        body.Controls.Add(rulesBanner);

        return MakePage("Règles", "La première règle active qui correspond s'applique, y compris aux processus déjà lancés. Cliquez sur l'état pour activer ou désactiver une règle.",
            body, moveUp, moveDown, editRule, deleteRule, more, add);
    }

    ContextMenuStrip BuildRuleMenu()
    {
        var menu = new ContextMenuStrip();
        var edit = new ToolStripMenuItem("Modifier…", null, (_, _) => EditRule());
        var duplicate = new ToolStripMenuItem("Dupliquer", null, (_, _) => DuplicateRule());
        var toggle = new ToolStripMenuItem("Désactiver", null, (_, _) => ToggleRule(SelectedRule));
        var delete = new ToolStripMenuItem("Supprimer", null, (_, _) => DeleteRule());
        menu.Items.AddRange(new ToolStripItem[] { edit, duplicate, toggle, new ToolStripSeparator(), delete });
        menu.Opening += (_, e) =>
        {
            int i = SelectedRule;
            if (i < 0)
            {
                e.Cancel = true;
                return;
            }
            toggle.Text = settings.Rules[i].Enabled ? "Désactiver" : "Activer";
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
                r.AffinityMask is { } m ? $"{System.Numerics.BitOperations.PopCount((ulong)m)}" : "Tous",
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
        rulesBanner.Show($"Règle « {removed.Pattern} » supprimée.", () =>
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
            MessageBox.Show(this, "Il n'y a aucune règle à exporter.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new SaveFileDialog { Filter = "Règles Corral (*.json)|*.json", FileName = "regles-corral.json" };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            RuleStore.ExportRules(settings.Rules, dlg.FileName);
            rulesBanner.Show($"{settings.Rules.Count} règle(s) exportée(s).");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Export impossible : " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ImportRules()
    {
        using var dlg = new OpenFileDialog { Filter = "Règles Corral (*.json)|*.json|Tous les fichiers|*.*" };
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
            MessageBox.Show(this, "Ce fichier ne contient aucune règle utilisable.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        bool replace = false;
        if (settings.Rules.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"{imported.Count} règle(s) trouvée(s).\n\nOui : les ajouter aux règles actuelles.\nNon : remplacer les règles actuelles.",
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
        rulesBanner.Show($"{imported.Count} règle(s) importée(s).", () =>
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
            MessageBox.Show(this, "Impossible d'enregistrer la configuration : " + ex.Message, Text,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------- ProBalance ----------

    Control BuildProBalancePage()
    {
        var presets = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        foreach (var preset in ProBalanceSettings.Presets)
        {
            var b = new ModernButton(preset == ProBalanceSettings.Default ? $"{preset.Name} (défaut)" : preset.Name);
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
        tips.SetToolTip(presetButtons[0].Button, "Doux : n'intervient que sur un processeur très chargé.");
        tips.SetToolTip(presetButtons[1].Button, "Équilibré : le bon compromis pour la plupart des PC.");
        tips.SetToolTip(presetButtons[2].Button, "Réactif : intervient tôt pour garder la fenêtre active toujours fluide.");
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
            SettingRow("Activer ProBalance", "Abaisse temporairement la priorité des processus qui saturent le processeur. La fenêtre que vous utilisez n'est jamais touchée.", pbEnabled),
            SettingRow("Me prévenir quand ProBalance intervient", "Affiche une bulle près de l'horloge (au plus une toutes les 30 secondes).", pbNotify)), "Fonctionnement"));
        stack.Controls.Add(new Card(Rows(
            SettingRow("Préréglage", "Remplit les seuils ci-dessous. Pensez à enregistrer.", presets)), "Sensibilité"));
        stack.Controls.Add(new Card(Rows(
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
            Text = "Un nom d'exécutable par ligne (jokers acceptés). Astuce : clic droit sur un processus → « Exclure de ProBalance ». " +
                   "Les processus système et ceux dont une règle fixe la priorité sont toujours exclus.",
        });
        exclusions.Controls.Add(pbExclusions);
        stack.Controls.Add(new Card(exclusions, "Exclusions"));

        var save = new ModernButton("Enregistrer", primary: true);
        save.Click += (_, _) => ApplyProBalance();
        tips.SetToolTip(save, "Appliquer les réglages de ProBalance");
        return MakePage("ProBalance", "Garde le système réactif quand un programme accapare le processeur.", stack, save);
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
        presetState.Text = current == null ? "Personnalisé" : "";
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
        if (r.IsGame) parts.Add("Jeu");
        if (r.PowerPlan is { } g) parts.Add("Plan " + PlanName(g));
        if (r.CpuLimitPercent is { } c) parts.Add($"CPU max {c} %");
        if (r.MemoryLimitMB is { } mem) parts.Add($"RAM max {mem} Mo");
        if (r.EfficiencyMode == true) parts.Add("Efficacité");
        if (r.EfficiencyMode == false) parts.Add("Jamais efficacité");
        if (r.IoPriority is { } io) parts.Add("Disque " + Engine.IoLabel(io).ToLowerInvariant());
        if (r.MemoryPriority is { } m) parts.Add("Mémoire " + Engine.MemoryLabel(m).ToLowerInvariant());
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    // ---------- Mode Jeu ----------

    readonly Label gameState = new() { AutoSize = true, Font = Ui.Section, Margin = new Padding(0, 2, 0, 0) };
    readonly Label gameDetail = new() { AutoSize = true, Tag = Theme.HintTag, MaximumSize = new Size(620, 0), Margin = new Padding(0, 4, 0, 0) };
    readonly Label gameList = new() { AutoSize = true, Tag = Theme.HintTag, MaximumSize = new Size(520, 0), Margin = new Padding(0, 2, 0, 0) };
    readonly ModernButton gameToggle = new("Activer maintenant");
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
        gameToggle.Click += (_, _) => ToggleGameMode();
        var addGame = new ModernButton("Ajouter un jeu…");
        addGame.Click += (_, _) => AddRule(new Rule { IsGame = true, Priority = System.Diagnostics.ProcessPriorityClass.High });
        tips.SetToolTip(addGame, "Crée une règle marquée « C'est un jeu » : le Mode Jeu s'activera quand ce programme tourne.");

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
        apps.Controls.Add(SettingRow("Calmer les programmes de fond",
            "Priorité basse et mode efficacité pour les programmes ci-dessous (navigateurs, synchronisation…), sauf s'ils ont leur propre règle.", gmLower));
        apps.Controls.Add(gmApps);

        var stack = new CardStack();
        stack.Controls.Add(new Card(status, "État"));
        stack.Controls.Add(new Card(Rows(
            SettingRow("Activer automatiquement quand un jeu tourne",
                "Un programme est un jeu si sa règle est marquée « C'est un jeu » (le modèle Jeu le fait).", gmAuto),
            SettingRow("Jeux reconnus", null, addGame),
            gameList), "Déclenchement"));
        stack.Controls.Add(new Card(Rows(
            SettingRow("Plan d'alimentation", "Activé pendant le Mode Jeu, puis le plan d'origine revient.", gmPlan),
            SettingRow("ProBalance réactif", "Utilise le préréglage Réactif pour que le jeu garde la main.", gmReactive),
            SettingRow("Me prévenir", "Une bulle à l'activation et à la désactivation.", gmNotify)), "Pendant le Mode Jeu"));
        stack.Controls.Add(new Card(apps, "Programmes de fond"));

        var save = new ModernButton("Enregistrer", primary: true);
        save.Click += (_, _) => ApplyGameSettings();
        tips.SetToolTip(gameToggle, "Activer ou désactiver le Mode Jeu manuellement (aussi depuis la barre latérale et l'icône de notification)");
        LoadGameSettings();
        UpdateGameStatus(new EngineSnapshot(0, false, Array.Empty<ProcessRow>()));
        return MakePage("Mode Jeu", "Tout pour le jeu, le temps de jouer : plan Performances, ProBalance réactif et programmes de fond calmés. Tout revient à la normale ensuite.",
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
        gmPlan.Items.Add("(inchangé)");
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
        var games = settings.Rules.Where(r => r.IsGame).Select(r => r.Pattern + (r.Enabled ? "" : " (règle inactive)")).ToList();
        gameList.Text = games.Count == 0
            ? "Aucun jeu pour l'instant. Utilisez « Ajouter un jeu… » ou le modèle Jeu d'une règle."
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
    public void ToggleGameMode()
    {
        bool active = lastSnapshot?.GameMode == true;
        if (active && !engine.GameModeManual)
        {
            MessageBox.Show(this,
                $"Le Mode Jeu est actif automatiquement parce que « {lastSnapshot?.GameTrigger} » tourne.\nIl se désactivera tout seul à sa fermeture.\n\n" +
                "Pour ne plus le déclencher automatiquement, désactivez l'option dans la page Mode Jeu.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        engine.SetGameMode(!engine.GameModeManual);
    }

    void UpdateGameStatus(EngineSnapshot snap)
    {
        var key = (snap.GameMode, engine.GameModeManual, snap.GameTrigger, settings.GameMode.Automatic);
        if (lastGameState == key)
            return;
        lastGameState = key;
        gameState.Text = snap.GameMode ? "Mode Jeu actif" : "Mode Jeu inactif";
        gameState.ForeColor = snap.GameMode ? Theme.Current.Accent : Theme.Current.Fore;
        gameDetail.Text = snap.GameMode
            ? (snap.GameTrigger != null ? $"Déclenché par « {snap.GameTrigger} » : il se désactivera à sa fermeture." : "Activé manuellement.")
            : settings.GameMode.Automatic ? "Il s'activera tout seul au lancement d'un jeu reconnu." : "Activation manuelle uniquement.";
        gameToggle.Text = snap.GameMode && engine.GameModeManual ? "Désactiver" : "Activer maintenant";
        gameToggle.Enabled = !(snap.GameMode && !engine.GameModeManual);
    }

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
        var startMenu = new ToggleSwitch { Checked = settings.StartMenuShortcut, Enabled = Updater.IsPublishedBuild };
        if (!Updater.IsPublishedBuild)
            tips.SetToolTip(startMenu, "Disponible uniquement avec l'exe publié (pas en développement).");
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
        var check = new ModernButton("Vérifier maintenant");
        check.Click += (_, _) => CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
        var openFolder = new ModernButton("Ouvrir le dossier");
        openFolder.Click += (_, _) => OpenConfigFolder();
        var showWelcome = new ModernButton("Revoir l'accueil");
        showWelcome.Click += (_, _) =>
        {
            settings.WelcomeDismissed = false;
            SaveSettings();
            if (welcome != null)
                welcome.Visible = true;
            else
                MessageBox.Show(this, "L'accueil s'affichera au prochain lancement de Corral.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            ShowPage("Processus");
        };

        var shortcuts = new Label
        {
            AutoSize = true,
            Tag = Theme.HintTag,
            MaximumSize = new Size(640, 0),
            Text = "Ctrl+F : rechercher un processus · Ctrl+N : nouvelle règle · Ctrl+1 à 8 : changer de page · " +
                   "F5 : actualiser · Suppr / Entrée / Espace : supprimer, modifier, activer la règle sélectionnée",
        };

        var stack = new CardStack();
        stack.Controls.Add(new Card(Rows(
            SettingRow("Thème", "« Système » suit le mode clair ou sombre de Windows.", themeChoice)), "Apparence"));
        stack.Controls.Add(new Card(Rows(
            SettingRow("Lancer Corral à l'ouverture de session", "Démarre réduit dans la zone de notification, avec les droits administrateur.", autoStart),
            SettingRow("Afficher Corral dans le menu Démarrer", "Pour le retrouver avec la recherche Windows. Le raccourci suit l'exe s'il est déplacé.", startMenu)), "Démarrage"));
        stack.Controls.Add(new Card(Rows(
            SettingRow("Rechercher les mises à jour automatiquement", "Au démarrage, puis toutes les 6 heures.", updates),
            SettingRow($"Version {Updater.CurrentVersion.ToString(3)}",
                Updater.IsSupported ? "Mises à jour publiées sur GitHub." : $"Mises à jour indisponibles : {Updater.UnsupportedReason}.", check)), "Mises à jour"));
        stack.Controls.Add(new Card(Rows(
            SettingRow("Message d'accueil", "Les trois choses à savoir pour bien démarrer.", showWelcome),
            shortcuts), "Aide"));
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
        return MakePage("Options", "Apparence, démarrage, mises à jour et aide.", stack);
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
        logList.Columns.Add("Heure", 90);
        logList.Columns.Add("Type", 130);
        logList.Columns.Add("Message", 640);
        Theme.StyleList(logList, (item, col) =>
            col == 1 && item.Tag is LogEntry entry ? new Theme.CellStyle(Text: "● " + item.SubItems[1].Text, Fore: LogColor(entry)) : null);

        var filters = new (string Label, Func<LogEntry, bool> Filter)[]
        {
            ("Tout", _ => true),
            ("Règles", e => e.Category == LogCategory.Rule),
            ("ProBalance", e => e.Category == LogCategory.ProBalance),
            ("Erreurs", e => e.Level != LogLevel.Info),
        };
        foreach (var (label, filter) in filters)
        {
            var b = new ModernButton(label) { Toggled = label == "Tout" };
            b.Click += (_, _) =>
            {
                foreach (var (other, _) in logFilters)
                    other.Toggled = other == b;
                logFilter = filter;
                RefreshLog(force: true);
            };
            logFilters.Add((b, filter));
        }
        var open = new ModernButton("Ouvrir le dossier");
        open.Click += (_, _) => OpenConfigFolder();
        tips.SetToolTip(open, "Le journal complet est enregistré dans corral.log");

        var actions = logFilters.Select(f => (Control)f.Button).Append(open).ToArray();
        return MakePage("Journal", "Ce que Corral a fait : règles appliquées, interventions de ProBalance, erreurs. Les plus récents en haut.",
            new Card(logList, fill: true), actions);
    }

    static string CategoryLabel(LogEntry e) => e.Level switch
    {
        LogLevel.Error => "Erreur",
        LogLevel.Warning when e.Category == LogCategory.General => "Avertissement",
        _ => e.Category switch
        {
            LogCategory.Rule => "Règle",
            LogCategory.ProBalance => "ProBalance",
            LogCategory.Power => "Alimentation",
            LogCategory.Update => "Mise à jour",
            _ => "Général",
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
            4 => string.Compare(a.Rule, b.Rule, StringComparison.OrdinalIgnoreCase),
            5 => a.Restrained.CompareTo(b.Restrained),
            _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        if (c == 0 && Column != 0)
            c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        return Descending ? -c : c;
    }
}
