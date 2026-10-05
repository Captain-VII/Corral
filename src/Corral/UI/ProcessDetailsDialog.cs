using System.Diagnostics;
using Corral.Core;

namespace Corral.UI;

/// <summary>Fiche d'un processus : emplacement, éditeur, ligne de commande, parent, ressources, règle.</summary>
public sealed class ProcessDetailsDialog : Form
{
    readonly ToolTip tips = Theme.CreateToolTip();

    public ProcessDetailsDialog(ProcessDetails d, ProcessRow? row, bool canCreateRule, ProcessHistory? history = null)
    {
        Text = $"Détails — {d.Name}";
        Font = Ui.Base;
        Icon = AppIcon.Load(new Size(32, 32));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(20, 12, 20, 8) };
        void Section(string title)
        {
            var l = new Label { Text = title, Font = Ui.Section, AutoSize = true, Margin = new Padding(0, grid.RowCount == 0 ? 4 : 16, 0, 6) };
            grid.Controls.Add(l, 0, grid.RowCount);
            grid.SetColumnSpan(l, 2);
            grid.RowCount++;
        }
        void Row(string label, string? value)
        {
            int r = grid.RowCount++;
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Tag = Theme.HintTag, Margin = new Padding(0, 4, 20, 4) }, 0, r);
            // Zone de texte sans bordure : la valeur se sélectionne et se copie
            var box = new TextBox
            {
                Text = value ?? "—",
                ReadOnly = true,
                Tag = Theme.FlatTag,
                Width = 460,
                Margin = new Padding(0, 4, 0, 4),
                Multiline = value != null && value.Length > 60,
                WordWrap = true,
            };
            if (box.Multiline)
                box.Height = Math.Min(80, 20 * (1 + value!.Length / 60));
            grid.Controls.Add(box, 1, r);
        }

        Section("Programme");
        Row("Nom", d.Name);
        Row("Description", d.Description);
        Row("Éditeur", d.Company);
        Row("Version", d.Version);
        Row("Emplacement", d.Path ?? "inaccessible (processus protégé)");
        Row("Ligne de commande", d.CommandLine);

        Section("Processus");
        Row("PID", d.Pid.ToString());
        Row("Lancé par", d.ParentPid is { } pp ? (d.ParentName != null ? $"{d.ParentName} (PID {pp})" : $"PID {pp} (terminé)") : null);
        Row("Démarré le", d.StartTime?.ToString("dddd d MMMM yyyy à HH:mm:ss"));
        Row("Priorité", d.Priority);
        Row("Temps processeur", d.CpuTime is { } t ? $"{(int)t.TotalHours} h {t.Minutes:00} min {t.Seconds:00} s" : null);
        Row("Mémoire", d.WorkingSet is { } ws ? $"{ws / (1024 * 1024):N0} Mo en RAM · {(d.PrivateBytes ?? 0) / (1024 * 1024):N0} Mo privés" : null);
        Row("Threads / handles", d.Threads is { } th ? $"{th} threads · {d.Handles} handles" : null);

        if (history != null)
            AddActivity(grid, history, d.Pid, row?.Name ?? d.Name);

        Section("Corral");
        Row("Règle", row?.Rule != null ? $"« {row.Rule} » : {row.RuleSummary}" : "aucune");
        Row("ProBalance", row?.Restrained == true ? "abaissé temporairement (il saturait le processeur)" : "pas d'intervention en cours");

        var close = new ModernButton("Fermer", primary: true) { DialogResult = DialogResult.OK };
        var copy = new ModernButton("Copier");
        copy.Click += (_, _) =>
        {
            Clipboard.SetText(string.Join(Environment.NewLine, new[]
            {
                $"{d.Name} (PID {d.Pid})", d.Path, d.CommandLine, d.Description, d.Company, d.Version,
            }.Where(s => !string.IsNullOrEmpty(s))));
            copy.Text = "Copié ✓";
        };
        tips.SetToolTip(copy, "Copier le nom, l'emplacement et la ligne de commande");
        var open = new ModernButton("Ouvrir l'emplacement") { Enabled = d.Path != null };
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{d.Path}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Ouverture de l'emplacement", ex); }
        };
        var rule = new ModernButton("Créer une règle") { Enabled = canCreateRule };
        rule.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
            CreateRuleRequested = true;
        };

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 18, 0, 0) };
        buttons.Controls.AddRange(new Control[] { close, rule, open, copy });
        grid.Controls.Add(buttons, 0, grid.RowCount);
        grid.SetColumnSpan(buttons, 2);
        Controls.Add(grid);
        AcceptButton = close;
        CancelButton = close;
        Theme.Apply(this);
    }

    /// <summary>Quatre courbes (processeur, GPU, mémoire, disque) rafraîchies chaque seconde tant que la fiche est ouverte.</summary>
    void AddActivity(TableLayoutPanel grid, ProcessHistory history, int pid, string name)
    {
        var title = new Label { Text = $"Activité ({history.Keep.TotalMinutes:0} dernières minutes)", Font = Ui.Section, AutoSize = true, Margin = new Padding(0, 16, 0, 6) };
        grid.Controls.Add(title, 0, grid.RowCount);
        grid.SetColumnSpan(title, 2);
        grid.RowCount++;

        var cpu = new MiniChart { Title = "Processeur", Format = v => $"{v:0.0} %" };
        var gpu = new MiniChart { Title = "GPU" };
        var mem = new MiniChart { Title = "Mémoire", Max = null, Format = v => $"{v / (1 << 20):N0} Mo" };
        var io = new MiniChart { Title = "Disque et réseau (E/S)", Max = null, Format = Units.Rate };
        tips.SetToolTip(io, "Octets lus et écrits par le programme : fichiers, mais aussi réseau et périphériques");
        tips.SetToolTip(gpu, "Moteur graphique le plus sollicité par ce programme (3D, vidéo, copie…), comme dans le Gestionnaire des tâches");
        var charts = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
        foreach (var (c, i) in new[] { cpu, gpu, mem, io }.Select((c, i) => (c, i)))
        {
            c.Range = history.Keep;
            c.Margin = new Padding(0, 0, i % 2 == 0 ? 10 : 0, 10);
            charts.Controls.Add(c, i % 2, i / 2);
        }
        grid.Controls.Add(charts, 0, grid.RowCount);
        grid.SetColumnSpan(charts, 2);
        grid.RowCount++;

        void Refresh()
        {
            var s = history.Get(pid, name);
            cpu.SetData(s.Select(x => (x.Time, (double)x.Cpu)).ToList());
            gpu.SetData(s.Select(x => (x.Time, (double)x.Gpu)).ToList());
            mem.SetData(s.Select(x => (x.Time, (double)x.Memory)).ToList());
            io.SetData(s.Select(x => (x.Time, (double)x.Io)).ToList());
        }
        Refresh();
        refresh.Tick += (_, _) => Refresh();
        refresh.Start();
    }

    readonly System.Windows.Forms.Timer refresh = new() { Interval = 1000 };

    /// <summary>L'utilisateur a cliqué sur « Créer une règle » (traité par l'appelant une fois la fiche fermée).</summary>
    public bool CreateRuleRequested { get; private set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            refresh.Dispose();
            tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
