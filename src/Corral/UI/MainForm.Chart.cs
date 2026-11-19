using Corral.Core;
using Corral.Models;

namespace Corral.UI;

// Page « Graphique » : à gauche les mesures de tout le PC (processeur, mémoire, GPU, disques, réseau) avec leur petite courbe ;
// à droite le détail de la mesure choisie (grande courbe, chiffres, répartition, programmes les plus gourmands).
public sealed partial class MainForm
{
    enum Gauge { Cpu, Memory, Gpu, Disk, Network }

    static readonly TimeSpan Kept = TimeSpan.FromMinutes(15);
    readonly CpuHistory cpuHistory = new(Kept);
    readonly CpuHistory memHistory = new(Kept);
    readonly CpuHistory gpuHistory = new(Kept);
    readonly CpuHistory diskHistory = new(Kept, double.MaxValue), diskRead = new(Kept, double.MaxValue), diskWrite = new(Kept, double.MaxValue);
    readonly CpuHistory netHistory = new(Kept, double.MaxValue), netReceived = new(Kept, double.MaxValue), netSent = new(Kept, double.MaxValue);
    readonly CpuChart chart;
    readonly MiniChart[] gauges = Enum.GetValues<Gauge>().Select(_ => new MiniChart { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8), Selectable = true }).ToArray();
    readonly StatGrid statGrid = new();
    readonly StatGrid threadGrid = new() { TileWidth = 64 };
    readonly TopList breakdown = new();
    readonly TopList topList = new();
    Card threadCard = null!, breakdownCard = null!, topCard = null!;
    CardStack detail = null!;
    Gauge gauge = Gauge.Cpu;
    double? cpuThreshold;
    readonly SystemSampler systemSampler = new();
    volatile SystemSampler.Sample? lastSystem;
    volatile bool active; // fenêtre au premier plan : le GPU (compteurs coûteux) n'est lu qu'alors, comme dans le moteur
    readonly TopTracker topTracker = new(Kept);
    readonly ProcessHistory processHistory = new(TimeSpan.FromMinutes(5));
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
                chart.Range = TimeSpan.FromMinutes(minutes);
                if (lastSnapshot != null)
                    RefreshCharts(lastSnapshot);
            };
            tips.SetToolTip(b, Tr($"Afficher les {minutes} dernières minutes", $"Show the last {minutes} minutes"));
            ranges.Add(b);
        }

        // Colonne de gauche : une ligne par mesure, cliquable
        var list = new TableLayoutPanel { ColumnCount = 1, RowCount = gauges.Length, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 14, 0) };
        list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var g in Enum.GetValues<Gauge>())
        {
            var mini = gauges[(int)g];
            mini.Title = GaugeName(g);
            if (g is Gauge.Disk or Gauge.Network)
            {
                mini.Max = null;
                mini.Format = Units.Rate;
            }
            mini.Click += (_, _) => SelectGauge(g);
            list.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / gauges.Length));
            list.Controls.Add(mini, 0, (int)g);
        }
        tips.SetToolTip(gauges[(int)Gauge.Gpu], Tr("Moteur de la carte graphique le plus chargé. Mesuré seulement quand Corral est au premier plan (lecture coûteuse).",
            "Busiest engine of the graphics card. Measured only while Corral is in the foreground (costly to read)."));
        tips.SetToolTip(gauges[(int)Gauge.Disk], Tr("Lecture (L) et écriture (É), tous disques physiques confondus", "Reads (R) and writes (W), all physical disks combined"));
        tips.SetToolTip(gauges[(int)Gauge.Network], Tr("Reçu (↓) et envoyé (↑), toutes cartes réseau confondues", "Received (↓) and sent (↑), all network adapters combined"));
        Activated += (_, _) => active = true;
        Deactivate += (_, _) => active = false;

        // Colonne de droite : détail de la mesure choisie, qui défile si la fenêtre est petite
        chart.Size = new Size(400, 320);
        threadCard = new Card(threadGrid, Tr("Par processeur logique", "Per logical processor"));
        breakdownCard = new Card(breakdown, "");
        topCard = new Card(topList, Tr("Les plus gourmands", "Top consumers"));
        detail = new CardStack { Dock = DockStyle.Fill, Margin = new Padding(0) };
        detail.Controls.Add(new Card(chart) { Margin = new Padding(0, 0, 0, 14) });
        detail.Controls.Add(new Card(statGrid));
        detail.Controls.Add(threadCard);
        detail.Controls.Add(breakdownCard);
        detail.Controls.Add(topCard);

        var grid = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(list, 0, 0);
        grid.Controls.Add(detail, 1, 0);
        SelectGauge(Gauge.Cpu);

        return MakePage(Tr("Graphique", "Charts"), Tr("Processeur, mémoire, carte graphique, disques et réseau de tout le PC. Choisissez une mesure à gauche pour voir son détail ; survolez la courbe pour lire une valeur.",
            "CPU, memory, GPU, disks and network of the whole PC. Pick a measure on the left to see its details; hover the curve to read a value."),
            grid, ranges.ToArray());
    }

    static string GaugeName(Gauge g) => g switch
    {
        Gauge.Cpu => Tr("Processeur", "CPU"),
        Gauge.Memory => Tr("Mémoire", "Memory"),
        Gauge.Gpu => Tr("Carte graphique", "GPU"),
        Gauge.Disk => Tr("Disques", "Disks"),
        _ => Tr("Réseau", "Network"),
    };

    /// <summary>Affiche le détail d'une mesure (0 = processeur … 4 = réseau) ; public pour les captures de test.</summary>
    public void SelectGauge(int index) => SelectGauge((Gauge)index);

    void SelectGauge(Gauge g)
    {
        gauge = g;
        for (int i = 0; i < gauges.Length; i++)
            gauges[i].Selected = i == (int)g;
        ConfigureChart();
        if (lastSnapshot != null)
            RefreshCharts(lastSnapshot);
    }

    /// <summary>Règle la grande courbe sur la mesure choisie (historique, séries, unité, seuil).</summary>
    void ConfigureChart()
    {
        chart.Title = GaugeName(gauge);
        chart.AccessibleName = Tr($"Historique : {GaugeName(gauge)}", $"History: {GaugeName(gauge)}");
        chart.History = gauge switch
        {
            Gauge.Cpu => cpuHistory,
            Gauge.Memory => memHistory,
            Gauge.Gpu => gpuHistory,
            Gauge.Disk => diskHistory,
            _ => netHistory,
        };
        chart.Series = gauge switch
        {
            Gauge.Disk => new[] { (diskRead, Tr("Lecture", "Read")), (diskWrite, Tr("Écriture", "Write")) },
            Gauge.Network => new[] { (netReceived, Tr("Reçu", "Received")), (netSent, Tr("Envoyé", "Sent")) },
            _ => null,
        };
        bool rate = gauge is Gauge.Disk or Gauge.Network;
        chart.Max = rate ? null : 100;
        chart.Format = rate ? Units.Rate : v => $"{v:0} %";
        chart.Threshold = gauge == Gauge.Cpu ? cpuThreshold : null;
        threadCard.Visible = gauge == Gauge.Cpu;
        breakdownCard.Visible = gauge != Gauge.Cpu;
        topCard.Title = gauge == Gauge.Disk ? Tr("Les plus gourmands (E/S, réseau compris)", "Top consumers (I/O, network included)") : Tr("Les plus gourmands", "Top consumers");
        chart.Invalidate();
    }

    /// <summary>Mesure GPU, disques et réseau ; appelé à chaque instantané du moteur, hors du thread de l'interface.</summary>
    void SampleSystem(DateTime t, bool record)
    {
        if (systemSampler.Read(withGpu: active) is not { } s)
            return;
        lastSystem = s;
        if (!record)
            return; // première mesure : simple référence pour les débits
        if (s.Gpu is { } gpu)
            gpuHistory.Add(t, gpu);
        diskRead.Add(t, s.DiskRead);
        diskWrite.Add(t, s.DiskWrite);
        diskHistory.Add(t, s.DiskRead + s.DiskWrite);
        netReceived.Add(t, s.NetReceived);
        netSent.Add(t, s.NetSent);
        netHistory.Add(t, s.NetReceived + s.NetSent);
    }

    /// <summary>Met à jour la page (thread de l'interface, page affichée).</summary>
    void RefreshCharts(EngineSnapshot snap)
    {
        var from = DateTime.UtcNow - chart.Range;
        var histories = new[] { cpuHistory, memHistory, gpuHistory, diskHistory, netHistory };
        for (int i = 0; i < gauges.Length; i++)
        {
            gauges[i].Range = chart.Range;
            gauges[i].SetData(histories[i].Since(from));
        }
        var s = lastSystem;
        gauges[(int)Gauge.Memory].Caption = snap.MemoryTotal > 0 ? $"{FormatBytes(snap.MemoryUsed)} / {FormatBytes(snap.MemoryTotal)}" : null;
        gauges[(int)Gauge.Disk].Caption = s == null ? null : Tr($"L {Units.Rate(s.DiskRead)}   É {Units.Rate(s.DiskWrite)}", $"R {Units.Rate(s.DiskRead)}   W {Units.Rate(s.DiskWrite)}");
        gauges[(int)Gauge.Network].Caption = s == null ? null : $"↓ {Units.Rate(s.NetReceived)}   ↑ {Units.Rate(s.NetSent)}";
        RefreshDetail(snap, s, systemSampler.ReadDetails(), SystemSampler.ReadPerformance(), from);
        chart.Invalidate();
        detail.PerformLayout();
    }

    void RefreshDetail(EngineSnapshot snap, SystemSampler.Sample? s, SystemSampler.Details? d, SystemSampler.Performance? perf, DateTime from)
    {
        const string None = "—";
        string Bytes(long? b) => b is { } v ? FormatBytes(v) : None;
        var tiles = new List<(string, string, double?)>();
        var rows = new List<(string Name, double Value, string Text)>();
        breakdown.FullValue = 100;
        breakdown.EmptyText = Tr("Collecte des mesures…", "Collecting data…");
        switch (gauge)
        {
            case Gauge.Cpu:
            {
                int threads = Environment.ProcessorCount, cores = CpuTopology.Current?.Cores.Count ?? threads;
                chart.Caption = Join(Hardware.CpuName, Tr($"{cores} cœurs, {threads} threads", $"{cores} cores, {threads} threads"));
                tiles.Add((Tr("Fréquence", "Speed"), d?.FrequencyMHz is { } mhz ? $"{mhz / 1000:0.00} GHz" : None, null));
                tiles.Add((Tr("Processus", "Processes"), perf?.Processes.ToString("N0") ?? None, null));
                tiles.Add((Tr("Threads", "Threads"), perf?.Threads.ToString("N0") ?? None, null));
                tiles.Add((Tr("Handles", "Handles"), perf?.Handles.ToString("N0") ?? None, null));
                threadGrid.SetItems((d?.Threads ?? Array.Empty<double>()).Select((v, i) => ($"{i}", $"{v:0} %", (double?)(v / 100))).ToList());
                break;
            }
            case Gauge.Memory:
            {
                chart.Caption = snap.MemoryTotal > 0 ? Tr($"{FormatBytes(snap.MemoryUsed)} utilisés sur {FormatBytes(snap.MemoryTotal)}", $"{FormatBytes(snap.MemoryUsed)} used of {FormatBytes(snap.MemoryTotal)}") : null;
                long? compressed = snap.Rows.FirstOrDefault(r => r.Name == "Memory Compression")?.MemoryBytes;
                tiles.Add((Tr("Utilisée", "In use"), snap.MemoryTotal > 0 ? FormatBytes(snap.MemoryUsed) : None, null));
                tiles.Add((Tr("Disponible", "Available"), Bytes(perf?.PhysicalAvailable), null));
                tiles.Add((Tr("Validée", "Committed"), perf != null ? $"{FormatBytes(perf.CommitTotal)} / {FormatBytes(perf.CommitLimit)}" : None, null));
                tiles.Add((Tr("En cache", "Cached"), Bytes(perf?.SystemCache), null));
                tiles.Add((Tr("Compressée", "Compressed"), Bytes(compressed), null));
                tiles.Add((Tr("Défauts de page", "Page faults"), d != null ? $"{d.PageFaults:N0}/s" : None, null));
                breakdownCard.Title = Tr("Répartition", "Breakdown");
                if (perf != null && perf.PhysicalTotal > 0)
                {
                    breakdown.FullValue = perf.PhysicalTotal;
                    long used = perf.PhysicalTotal - perf.PhysicalAvailable, cache = Math.Min(perf.SystemCache, perf.PhysicalAvailable);
                    rows.Add((Tr("Programmes et système", "Programs and system"), used, FormatBytes(used)));
                    rows.Add((Tr("Cache (libérable)", "Cache (can be freed)"), cache, FormatBytes(cache)));
                    rows.Add((Tr("Libre", "Free"), perf.PhysicalAvailable - cache, FormatBytes(perf.PhysicalAvailable - cache)));
                }
                break;
            }
            case Gauge.Gpu:
            {
                var (name, vram) = Hardware.Gpu;
                chart.Caption = Join(name, vram > 0 ? FormatBytes(vram) : "");
                tiles.Add((Tr("Mémoire dédiée", "Dedicated memory"), s is { GpuDedicated: > 0 } ? (vram > 0 ? $"{FormatBytes(s.GpuDedicated)} / {FormatBytes(vram)}" : FormatBytes(s.GpuDedicated)) : None, null));
                tiles.Add((Tr("Mémoire partagée", "Shared memory"), s is { GpuDedicated: > 0 } ? FormatBytes(s.GpuShared) : None, null));
                breakdownCard.Title = Tr("Par type de moteur", "Per engine type");
                breakdown.EmptyText = Tr("Mesuré quand Corral est au premier plan.", "Measured while Corral is in the foreground.");
                if (s != null)
                    rows.AddRange(s.GpuEngines.OrderByDescending(e => e.Value).Take(5).Select(e => (EngineName(e.Key), e.Value, $"{e.Value:0} %")));
                break;
            }
            case Gauge.Disk:
                chart.Caption = d != null ? Tr($"{d.Disks.Count} disques physiques", $"{d.Disks.Count} physical disks") : null;
                tiles.Add((Tr("Lecture", "Read"), s != null ? Units.Rate(s.DiskRead) : None, null));
                tiles.Add((Tr("Écriture", "Write"), s != null ? Units.Rate(s.DiskWrite) : None, null));
                tiles.Add((Tr("Activité", "Active time"), d is { Disks.Count: > 0 } ? $"{d.Disks.Max(x => x.Active):0} %" : None, null));
                tiles.Add((Tr("File d'attente", "Queue length"), d != null ? $"{d.Disks.Sum(x => x.Queue):0.00}" : None, null));
                breakdownCard.Title = Tr("Activité par disque", "Active time per disk");
                if (d != null)
                    rows.AddRange(d.Disks.Select(x => (DiskName(x.Name), x.Active, $"{x.Active:0} %")));
                break;
            case Gauge.Network:
            {
                var busiest = d?.Adapters.FirstOrDefault();
                chart.Caption = busiest is { } b ? Join(b.Name, Bandwidth(b.Bandwidth)) : null;
                tiles.Add((Tr("Reçu", "Received"), s != null ? Units.Rate(s.NetReceived) : None, null));
                tiles.Add((Tr("Envoyé", "Sent"), s != null ? Units.Rate(s.NetSent) : None, null));
                tiles.Add((Tr("Utilisation", "Utilization"), d is { Adapters.Count: > 0 } ? $"{d.Adapters.Max(x => Use(x.BytesPerSec, x.Bandwidth)):0.0} %" : None, null));
                breakdownCard.Title = Tr("Utilisation par carte", "Utilization per adapter");
                if (d != null)
                    rows.AddRange(d.Adapters.Take(4).Select(x => (Join(x.Name, Bandwidth(x.Bandwidth)), Use(x.BytesPerSec, x.Bandwidth), $"{Use(x.BytesPerSec, x.Bandwidth):0.0} %")));
                break;
            }
        }
        statGrid.SetItems(tiles);
        breakdown.SetItems(rows);
        RefreshTop(from);
    }

    void RefreshTop(DateTime from)
    {
        if (gauge == Gauge.Network)
        {
            topList.EmptyText = Tr("Windows ne donne pas le débit réseau de chaque programme sans traçage (ETW).", "Windows does not report per-program network traffic without tracing (ETW).");
            topList.SetItems(Array.Empty<(string, double, string)>());
            return;
        }
        var metric = gauge switch
        {
            Gauge.Memory => TopTracker.Metric.Memory,
            Gauge.Gpu => TopTracker.Metric.Gpu,
            Gauge.Disk => TopTracker.Metric.Io,
            _ => TopTracker.Metric.Cpu,
        };
        topList.EmptyText = gauge == Gauge.Gpu ? Tr("Mesuré quand Corral est au premier plan.", "Measured while Corral is in the foreground.") : Tr("Collecte des mesures…", "Collecting data…");
        topList.SetItems(topTracker.Top(from, metric).Select(e => (e.Name, e.Value, metric switch
        {
            TopTracker.Metric.Cpu => $"{e.Value:0.0} %",
            TopTracker.Metric.Gpu => $"{e.Value:0} %",
            TopTracker.Metric.Io => Units.Rate(e.Value),
            _ => FormatBytes((long)e.Value),
        })).ToList());
    }

    static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : $"{a} · {b}";

    static double Use(double bytesPerSec, double bitsPerSec) => bitsPerSec > 0 ? Math.Min(100, bytesPerSec * 8 * 100 / bitsPerSec) : 0;

    static string Bandwidth(double bitsPerSec) =>
        bitsPerSec >= 1e9 ? $"{bitsPerSec / 1e9:0.#} Gbit/s" : $"{bitsPerSec / 1e6:0} Mbit/s";

    static string EngineName(string type) => type switch
    {
        "Copy" => Tr("Copie", "Copy"),
        "VideoDecode" => Tr("Décodage vidéo", "Video decode"),
        "VideoEncode" => Tr("Encodage vidéo", "Video encode"),
        "VideoProcessing" => Tr("Traitement vidéo", "Video processing"),
        _ when type.StartsWith("Compute", StringComparison.Ordinal) => Tr("Calcul", "Compute") + type[7..].Replace('_', ' '),
        _ => type,
    };

    /// <summary>« 0 C: D: » (instance PhysicalDisk) → « Disque 0 (C: D:) · 412 Go libres sur 931 Go ».</summary>
    static string DiskName(string instance)
    {
        var parts = instance.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long free = 0, size = 0;
        foreach (var letter in parts.Skip(1))
        {
            try
            {
                var drive = new DriveInfo(letter);
                if (drive.IsReady)
                {
                    free += drive.TotalFreeSpace;
                    size += drive.TotalSize;
                }
            }
            catch (Exception) { } // lecteur disparu ou illisible : on affiche sans la place libre
        }
        var name = Tr($"Disque {parts.FirstOrDefault()}", $"Disk {parts.FirstOrDefault()}") + (parts.Length > 1 ? $" ({string.Join(' ', parts.Skip(1))})" : "");
        return size > 0 ? Join(name, Tr($"{FormatBytes(free)} libres sur {FormatBytes(size)}", $"{FormatBytes(free)} free of {FormatBytes(size)}")) : name;
    }

    static string FormatBytes(long bytes) =>
        bytes >= 1L << 40 ? $"{bytes / (double)(1L << 40):0.0} {Units.TB}"
        : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} {Units.GB}" : $"{bytes / (1024 * 1024):N0} {Units.MB}";
}
