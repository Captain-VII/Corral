using Corral.Core;
using Corral.Models;

namespace Corral.UI;

// Page « Graphique » : historiques CPU et mémoire, programmes les plus gourmands.
public sealed partial class MainForm
{
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
}
