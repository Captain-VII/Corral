using System.Diagnostics;
using Corral.Core;
using Corral.Models;

namespace Corral.Tests;

public class AutomationTests : IDisposable
{
    sealed class NoPower : IPowerPlanApi
    {
        public Guid? GetActive() => null;
        public bool SetActive(Guid plan) => true;
    }

    readonly string dir = Path.Combine(Path.GetTempPath(), "CorralAuto_" + Guid.NewGuid().ToString("N"));
    readonly List<Process> started = new();
    DateTime clock = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        foreach (var p in started)
            try { if (!p.HasExited) p.Kill(); } catch { }
        try { Directory.Delete(dir, true); } catch { }
    }

    Process Start(string name)
    {
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, name + ".exe");
        if (!File.Exists(exe))
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        var p = Process.Start(new ProcessStartInfo(exe, "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        started.Add(p);
        return p;
    }

    Engine NewEngine(Rule rule, List<string>? notes = null)
    {
        var s = new Settings();
        s.ProBalance.Enabled = false;
        s.Rules.Add(rule);
        var e = new Engine(s, new NoPower(), foregroundPid: () => -1, clock: () => clock);
        return e;
    }

    [Fact]
    public void BlockedProgramIsClosed()
    {
        var p = Start("corral_block_test");
        var engine = NewEngine(new Rule { Pattern = "corral_block_test", Block = BlockMode.Always });
        engine.TickOnce();
        Assert.True(p.WaitForExit(3000));
        engine.Stop();
    }

    [Fact]
    public void SingleInstanceKeepsTheOldest()
    {
        var first = Start("corral_single_test");
        Thread.Sleep(50);
        var second = Start("corral_single_test");
        var engine = NewEngine(new Rule { Pattern = "corral_single_test", Block = BlockMode.SingleInstance });
        engine.TickOnce();
        Assert.True(second.WaitForExit(3000));
        Assert.False(first.HasExited);
        engine.Stop();
    }

    [Fact]
    public void CatchAllPatternsNeverBlock()
    {
        Assert.True(Engine.IsCatchAll("*"));
        Assert.True(Engine.IsCatchAll("*.exe"));
        Assert.True(Engine.IsCatchAll(" ?* "));
        Assert.False(Engine.IsCatchAll("jeu*"));
    }

    [Fact]
    public void MemoryAlertWaitsForTheDurationThenClosesOnce()
    {
        // Copie de cmd.exe en attente (≈ 7 Mo de mémoire privée, au-dessus du seuil de 1 Mo), sans processus enfant
        using var procs = new TestProcesses();
        var p = procs.Cmd("corral_alert_test");
        Thread.Sleep(300);

        var engine = NewEngine(new Rule { Pattern = "corral_alert_test", AlertMemoryMB = 1, AlertMinutes = 2, AlertAction = AlertAction.Close });
        var notes = new List<string>();
        engine.Notification += (_, message) => notes.Add(message);

        engine.TickOnce();
        clock = clock.AddMinutes(1);
        engine.TickOnce();
        Assert.False(p.HasExited); // seuil dépassé depuis 1 min seulement

        clock = clock.AddMinutes(1.5);
        engine.TickOnce();
        Assert.True(p.WaitForExit(3000));
        Assert.Contains(Log.Recent(), e => e.Message.Contains("Surveillance") && e.Message.Contains("corral_alert_test") && e.Message.Contains("fermé"));
        engine.Stop();
    }

    [Fact]
    public void KeepAwakeFollowsTheProgram()
    {
        var p = Start("corral_awake_test");
        var engine = NewEngine(new Rule { Pattern = "corral_awake_test", KeepAwake = true });
        engine.TickOnce();
        Assert.Contains("corral_awake_test", engine.KeepAwakeReason);

        p.Kill();
        p.WaitForExit();
        engine.TickOnce();
        Assert.Null(engine.KeepAwakeReason);
        engine.Stop();
    }

    [Fact]
    public void KeepAwakeRequestCanBeSetAndCleared()
    {
        using var k = new KeepAwake();
        k.Set("Corral : test");
        Assert.True(k.Active);
        k.Set(null);
        Assert.False(k.Active);
    }
}
