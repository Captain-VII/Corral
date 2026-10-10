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

    static IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));

    [Fact]
    public void ScreenReadersGetNamesForEveryControl()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CorralA11y_" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            var settings = new Settings();
            var engine = new Engine(RuleStore.Clone(settings), new NoPower(), foregroundPid: () => -1);
            using var form = new MainForm(engine, new RuleStore(Path.Combine(dir, "config.json")), settings);
            form.Show();
            Application.DoEvents();

            var unnamed = All(form)
                .Where(c => c is ToggleSwitch or ComboBox or NumericUpDown or TextBoxBase or ButtonBase && c.Parent is not NumericUpDown)
                .Where(c => string.IsNullOrWhiteSpace(c.AccessibleName) && (string.IsNullOrWhiteSpace(c.Text) || A11y.IsIconOnly(c.Text) || c is ComboBox or NumericUpDown or TextBoxBase))
                .Select(c => $"{c.GetType().Name} « {c.Text} » dans {c.Parent?.GetType().Name}")
                .ToList();
            Assert.True(unnamed.Count == 0, "Sans nom : " + string.Join(" | ", unnamed));

            // Barre de navigation : une liste d'onglets, un par page, que l'on peut ouvrir
            var nav = All(form).OfType<NavBar>().Single().AccessibilityObject;
            Assert.Equal(AccessibleRole.PageTabList, nav.Role);
            var tabs = Enumerable.Range(0, nav.GetChildCount()).Select(nav.GetChild).Where(c => c?.Role == AccessibleRole.PageTab).ToList();
            Assert.Equal(9, tabs.Count);
            Assert.All(tabs, t => Assert.False(string.IsNullOrEmpty(t!.Name)));
            tabs[2]!.DoDefaultAction();
            Assert.True((tabs[2]!.State & AccessibleStates.Selected) != 0);

            form.CloseForReal();
            engine.Stop();
        });
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void BugReportLinkIsPrefilled()
    {
        var url = MainForm.BugReportUrl();
        Assert.StartsWith("https://github.com/Captain-VII/Corral/issues/new?template=bug.yml&version=", url);
        Assert.Contains("&windows=Windows%20", url);
    }

    [Fact]
    public void HighContrastPaletteUsesSystemColors()
    {
        var p = Theme.HighContrastPalette();
        Assert.Equal(SystemColors.WindowText, p.Fore);
        Assert.Equal(SystemColors.Window, p.Back);
        Assert.Equal(SystemColors.Highlight, p.Accent);
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
