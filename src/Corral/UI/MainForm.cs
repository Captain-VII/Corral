using Corral.Core;
using Corral.Models;

namespace Corral.UI;

public sealed partial class MainForm : Form
{
    readonly IEngine engine;
    readonly ISettingsStore store;
    readonly Settings settings; // copie détenue par l'interface ; le moteur reçoit des clones

    readonly NavBar nav = new();
    readonly Panel pageHost = new() { Dock = DockStyle.Fill };
    readonly List<(string Title, Control Page)> pages = new();
    readonly ToolTip tips = Theme.CreateToolTip();

    IReadOnlyList<PowerPlanInfo>? plans;

    volatile bool shown;
    bool allowClose;
    bool loading = true;

    /// <summary>« Vérifier maintenant » : traité par l'icône de notification, qui gère les mises à jour.</summary>
    public event EventHandler? CheckUpdatesRequested;

    /// <summary>Bouton Pause de la barre latérale ; l'icône de notification garde l'état de référence.</summary>
    public event Action<bool>? PauseRequested;

    /// <summary>Les raccourcis globaux ont changé : l'icône de notification les réenregistre.</summary>
    public event Action? HotkeysChanged;

    public MainForm(IEngine engine, ISettingsStore store, Settings settings)
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
        A11y.NameIconButtons(this, tips);
        chart.AccessibleName = Tr("Historique du processeur", "CPU history");
        memChart.AccessibleName = Tr("Historique de la mémoire", "Memory history");
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
        if (Policies.Current.Locks(title, settings))
        {
            // Réglages imposés par l'organisation : visibles mais en lecture seule
            foreach (Control c in page.Controls)
                c.Enabled = false;
            page.Controls.Add(new Label
            {
                Text = Policies.StandardUser && !Policies.Current.LockSettings
                    ? Tr("🔒 Réservé aux administrateurs de ce PC : ces réglages sont en lecture seule.", "🔒 Restricted to administrators of this PC: these settings are read-only.")
                    : Tr("🔒 Géré par votre organisation : ces réglages sont en lecture seule.", "🔒 Managed by your organization: these settings are read-only."),
                Dock = DockStyle.Top,
                AutoSize = false,
                Height = 30,
                Tag = Theme.HintTag,
                Padding = new Padding(28, 8, 0, 0),
            });
        }
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

    /// <summary>Formulaire « Bug » du dépôt (.github/ISSUE_TEMPLATE/bug.yml), champs version et windows pré-remplis.</summary>
    public static string BugReportUrl()
    {
        string windows = Environment.OSVersion.Version.ToString();
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            int build = Environment.OSVersion.Version.Build;
            windows = $"Windows {(build >= 22000 ? 11 : 10)} {key?.GetValue("DisplayVersion")} ({build}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})";
        }
        catch { }
        var repo = Updater.Repository ?? "Captain-VII/Corral";
        return $"https://github.com/{repo}/issues/new?template=bug.yml" +
               $"&version={Uri.EscapeDataString(Updater.CurrentVersion.ToString(3))}&windows={Uri.EscapeDataString(windows)}";
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
        A11y.Label(control, label, description);
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
        try { autoStart.Checked = Installation.IsMsi || AutoStart.IsEnabled(); }
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

    static NumericUpDown Num(int min, int max) => new() { Minimum = min, Maximum = max, Width = 72, TextAlign = HorizontalAlignment.Right };

    static void SetNum(NumericUpDown n, double value) =>
        n.Value = Math.Clamp((decimal)value, n.Minimum, n.Maximum);
}
