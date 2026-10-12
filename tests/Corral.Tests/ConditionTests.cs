using System.Diagnostics;
using Corral.Core;
using Corral.Models;

namespace Corral.Tests;

public class ConditionTests : IDisposable
{
    readonly TestProcesses procs = new();

    public void Dispose() => procs.Dispose();

    sealed class NoPower : IPowerPlanApi
    {
        public Guid? GetActive() => null;
        public bool SetActive(Guid plan) => true;
    }

    // Lundi 12 octobre 2026
    static DateTime Monday(int hour, int minute = 0) => new(2026, 10, 12, hour, minute, 0);

    [Theory]
    [InlineData(9, 0, true)]   // début inclus
    [InlineData(17, 59, true)]
    [InlineData(18, 0, false)] // fin exclue
    [InlineData(8, 59, false)]
    public void DaytimeRange(int hour, int minute, bool expected)
    {
        var c = new RuleCondition { From = new TimeOnly(9, 0), To = new TimeOnly(18, 0) };
        Assert.Equal(expected, c.IsMet(Monday(hour, minute), onBattery: false));
    }

    [Theory]
    [InlineData(23, true)]
    [InlineData(2, true)]
    [InlineData(6, false)]
    [InlineData(12, false)]
    public void RangeAcrossMidnight(int hour, bool expected)
    {
        var c = new RuleCondition { From = new TimeOnly(22, 0), To = new TimeOnly(6, 0) };
        Assert.Equal(expected, c.IsMet(Monday(hour), onBattery: false));
    }

    [Fact]
    public void PowerAndDays()
    {
        var battery = new RuleCondition { Power = PowerSource.Battery };
        Assert.True(battery.IsMet(Monday(12), onBattery: true));
        Assert.False(battery.IsMet(Monday(12), onBattery: false));
        Assert.False(new RuleCondition { Power = PowerSource.AC }.IsMet(Monday(12), onBattery: true));

        var weekend = new RuleCondition { Days = new() { DayOfWeek.Saturday, DayOfWeek.Sunday } };
        Assert.False(weekend.IsMet(Monday(12), false));
        Assert.True(weekend.IsMet(Monday(12).AddDays(5), false));
    }

    [Fact]
    public void NormalizeDropsConditionsThatRestrictNothing()
    {
        Assert.Null(RuleCondition.Normalize(new RuleCondition()));
        Assert.Null(RuleCondition.Normalize(new RuleCondition { Days = Enum.GetValues<DayOfWeek>().ToList() }));
        Assert.Null(RuleCondition.Normalize(new RuleCondition { From = new TimeOnly(9, 0), To = new TimeOnly(9, 0) }));
        var c = RuleCondition.Normalize(new RuleCondition { Days = new() { DayOfWeek.Friday, DayOfWeek.Monday, DayOfWeek.Friday, (DayOfWeek)42 } })!;
        Assert.Equal(new[] { DayOfWeek.Monday, DayOfWeek.Friday }, c.Days);

        // Enregistrement : la condition survit au JSON
        var s = new Settings();
        s.Rules.Add(new Rule { Pattern = "x", When = new RuleCondition { Power = PowerSource.Battery, From = new TimeOnly(8, 30), To = new TimeOnly(12, 0) } });
        var back = RuleStore.Clone(s).Rules[0].When!;
        Assert.Equal((PowerSource.Battery, new TimeOnly(8, 30), new TimeOnly(12, 0)), (back.Power, back.From, back.To));
    }

    [Fact]
    public void RuleFollowsPowerSource()
    {
        var name = procs.Name("cond");
        var ping = procs.Ping(name);
        var orig = ping.PriorityClass;
        var s = new Settings();
        s.ProBalance.Enabled = false;
        s.Rules.Add(new Rule { Pattern = name, Priority = ProcessPriorityClass.Idle, When = new RuleCondition { Power = PowerSource.Battery } });
        bool battery = false;
        var engine = new Engine(s, new NoPower(), foregroundPid: () => -1) { OnBattery = () => battery };

        var row = engine.TickOnce().Rows.Single(r => r.Pid == ping.Id);
        Assert.Null(row.Rule); // sur secteur : la règle ne s'applique pas
        ping.Refresh();
        Assert.Equal(orig, ping.PriorityClass);

        battery = true; // débranché
        engine.TickOnce();
        ping.Refresh();
        Assert.Equal(ProcessPriorityClass.Idle, ping.PriorityClass);

        battery = false; // rebranché : effet annulé
        engine.TickOnce();
        ping.Refresh();
        Assert.Equal(orig, ping.PriorityClass);
        engine.Stop();
    }

    [Fact]
    public void RuleFollowsTimeRange()
    {
        var name = procs.Name("hour");
        var ping = procs.Ping(name);
        var orig = ping.PriorityClass;
        var s = new Settings();
        s.ProBalance.Enabled = false;
        s.Rules.Add(new Rule { Pattern = name, Priority = ProcessPriorityClass.BelowNormal, When = new RuleCondition { From = new TimeOnly(9, 0), To = new TimeOnly(18, 0) } });
        var clock = Monday(17, 59);
        var engine = new Engine(s, new NoPower(), foregroundPid: () => -1) { LocalTime = () => clock };

        engine.TickOnce();
        ping.Refresh();
        Assert.Equal(ProcessPriorityClass.BelowNormal, ping.PriorityClass);
        clock = Monday(18, 0);
        engine.TickOnce();
        ping.Refresh();
        Assert.Equal(orig, ping.PriorityClass);
        Assert.Contains("09:00-18:00", Engine.Summary(s.Rules[0]));
        engine.Stop();
    }
}
