using System.ComponentModel;
using System.Diagnostics;
using Corral.Core;
using Corral.Models;
using Microsoft.Win32;

namespace Corral.Tests;

public class OptimizationTests : IDisposable
{
    sealed class FakePower : IPowerPlanApi
    {
        public Guid Active = Guid.Empty;
        public Guid? GetActive() => Active;
        public bool SetActive(Guid plan) { Active = plan; return true; }
    }

    readonly TestProcesses procs = new();
    readonly string gpuKey = @"Software\CorralTests\Gpu_" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        procs.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(gpuKey, throwOnMissingSubKey: false); } catch { }
    }

    static Settings Base()
    {
        var s = new Settings();
        s.ProBalance.Enabled = false;
        return s;
    }

    [Fact]
    public void ForegroundBoostFollowsTheActiveWindow()
    {
        var p = procs.Ping(procs.Name("fg"));
        int fg = p.Id;
        var s = Base();
        s.ForegroundBoost.Enabled = true;
        var engine = new Engine(s, new FakePower(), foregroundPid: () => fg);

        engine.TickOnce();
        p.Refresh();
        Assert.Equal(ProcessPriorityClass.AboveNormal, p.PriorityClass);

        fg = -1; // l'utilisateur passe à une autre fenêtre
        engine.TickOnce();
        p.Refresh();
        Assert.Equal(ProcessPriorityClass.Normal, p.PriorityClass);

        fg = p.Id;
        engine.TickOnce();
        engine.Stop(); // l'arrêt rend la priorité d'origine
        p.Refresh();
        Assert.Equal(ProcessPriorityClass.Normal, p.PriorityClass);
    }

    [Fact]
    public void ForegroundBoostLeavesRulePriorityAlone()
    {
        var name = procs.Name("fgrule");
        var p = procs.Ping(name);
        var s = Base();
        s.ForegroundBoost.Enabled = true;
        s.Rules.Add(new Rule { Pattern = name, Priority = ProcessPriorityClass.BelowNormal });
        var engine = new Engine(s, new FakePower(), foregroundPid: () => p.Id);
        engine.TickOnce();
        p.Refresh();
        Assert.Equal(ProcessPriorityClass.BelowNormal, p.PriorityClass);
        engine.Stop();
    }

    [Fact]
    public void IdleSaverSwitchesPlanOnlyWhenAppropriate()
    {
        var saver = Guid.NewGuid();
        var s = Base();
        s.IdleSaver.Enabled = true;
        s.IdleSaver.Minutes = 10;
        s.IdleSaver.Plan = saver;
        var power = new FakePower();
        var idle = TimeSpan.Zero;
        bool video = false;
        var engine = new Engine(s, power, foregroundPid: () => -1) { IdleTimeSource = () => idle, DisplayRequiredSource = () => video };

        engine.TickOnce();
        Assert.Equal(Guid.Empty, power.Active);

        idle = TimeSpan.FromMinutes(11);
        engine.TickOnce();
        Assert.Equal(saver, power.Active);
        Assert.True(engine.IdleSaverActive);

        idle = TimeSpan.FromSeconds(1); // retour de l'utilisateur
        engine.TickOnce();
        Assert.Equal(Guid.Empty, power.Active);

        idle = TimeSpan.FromMinutes(30);
        video = true; // une vidéo demande l'écran : pas d'économie
        engine.TickOnce();
        Assert.Equal(Guid.Empty, power.Active);

        video = false;
        engine.SetGameMode(true); // Mode Jeu : pas d'économie
        engine.TickOnce();
        engine.TickOnce();
        Assert.False(engine.IdleSaverActive);
        engine.Stop();
    }

    [Fact]
    public void TrimReducesWorkingSet()
    {
        var p = procs.Cmd(procs.Name("trim"));
        Thread.Sleep(300);
        p.Refresh();
        long before = p.WorkingSet64;
        MemoryCleaner.TrimWorkingSet(p);
        p.Refresh();
        Assert.True(p.WorkingSet64 < before, $"{p.WorkingSet64} >= {before}");
    }

    [Fact]
    public void PurgeStandbyNeedsAdminOrSucceeds()
    {
        try
        {
            MemoryCleaner.PurgeStandbyList(); // réussit si les tests tournent en administrateur
        }
        catch (Win32Exception)
        {
            // attendu sans droits administrateur : erreur propre, pas de plantage
        }
    }

    [Fact]
    public void ManualCleanupRunsAndIsLogged()
    {
        var engine = new Engine(Base(), new FakePower(), foregroundPid: () => -1);
        engine.CleanMemoryNow();
        engine.TickOnce();
        Assert.Contains(Log.Recent(), e => e.Message.StartsWith("Nettoyage mémoire :") && e.Time > DateTime.Now.AddSeconds(-10));
        engine.Stop();
    }

    [Fact]
    public void GpuPreferenceIsSetThenRemovedWithTheRule()
    {
        var gpu = new GpuPreferences(null, gpuKey);
        var name = procs.Name("gpu");
        var p = procs.Ping(name);
        var s = Base();
        s.Rules.Add(new Rule { Pattern = name, GpuPreference = GpuPreference.HighPerformance });
        var engine = new Engine(s, new FakePower(), foregroundPid: () => -1) { Gpu = gpu };

        engine.TickOnce();
        var path = ProcessDetails.Read(p.Id).Path!;
        Assert.Equal(GpuPreference.HighPerformance, gpu.Get(path));
        using (var key = Registry.CurrentUser.OpenSubKey(gpuKey))
            Assert.Equal("GpuPreference=2;", key!.GetValue(path));

        engine.UpdateSettings(Base()); // règle supprimée
        engine.TickOnce();
        Assert.Null(gpu.Get(path));
        Assert.Empty(gpu.Managed);
        engine.Stop();
    }
}
