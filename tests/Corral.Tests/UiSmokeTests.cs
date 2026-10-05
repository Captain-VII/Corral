using System.Diagnostics;
using Corral.Core;
using Corral.Models;
using Corral.UI;

namespace Corral.Tests;

public class UiSmokeTests
{
    static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null)
            throw new Exception("Erreur interface", error);
    }

    [Fact]
    public void MainFormAndDialogBuildAndShow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CorralUi_" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            var settings = new Settings();
            settings.Rules.Add(new Rule { Pattern = "x.exe", Priority = ProcessPriorityClass.High, AffinityMask = 3, PowerPlan = Guid.NewGuid(), CpuLimitPercent = 10 });
            var engine = new Engine(RuleStore.Clone(settings), new NoPower(), foregroundPid: () => -1);
            using var form = new MainForm(engine, new RuleStore(Path.Combine(dir, "config.json")), settings);
            form.Show();
            Application.DoEvents();
            form.CloseForReal();

            using var dlg = new RuleDialog(settings.Rules[0], new[] { new PowerPlanInfo(Guid.NewGuid(), "Test", true) }, isNew: false);
            dlg.Show();
            Application.DoEvents();
            dlg.Close();
            engine.Stop();
        });
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void EmbeddedIconLoads()
    {
        using var icon = AppIcon.Load(new Size(16, 16));
        Assert.NotSame(SystemIcons.Application, icon);
        Assert.Equal(16, icon.Width);
    }

    sealed class NoPower : IPowerPlanApi
    {
        public Guid? GetActive() => null;
        public bool SetActive(Guid plan) => true;
    }
}
