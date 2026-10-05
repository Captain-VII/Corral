using System.Diagnostics;
using Corral.Core;
using Corral.Models;

namespace Corral.Tests;

public class ImportExportTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "CorralQol_" + Guid.NewGuid().ToString("N"));

    public ImportExportTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void ExportThenImportKeepsRules()
    {
        var file = Path.Combine(dir, "regles.json");
        var rules = new List<Rule>
        {
            new() { Pattern = "jeu.exe", Priority = ProcessPriorityClass.High, AffinityMask = 3 },
            new() { Pattern = "sauvegarde*", Enabled = false, CpuLimitPercent = 20, MemoryLimitMB = 1024 },
        };
        RuleStore.ExportRules(rules, file);
        var back = RuleStore.ImportRules(file);
        Assert.Equal(2, back.Count);
        Assert.Equal((ProcessPriorityClass?)ProcessPriorityClass.High, back[0].Priority);
        Assert.Equal(3L, back[0].AffinityMask);
        Assert.False(back[1].Enabled);
        Assert.Equal(20, back[1].CpuLimitPercent);
    }

    [Fact]
    public void ImportSanitizesRules()
    {
        var file = Path.Combine(dir, "regles.json");
        File.WriteAllText(file, """
            { "Rules": [
                { "Pattern": "  ok.exe  ", "AffinityMask": 0, "CpuLimitPercent": 150 },
                { "Pattern": "" },
                null
            ] }
            """);
        var rule = Assert.Single(RuleStore.ImportRules(file));
        Assert.Equal("ok.exe", rule.Pattern);
        Assert.Null(rule.AffinityMask);
        Assert.Null(rule.CpuLimitPercent);
    }

    [Fact]
    public void ImportRejectsOtherFiles()
    {
        var file = Path.Combine(dir, "autre.json");
        File.WriteAllText(file, "pas du json");
        Assert.Throws<InvalidDataException>(() => RuleStore.ImportRules(file));
        File.WriteAllText(file, "{}");
        Assert.Throws<InvalidDataException>(() => RuleStore.ImportRules(file));
    }
}

public class StartMenuTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "CorralLnk_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void ShortcutIsCreatedFollowedAndRemoved()
    {
        var lnk = Path.Combine(dir, "Corral.lnk");
        var exe1 = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        var exe2 = Path.Combine(Environment.SystemDirectory, "calc.exe");

        StartMenu.Sync(true, exe1, lnk);
        Assert.True(File.Exists(lnk));
        Assert.Equal(exe1, StartMenu.ReadTarget(lnk), ignoreCase: true);

        StartMenu.Sync(true, exe2, lnk); // exe « déplacé » : le raccourci suit
        Assert.Equal(exe2, StartMenu.ReadTarget(lnk), ignoreCase: true);

        StartMenu.Sync(false, exe2, lnk);
        Assert.False(File.Exists(lnk));
    }
}

public class PresetTests
{
    [Fact]
    public void DefaultSettingsMatchBalancedPreset()
    {
        Assert.Same(ProBalanceSettings.Default, new ProBalanceSettings().CurrentPreset());
        Assert.Equal("Équilibré", ProBalanceSettings.Default.Name);
    }

    [Fact]
    public void ApplyPresetSetsAllThresholds()
    {
        var pb = new ProBalanceSettings();
        var soft = ProBalanceSettings.Presets[0];
        pb.ApplyPreset(soft);
        Assert.Equal((85.0, 10.0, 4.0, 5, 8), (pb.SystemThreshold, pb.ProcessThreshold, pb.RestoreThreshold, pb.TriggerSeconds, pb.RestoreSeconds));
        Assert.Same(soft, pb.CurrentPreset());
        pb.SystemThreshold = 70;
        Assert.Null(pb.CurrentPreset()); // « Personnalisé »
    }
}

public class SettingsQolTests
{
    [Fact]
    public void InvalidWindowBoundsAreReset()
    {
        var s = new Settings { Window = new WindowSettings { Width = -5, Height = 600, LastPage = "Règles" } };
        s.Normalize();
        Assert.False(s.Window.HasBounds);
        Assert.Equal("Règles", s.Window.LastPage);
    }

    [Fact]
    public void LogKeepsCategoryAndLevel()
    {
        var marker = "test-" + Guid.NewGuid().ToString("N");
        Log.Info(marker, LogCategory.ProBalance);
        Log.Warn(marker + "-w", LogCategory.Rule);
        var entries = Log.Recent();
        var info = entries.Single(e => e.Message == marker);
        Assert.Equal((LogLevel.Info, LogCategory.ProBalance), (info.Level, info.Category));
        var warn = entries.Single(e => e.Message == marker + "-w");
        Assert.Equal((LogLevel.Warning, LogCategory.Rule), (warn.Level, warn.Category));
    }

    [Fact]
    public void PreviewListsMatchingProcesses()
    {
        var running = new[] { "chrome", "chrome", "code", "chromedriver" };
        Assert.Equal(3, RuleMatcher.Preview("chrome*", running).Count);
        Assert.Single(RuleMatcher.Preview("code.exe", running));
        Assert.Empty(RuleMatcher.Preview("absent", running));
    }

    [Fact]
    public void RuleSummaryIsReadable()
    {
        Assert.Null(Engine.Summary(null));
        Assert.Equal("priorité haute · 2 cœurs · CPU max 25 %",
            Engine.Summary(new Rule { Priority = ProcessPriorityClass.High, AffinityMask = 0b101, CpuLimitPercent = 25 }));
    }
}

public class EngineQolTests
{
    sealed class NoPower : IPowerPlanApi
    {
        public Guid? GetActive() => null;
        public bool SetActive(Guid plan) => true;
    }

    [Fact]
    public void PathAndPriorityOnce()
    {
        using var ping = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            var settings = new Settings();
            settings.ProBalance.Enabled = false;
            var engine = new Engine(settings, new NoPower(), foregroundPid: () => -1);
            var snap = engine.TickOnce();
            var row = Assert.Single(snap.Rows, r => r.Pid == ping.Id);
            Assert.EndsWith("ping.exe", row.Path, StringComparison.OrdinalIgnoreCase);

            Assert.Null(engine.SetPriorityOnce(ping.Id, row.Name, ProcessPriorityClass.BelowNormal));
            ping.Refresh();
            Assert.Equal(ProcessPriorityClass.BelowNormal, ping.PriorityClass);

            Assert.NotNull(engine.SetPriorityOnce(ping.Id, "autre-nom", ProcessPriorityClass.High)); // mauvais processus : refusé
            Assert.NotNull(engine.SetPriorityOnce(4, "System", ProcessPriorityClass.High));          // processus système : refusé
            engine.Stop();
        }
        finally
        {
            ping.Kill();
        }
    }
}
