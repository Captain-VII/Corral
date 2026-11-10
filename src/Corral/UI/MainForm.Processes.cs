using System.Collections;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using Corral.Core;
using Corral.Models;

namespace Corral.UI;

// Page « Processus » : liste des processus, menu contextuel, actions ponctuelles.
public sealed partial class MainForm
{
    readonly BufferedListView procList = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true };
    readonly Dictionary<string, ListViewItem> procItems = new();
    readonly ProcessSorter sorter = new();
    readonly TextBox search = new() { PlaceholderText = Tr("Rechercher un processus", "Search processes"), Width = 240, Margin = new Padding(4, 5, 8, 0), AccessibleName = Tr("Rechercher un processus", "Search processes") };
    readonly ModernButton createRule = new(Tr("Créer une règle", "Create a rule")) { Enabled = false };
    readonly ModernButton detailsButton = new(Tr("Détails", "Details")) { Enabled = false };
    readonly UndoBanner processBanner = new();
    readonly Dictionary<string, Image> iconCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Image genericIcon = ScaleIcon(SystemIcons.Application);
    Panel? welcome;
    EngineSnapshot? lastSnapshot;        // dernier affiché (thread de l'interface)
    volatile EngineSnapshot? latestSnapshot; // dernier reçu, même fenêtre cachée

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
            processBanner.Show(Tr($"Priorité de « {row.Name} » passée à « {RuleText.PriorityLabel(priority)} » jusqu'à sa fermeture.", $"“{row.Name}” priority set to “{RuleText.PriorityLabel(priority)}” until it closes."));
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
        if (Policies.Current.DisableProcessTermination)
        {
            MessageBox.Show(this, Tr("Votre organisation ne permet pas de terminer des processus depuis Corral.", "Your organization does not allow ending processes from Corral."),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
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
        if (IsPageVisible("ProBalance"))
            pbSessions.Text = snap.Sessions is { Count: > 1 } sessions
                ? Tr("Sessions ouvertes : ", "Open sessions: ") + string.Join(" · ", sessions.Select(x =>
                    $"{x.User} {x.Cpu:0} %" + (x.Limited ? Tr(" (abaissée)", " (lowered)") : "")))
                : Tr("Une seule session ouverte : l'équité n'a rien à faire.", "Only one session open: fairness has nothing to do.");
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
