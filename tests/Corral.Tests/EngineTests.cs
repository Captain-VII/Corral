using System.Diagnostics;
using Corral.Core;
using Corral.Models;

namespace Corral.Tests;

/// <summary>Tests sur de vrais processus (copies de ping.exe à nom unique ; aucun droit admin requis).</summary>
public class EngineTests : IDisposable
{
    sealed class FakePower : IPowerPlanApi
    {
        public Guid Active = Guid.Empty;
        public Guid? GetActive() => Active;
        public bool SetActive(Guid plan)
        {
            Active = plan;
            return true;
        }
    }

    readonly TestProcesses procs = new();
    readonly string name;

    public EngineTests() => name = procs.Name("engine");

    public void Dispose() => procs.Dispose();

    static Settings RuleFor(Rule rule)
    {
        var s = new Settings();
        s.ProBalance.Enabled = false;
        s.Rules.Add(rule);
        return s;
    }

    [Fact]
    public void AppliesRuleThenRestoresOnStop()
    {
        var ping = procs.Ping(name);
        var origPriority = ping.PriorityClass;
        var origAffinity = ping.ProcessorAffinity;
        var plan = Guid.NewGuid();
        var power = new FakePower();
        var engine = new Engine(RuleFor(new Rule
        {
            Pattern = name + ".exe",
            Priority = ProcessPriorityClass.AboveNormal,
            AffinityMask = 1,
            PowerPlan = plan,
        }), power, foregroundPid: () => -1);

        var snap = engine.TickOnce();
        ping.Refresh();
        Assert.Equal(ProcessPriorityClass.AboveNormal, ping.PriorityClass);
        Assert.Equal((IntPtr)1, ping.ProcessorAffinity);
        Assert.Equal(plan, power.Active);
        Assert.Contains(snap.Rows, r => r.Pid == ping.Id && r.Rule == name + ".exe");

        engine.Stop();
        ping.Refresh();
        Assert.Equal(origPriority, ping.PriorityClass);
        Assert.Equal(origAffinity, ping.ProcessorAffinity);
        Assert.Equal(Guid.Empty, power.Active);
    }

    [Fact]
    public void PauseRestoresAndResumeReapplies()
    {
        var ping = procs.Ping(name);
        var orig = ping.PriorityClass;
        var engine = new Engine(RuleFor(new Rule { Pattern = name, Priority = ProcessPriorityClass.BelowNormal }), new FakePower(), foregroundPid: () => -1);
        engine.TickOnce();
        ping.Refresh();
        Assert.Equal(ProcessPriorityClass.BelowNormal, ping.PriorityClass);

        engine.SetPaused(true);
        engine.TickOnce();
        ping.Refresh();
        Assert.Equal(orig, ping.PriorityClass);

        engine.SetPaused(false);
        engine.TickOnce();
        ping.Refresh();
        Assert.Equal(ProcessPriorityClass.BelowNormal, ping.PriorityClass);
        engine.Stop();
    }

    [Fact]
    public void RuleChangeIsAppliedToRunningProcess()
    {
        var ping = procs.Ping(name);
        var engine = new Engine(RuleFor(new Rule { Pattern = name, Priority = ProcessPriorityClass.BelowNormal }), new FakePower(), foregroundPid: () => -1);
        engine.TickOnce();
        engine.UpdateSettings(RuleFor(new Rule { Pattern = name, Priority = ProcessPriorityClass.AboveNormal }));
        engine.TickOnce();
        ping.Refresh();
        Assert.Equal(ProcessPriorityClass.AboveNormal, ping.PriorityClass);
        engine.Stop();
    }

    [Fact]
    public void ExitedProcessRestoresPowerPlan()
    {
        var power = new FakePower();
        var plan = Guid.NewGuid();
        var engine = new Engine(RuleFor(new Rule { Pattern = name, PowerPlan = plan }), power, foregroundPid: () => -1);
        var ping = procs.Ping(name);
        engine.TickOnce();
        Assert.Equal(plan, power.Active);
        ping.Kill();
        ping.WaitForExit();
        engine.TickOnce();
        Assert.Equal(Guid.Empty, power.Active);
        engine.Stop();
    }

    [Fact]
    public void JobLimitsCanBeApplied()
    {
        var ping = procs.Ping(name);
        JobLimiter.Apply(ping, 20, 512);
        Assert.True(JobLimiter.IsInJob(ping));
        Assert.False(ping.HasExited); // le handle du job est fermé sans tuer le processus
    }
}
