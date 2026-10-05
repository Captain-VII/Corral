using System.Diagnostics;
using Corral.Core;
using Corral.Models;

namespace Corral.Tests;

public class MonitoringTests : IDisposable
{
    readonly TestProcesses procs = new();
    readonly string dir = Path.Combine(Path.GetTempPath(), "CorralMon_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        procs.Dispose();
        try { Directory.Delete(dir, true); } catch { }
    }

    static ProcessRow Row(string name, double cpu, long mem) => new(1, name, cpu, mem, null, false);

    [Fact]
    public void TopRanksByAverageCpuAndPeakMemory()
    {
        var top = new TopTracker(TimeSpan.FromMinutes(15));
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        // « jeu » : 2 instances additionnées ; « navigateur » : pic de mémoire au 2e passage
        top.Add(t0, new[] { Row("jeu", 30, 100), Row("jeu", 10, 100), Row("navigateur", 20, 500) });
        top.Add(t0.AddSeconds(1), new[] { Row("jeu", 20, 100), Row("navigateur", 0, 900) });

        var cpu = top.Top(t0, TopTracker.Metric.Cpu);
        Assert.Equal("jeu", cpu[0].Name);
        Assert.Equal(30, cpu[0].Value, 3);       // (40 + 20) / 2
        Assert.Equal(10, cpu[1].Value, 3);       // (20 + 0) / 2

        var mem = top.Top(t0, TopTracker.Metric.Memory);
        Assert.Equal(("navigateur", 900.0), (mem[0].Name, mem[0].Value));

        Assert.Single(top.Top(t0.AddSeconds(1), TopTracker.Metric.Cpu)); // seul « jeu » consomme au 2e passage
    }

    [Fact]
    public void TopForgetsOldSamples()
    {
        var top = new TopTracker(TimeSpan.FromMinutes(1));
        var t0 = new DateTime(2026, 1, 1);
        top.Add(t0, new[] { Row("ancien", 50, 1) });
        top.Add(t0.AddMinutes(5), new[] { Row("récent", 5, 1) });
        Assert.Equal(new[] { "récent" }, top.Top(DateTime.MinValue, TopTracker.Metric.Cpu).Select(e => e.Name));
    }

    [Fact]
    public void ProBalanceStatsCountPerDayAndPersist()
    {
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "stats.json");
        var day = new DateOnly(2026, 3, 10);
        var stats = new ProBalanceStats(file, () => day);
        stats.Record(new[] { "jeu", "navigateur" });
        stats.Record(new[] { "jeu" });
        day = day.AddDays(1);
        stats.Record(new[] { "compilateur" });

        Assert.Equal(1, stats.CountToday());
        Assert.Equal(4, stats.Count(7));
        Assert.Equal(("jeu", 2), stats.TopPrograms(7)[0]);

        // Relecture du fichier
        var reloaded = new ProBalanceStats(file, () => day);
        Assert.Equal(4, reloaded.Count(7));
        Assert.Equal(1, reloaded.Count(1));

        day = day.AddDays(40); // tout est sorti de la fenêtre de 30 jours
        Assert.Equal(0, reloaded.Count(30));
        reloaded.Reset();
        Assert.Equal(0, new ProBalanceStats(file, () => day).Count(400));
    }

    [Fact]
    public void ProcessDetailsAreRead()
    {
        var name = procs.Name("details");
        var p = procs.Ping(name);
        var d = ProcessDetails.Read(p.Id);
        Assert.Equal(name, d.Name);
        Assert.EndsWith(name + ".exe", d.Path);
        Assert.Contains("-n 60 127.0.0.1", d.CommandLine);
        Assert.Equal(Environment.ProcessId, d.ParentPid); // lancé par le processus de test
        Assert.NotNull(d.StartTime);
        Assert.True(d.Threads > 0);
        Assert.Equal("Microsoft Corporation", d.Company);
    }

    [Fact]
    public void SnapshotIncludesMemory()
    {
        var s = new Settings();
        s.ProBalance.Enabled = false;
        var engine = new Engine(s, new NullPower(), foregroundPid: () => -1);
        var snap = engine.TickOnce();
        Assert.True(snap.MemoryTotal > 0);
        Assert.InRange(snap.MemoryPercent, 1, 100);
        engine.Stop();
    }

    sealed class NullPower : IPowerPlanApi
    {
        public Guid? GetActive() => null;
        public bool SetActive(Guid plan) => true;
    }
}
