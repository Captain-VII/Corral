using System.Diagnostics;
using Corral.Core;
using Corral.Models;

namespace Corral.Tests;

public class RuleMatcherTests
{
    [Theory]
    [InlineData("notepad.exe", "notepad", true)]
    [InlineData("NOTEPAD", "notepad", true)]
    [InlineData("notepad.exe", "notepad2", false)]
    [InlineData("chrome*", "chrome", true)]
    [InlineData("chrome*.exe", "chromedriver", true)]
    [InlineData("game?.exe", "game1", true)]
    [InlineData("game?.exe", "game12", false)]
    [InlineData("a.b", "aXb", false)] // le point n'est pas un joker
    [InlineData("", "x", false)]
    public void Matches(string pattern, string name, bool expected) =>
        Assert.Equal(expected, RuleMatcher.Matches(pattern, name));

    [Fact]
    public void FindReturnsFirstEnabledMatch()
    {
        var rules = new List<Rule>
        {
            new() { Pattern = "game.exe", Enabled = false },
            new() { Pattern = "game*", Priority = ProcessPriorityClass.High },
            new() { Pattern = "game.exe", Priority = ProcessPriorityClass.Idle },
        };
        Assert.Same(rules[1], RuleMatcher.Find(rules, "game"));
        Assert.Null(RuleMatcher.Find(rules, "other"));
    }

    [Theory]
    [InlineData(0L, 16, false)]
    [InlineData(-1L, 16, false)]
    [InlineData(0x1L, 16, true)]
    [InlineData(0xFFFFL, 16, true)]
    [InlineData(0x10000L, 16, false)]
    [InlineData(long.MaxValue, 64, true)]
    public void AffinityValidation(long mask, int cpus, bool expected) =>
        Assert.Equal(expected, RuleMatcher.IsValidAffinity(mask, cpus));

    [Fact]
    public void ProtectedProcesses()
    {
        Assert.True(Exclusions.IsProtected("csrss", 600, 1));
        Assert.True(Exclusions.IsProtected("lsass.exe", 700, 1));
        Assert.True(Exclusions.IsProtected("anything", 4, 1));
        Assert.True(Exclusions.IsProtected("me", 1234, 1234));
        Assert.False(Exclusions.IsProtected("notepad", 1234, 1));
    }
}

public class RuleStoreTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "CorralTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void MissingFileGivesDefaults()
    {
        var s = new RuleStore(Path.Combine(dir, "config.json")).Load();
        Assert.Empty(s.Rules);
        Assert.True(s.ProBalance.Enabled);
    }

    [Fact]
    public void RoundTrip()
    {
        var store = new RuleStore(Path.Combine(dir, "config.json"));
        var plan = Guid.NewGuid();
        var s = new Settings();
        s.Rules.Add(new Rule { Pattern = "x.exe", Priority = ProcessPriorityClass.High, AffinityMask = 3, PowerPlan = plan, CpuLimitPercent = 20, MemoryLimitMB = 512 });
        store.Save(s);

        var json = File.ReadAllText(store.FilePath);
        Assert.Contains("\"High\"", json); // énumérations lisibles

        var r = store.Load().Rules.Single();
        Assert.Equal(("x.exe", ProcessPriorityClass.High, 3L, plan, 20, 512),
            (r.Pattern, r.Priority!.Value, r.AffinityMask!.Value, r.PowerPlan!.Value, r.CpuLimitPercent!.Value, r.MemoryLimitMB!.Value));
    }

    [Fact]
    public void CorruptFileIsBackedUpAndDefaultsUsed()
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, "{ pas du json");
        var s = new RuleStore(path).Load();
        Assert.Empty(s.Rules);
        Assert.Single(Directory.GetFiles(dir, "config.json.*.bak"));
    }

    [Fact]
    public void NullsAndOutOfRangeValuesAreNormalized()
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, """{ "PollIntervalMs": 1, "Rules": [null, { "Pattern": null }], "ProBalance": { "SystemThreshold": 500, "Exclusions": null } }""");
        var s = new RuleStore(path).Load();
        Assert.Equal(250, s.PollIntervalMs);
        Assert.Equal("", s.Rules.Single().Pattern);
        Assert.Equal(100, s.ProBalance.SystemThreshold);
        Assert.NotNull(s.ProBalance.Exclusions);
    }
}

public class PowerPlanTests
{
    sealed class FakePower : IPowerPlanApi
    {
        public Guid Active;
        public readonly List<Guid> Calls = new();
        public Guid? GetActive() => Active;
        public bool SetActive(Guid plan)
        {
            Calls.Add(plan);
            Active = plan;
            return true;
        }
    }

    static readonly Guid Balanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    static readonly Guid High = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    static readonly Guid Saver = Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");

    [Fact]
    public void ParseFrenchList()
    {
        const string text = """
            Modes de gestion de l’alimentation existants (* Actif)
            -----------------------------------
            GUID du mode de gestion de l’alimentation : 381b4222-f694-41f0-9685-ff5bb260df2e  (Utilisation normale)
            GUID du mode de gestion de l’alimentation : 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Haute performance) *
            GUID du mode de gestion de l’alimentation : 381b4222-f694-41f0-9685-ff5bb260df2e  (Utilisation normale)
            """;
        var plans = PowerCfg.ParseList(text);
        Assert.Equal(2, plans.Count);
        Assert.Equal(new PowerPlanInfo(Balanced, "Utilisation normale", false), plans[0]);
        Assert.Equal(new PowerPlanInfo(High, "Haute performance", true), plans[1]);
    }

    [Fact]
    public void ManagerSwitchesAndRestores()
    {
        var api = new FakePower { Active = Balanced };
        var m = new PowerPlanManager(api, null);
        var a = new ProcKey(1, "a", 1);
        var b = new ProcKey(2, "b", 2);

        m.OnStart(a, High);
        Assert.Equal(High, api.Active);
        m.OnStart(b, Saver);          // le dernier démarré l'emporte
        Assert.Equal(Saver, api.Active);
        m.OnExit(b);                  // retour au plan encore demandé
        Assert.Equal(High, api.Active);
        m.OnExit(a);                  // plus personne : plan d'origine
        Assert.Equal(Balanced, api.Active);
        m.OnExit(a);                  // sans effet
        Assert.Equal(new[] { High, Saver, High, Balanced }, api.Calls);
    }

    [Fact]
    public void StateFileAllowsCrashRecovery()
    {
        var file = Path.Combine(Path.GetTempPath(), "corral_plan_" + Guid.NewGuid().ToString("N"));
        var api = new FakePower { Active = Balanced };
        var m = new PowerPlanManager(api, file);
        m.OnStart(new ProcKey(1, "a", 1), High);
        Assert.True(File.Exists(file));

        // « plantage » : on ne restaure pas, puis nouveau démarrage
        PowerPlanManager.RecoverFromCrash(api, file);
        Assert.Equal(Balanced, api.Active);
        Assert.False(File.Exists(file));
    }
}

public class ProBalanceTests
{
    static readonly ProcKey Hog = new(10, "hog", 1);
    static readonly DateTime T0 = new(2026, 1, 1);
    readonly ProBalanceLogic logic = new();
    readonly ProBalanceSettings cfg = new() { SystemThreshold = 75, ProcessThreshold = 5, RestoreThreshold = 2, TriggerSeconds = 3, RestoreSeconds = 5 };

    (List<ProcKey> restrain, List<ProcKey> restore) Step(int second, double sys, double cpu, bool eligible = true)
    {
        var restrain = new List<ProcKey>();
        var restore = new List<ProcKey>();
        logic.Evaluate(T0.AddSeconds(second), sys, new[] { new ProBalanceLogic.Sample(Hog, cpu, eligible) }, cfg, restrain, restore);
        return (restrain, restore);
    }

    [Fact]
    public void RestrainsAfterTriggerThenRestoresWhenCalm()
    {
        for (int t = 0; t < 3; t++)
            Assert.Empty(Step(t, 95, 50).restrain);
        Assert.Equal(new[] { Hog }, Step(3, 95, 50).restrain);
        Assert.True(logic.IsRestrained(Hog));

        Assert.Empty(Step(4, 95, 50).restore);           // toujours gourmand
        for (int t = 5; t < 10; t++)
            Assert.Empty(Step(t, 95, 1).restore);         // calme depuis 5 s : pas encore
        Assert.Equal(new[] { Hog }, Step(10, 95, 1).restore);
        Assert.False(logic.IsRestrained(Hog));
    }

    [Fact]
    public void NothingWhenSystemNotBusy()
    {
        for (int t = 0; t < 10; t++)
            Assert.Empty(Step(t, 50, 50).restrain);
    }

    [Fact]
    public void ShortSpikeIsIgnored()
    {
        Step(0, 95, 50);
        Step(1, 95, 50);
        Step(2, 95, 1); // retombe : compteur remis à zéro
        Assert.Empty(Step(3, 95, 50).restrain);
        Assert.Empty(Step(5, 95, 50).restrain);
        Assert.Single(Step(6, 95, 50).restrain);
    }

    [Fact]
    public void NeverRestrainsIneligibleAndRestoresWhenItBecomesIneligible()
    {
        for (int t = 0; t < 10; t++)
            Assert.Empty(Step(t, 95, 50, eligible: false).restrain);

        logic.Reset();
        for (int t = 0; t <= 3; t++)
            Step(t, 95, 50);
        Assert.True(logic.IsRestrained(Hog));
        Assert.Single(Step(4, 95, 50, eligible: false).restore); // passé au premier plan
    }

    [Fact]
    public void ExitedProcessIsForgotten()
    {
        for (int t = 0; t <= 3; t++)
            Step(t, 95, 50);
        logic.Evaluate(T0.AddSeconds(4), 95, Array.Empty<ProBalanceLogic.Sample>(), cfg, new(), new());
        Assert.False(logic.IsRestrained(Hog));
    }
}
