using System.Diagnostics;
using System.Numerics;
using Corral.Core;
using Corral.Models;

namespace Corral.Tests;

public class CpuTopologyTests
{
    [Fact]
    public void TopologyMatchesThisMachine()
    {
        var topo = CpuTopology.Current;
        Assert.NotNull(topo);
        Assert.NotEmpty(topo!.Cores);
        // Les cœurs couvrent tous les processeurs logiques, sans recouvrement
        Assert.Equal(Math.Min(Environment.ProcessorCount, 64), BitOperations.PopCount((ulong)topo.AllMask));
        Assert.Equal(topo.Cores.Sum(c => BitOperations.PopCount((ulong)c.Mask)), BitOperations.PopCount((ulong)topo.AllMask));
        Assert.NotEmpty(topo.L3Caches);
        foreach (var preset in topo.Presets())
        {
            Assert.True(RuleMatcher.IsValidAffinity(preset.Mask, Environment.ProcessorCount), preset.Name);
            Assert.Equal(preset.Mask, preset.Mask & topo.AllMask);
        }
    }

    [Fact]
    public void PresetsForHybridAndMultiCcd()
    {
        // Hybride : 2 cœurs P avec SMT (classe 1) + 2 cœurs E (classe 0) ; deux L3 de tailles différentes
        var topo = new CpuTopology(
            new[] { new CpuTopology.Core(0b0011, 1), new CpuTopology.Core(0b1100, 1), new CpuTopology.Core(0b010000, 0), new CpuTopology.Core(0b100000, 0) },
            new[] { new CpuTopology.L3Cache(0b001111, 96L << 20), new CpuTopology.L3Cache(0b110000, 32L << 20) });
        var presets = topo.Presets().ToDictionary(p => p.Name, p => p.Mask);
        Assert.Equal(0b1111, presets["Cœurs performants"]);
        Assert.Equal(0b110000, presets["Cœurs efficaces"]);
        Assert.Equal(0b001111, presets["CCD avec V-Cache"]);
        Assert.Equal(0b110101, presets["Sans SMT"]); // 1er thread de chaque cœur
    }

    [Fact]
    public void NoPointlessPresetsOnSimpleCpu()
    {
        // Un seul CCD, pas d'hybride, pas de SMT : rien à proposer
        var topo = new CpuTopology(new[] { new CpuTopology.Core(1, 0), new CpuTopology.Core(2, 0) }, new[] { new CpuTopology.L3Cache(3, 32L << 20) });
        Assert.Empty(topo.Presets());
    }
}

public class PerformanceEngineTests : IDisposable
{
    sealed class FakePower : IPowerPlanApi
    {
        public Guid Active = Guid.Empty;
        public readonly List<Guid> Calls = new();
        public Guid? GetActive() => Active;
        public bool SetActive(Guid plan) { Active = plan; Calls.Add(plan); return true; }
    }

    readonly string dir = Path.Combine(Path.GetTempPath(), "CorralPerf_" + Guid.NewGuid().ToString("N"));
    readonly List<Process> started = new();

    public void Dispose()
    {
        foreach (var p in started)
            try { p.Kill(); p.WaitForExit(2000); } catch { }
        try { Directory.Delete(dir, true); } catch { }
    }

    /// <summary>Lance une copie de ping.exe sous un nom unique, pour ne viser que nos processus de test.</summary>
    /// <param name="folder">Sous-dossier où copier l'exe (par exemple une fausse bibliothèque Steam).</param>
    Process Start(string name, string folder = "")
    {
        var target = Path.Combine(dir, folder);
        Directory.CreateDirectory(target);
        var exe = Path.Combine(target, name + ".exe");
        if (!File.Exists(exe))
            File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), exe);
        var p = Process.Start(new ProcessStartInfo(exe, "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        started.Add(p);
        return p;
    }

    static Settings Base()
    {
        var s = new Settings();
        s.ProBalance.Enabled = false;
        return s;
    }

    [Fact]
    public void EfficiencyIoAndMemoryAreAppliedThenRestored()
    {
        var p = Start("corral_tweak_test");
        var origIo = ProcessTweaks.GetIoPriority(p.Handle);
        var origMem = ProcessTweaks.GetMemoryPriority(p.Handle);
        var s = Base();
        s.Rules.Add(new Rule { Pattern = "corral_tweak_test", EfficiencyMode = true, IoPriority = IoPriorityLevel.VeryLow, MemoryPriority = MemoryPriorityLevel.Low });
        var engine = new Engine(s, new FakePower(), foregroundPid: () => -1);

        engine.TickOnce();
        Assert.Equal(IoPriorityLevel.VeryLow, ProcessTweaks.GetIoPriority(p.Handle));
        Assert.Equal(MemoryPriorityLevel.Low, ProcessTweaks.GetMemoryPriority(p.Handle));
        Assert.DoesNotContain(Log.Recent(), e => e.Message.Contains("corral_tweak_test") && e.Message.Contains("échec"));

        engine.Stop();
        Assert.Equal(origIo, ProcessTweaks.GetIoPriority(p.Handle));
        Assert.Equal(origMem, ProcessTweaks.GetMemoryPriority(p.Handle));
    }

    [Fact]
    public void GameModeFollowsTheGameAndLowersBackgroundApps()
    {
        var plan = Guid.NewGuid();
        var s = Base();
        s.Rules.Add(new Rule { Pattern = "corral_game_test", IsGame = true });
        s.GameMode.PowerPlan = plan;
        s.GameMode.BackgroundApps = new() { "corral_bg_test.exe" };
        var power = new FakePower();
        var engine = new Engine(s, power, foregroundPid: () => -1);

        var bg = Start("corral_bg_test");
        var origPriority = bg.PriorityClass;
        engine.TickOnce();
        Assert.False(engine.TickOnce().GameMode);

        var game = Start("corral_game_test");
        var snap = engine.TickOnce(); // détecte le jeu
        Assert.True(snap.GameMode);
        Assert.Equal("corral_game_test", snap.GameTrigger);
        engine.TickOnce();            // réapplication : programme de fond abaissé, plan activé
        bg.Refresh();
        Assert.Equal(ProcessPriorityClass.BelowNormal, bg.PriorityClass);
        Assert.Equal(plan, power.Active);
        Assert.Contains(engine.TickOnce().Rows, r => r.Pid == bg.Id && r.Rule == Engine.GameModeRuleName);

        game.Kill();
        game.WaitForExit();
        Assert.False(engine.TickOnce().GameMode); // jeu fermé
        engine.TickOnce();                        // réapplication : tout est rendu
        bg.Refresh();
        Assert.Equal(origPriority, bg.PriorityClass);
        Assert.Equal(Guid.Empty, power.Active);
        engine.Stop();
    }

    [Fact]
    public void GameStartSwitchesPowerPlanWithoutGoingBack()
    {
        Guid rulePlan = Guid.NewGuid(), gamePlan = Guid.NewGuid();
        var s = Base();
        s.Rules.Add(new Rule { Pattern = "corral_plan_game", IsGame = true, PowerPlan = rulePlan });
        s.GameMode.PowerPlan = gamePlan;
        var power = new FakePower();
        var engine = new Engine(s, power, foregroundPid: () => -1);

        var game = Start("corral_plan_game");
        Assert.True(engine.TickOnce().GameMode);
        engine.TickOnce(); // réapplication : le plan d'origine n'est pas remis entre-temps
        Assert.Equal(new[] { rulePlan, gamePlan }, power.Calls);

        game.Kill();
        game.WaitForExit();
        engine.TickOnce();
        engine.TickOnce();
        Assert.Equal(Guid.Empty, power.Active); // jeu fermé : plan d'origine rendu
        engine.Stop();
    }

    [Fact]
    public void InstalledGameTurnsGameModeOnOnceInTheForeground()
    {
        var s = Base();
        s.GameMode.BackgroundApps = new();
        var time = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        int fg = -1;
        var game = Start("corral_autogame", @"steamapps\common\Corral Test Game");
        var engine = new Engine(s, new FakePower(), foregroundPid: () => fg, clock: () => time) { Games = new GameDetector { Hive = () => null, LauncherFolders = Array.Empty<string> } };

        Assert.False(engine.TickOnce().GameMode); // jamais au premier plan : peut-être un logiciel de fond

        fg = game.Id;
        var snap = engine.TickOnce();
        Assert.True(snap.GameMode);
        Assert.Equal("corral_autogame", snap.GameTrigger);

        fg = -1; // le joueur passe sur une autre fenêtre
        time += TimeSpan.FromMinutes(9);
        Assert.True(engine.TickOnce().GameMode); // réapplication comprise : le dernier passage au premier plan est gardé
        time += TimeSpan.FromMinutes(2);
        Assert.False(engine.TickOnce().GameMode);
        engine.Stop();
    }

    [Theory]
    [InlineData(true)]  // le programme a sa propre règle, sans « C'est un jeu »
    [InlineData(false)] // détection désactivée
    public void GameDetectionGivesWayToRulesAndSetting(bool ownRule)
    {
        var s = Base();
        s.GameMode.BackgroundApps = new();
        var name = ownRule ? "corral_ruled_tool" : "corral_nodetect";
        if (ownRule)
            s.Rules.Add(new Rule { Pattern = name + ".exe" });
        else
            s.GameMode.DetectGames = false;
        var p = Start(name, @"steamapps\common\Corral Tool");
        var engine = new Engine(s, new FakePower(), foregroundPid: () => p.Id) { Games = new GameDetector { Hive = () => null, LauncherFolders = Array.Empty<string> } };
        Assert.False(engine.TickOnce().GameMode);
        Assert.False(engine.TickOnce().GameMode);
        engine.Stop();
    }

    [Fact]
    public void ProtectedProcessesDoNotThrowAtEveryPass()
    {
        // Idle, System, processus PPL : heure de démarrage inaccessible. Le refus est retenu au premier passage
        // (deux passages qui se suivent : aucun nouveau processus protégé entre les deux, en pratique).
        var engine = new Engine(Base(), new FakePower(), foregroundPid: () => -1);
        int thread = Environment.CurrentManagedThreadId, count = 0;
        void OnFirstChance(object? _, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (Environment.CurrentManagedThreadId == thread && e.Exception is System.ComponentModel.Win32Exception
                && Environment.StackTrace.Contains("get_StartTime"))
                count++;
        }
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            engine.TickOnce();
            int first = count;
            count = 0;
            engine.TickOnce();
            Assert.True(first > 0, "aucun processus protégé trouvé");
            Assert.True(count == 0, $"{count} exceptions au 2e passage, {first} au 1er");
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
            engine.Stop();
        }
    }

    [Fact]
    public void ManualGameMode()
    {
        var s = Base();
        var engine = new Engine(s, new FakePower(), foregroundPid: () => -1);
        engine.SetGameMode(true);
        Assert.True(engine.TickOnce().GameMode);
        engine.SetGameMode(false);
        Assert.False(engine.TickOnce().GameMode);
        engine.Stop();
    }
}
