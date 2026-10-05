using System.Diagnostics;
using Corral.Core;
using Corral.Models;
using Corral.UI;

namespace Corral.Tests;

public class ComfortTests
{
    static Settings WithRules(params string[] patterns)
    {
        var s = new Settings();
        foreach (var p in patterns)
            s.Rules.Add(new Rule { Pattern = p, Priority = ProcessPriorityClass.High });
        return s;
    }

    [Fact]
    public void ProfilesSwapRuleSets()
    {
        var s = WithRules("jeu.exe", "obs.exe");
        Profiles.Ensure(s, "Principal");
        Assert.Equal("Principal", s.ActiveProfile);
        Assert.Single(s.Profiles);

        var travail = Profiles.Create(s, " Travail ", copyCurrent: false)!;
        Assert.Equal("Travail", travail.Name);
        Assert.Null(Profiles.Create(s, "travail", copyCurrent: false)); // nom déjà pris (casse ignorée)
        Assert.Null(Profiles.Create(s, "  ", copyCurrent: false));

        Assert.True(Profiles.Switch(s, "Travail"));
        Assert.Equal("Travail", s.ActiveProfile);
        Assert.Empty(s.Rules);
        s.Rules.Add(new Rule { Pattern = "teams.exe" });

        Assert.True(Profiles.Switch(s, "principal"));
        Assert.Equal(new[] { "jeu.exe", "obs.exe" }, s.Rules.Select(r => r.Pattern));
        Assert.True(Profiles.Switch(s, "Travail"));
        Assert.Equal("teams.exe", Assert.Single(s.Rules).Pattern); // rien de perdu en route
        Assert.False(Profiles.Switch(s, "Travail")); // déjà actif
        Assert.False(Profiles.Switch(s, "Inconnu"));
    }

    [Fact]
    public void ProfileCopyIsIndependent()
    {
        var s = WithRules("jeu.exe");
        Profiles.Ensure(s, "Principal");
        var copy = Profiles.Create(s, "Copie", copyCurrent: true)!;
        Assert.Equal("jeu.exe", Assert.Single(copy.Rules).Pattern);
        copy.Rules[0].Pattern = "autre.exe";
        Assert.Equal("jeu.exe", s.Rules[0].Pattern);
    }

    [Fact]
    public void ActiveProfileCanBeRenamedButNotDeleted()
    {
        var s = WithRules("a.exe");
        Profiles.Ensure(s, "Principal");
        Profiles.Create(s, "Silencieux", copyCurrent: false);
        Assert.False(Profiles.Delete(s, "Principal"));
        Assert.False(Profiles.Rename(s, "Principal", "silencieux")); // conflit
        Assert.True(Profiles.Rename(s, "Principal", "Jeu"));
        Assert.Equal("Jeu", s.ActiveProfile);
        Assert.True(Profiles.Delete(s, "Silencieux"));
        Assert.Single(s.Profiles);
    }

    [Fact]
    public void ProfilesSurviveSaveAndNormalize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "CorralProfiles_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RuleStore(Path.Combine(dir, "config.json"));
            var s = WithRules("a.exe");
            Profiles.Ensure(s, "Principal");
            Profiles.Create(s, "Jeu", copyCurrent: true);
            s.Profiles.Add(new Profile { Name = "jeu" });   // doublon
            s.Profiles.Add(new Profile { Name = " " });     // vide
            s.Language = "de";                              // inconnue
            store.Save(s);
            var loaded = store.Load();
            Assert.Equal(new[] { "Principal", "Jeu" }, loaded.Profiles.Select(p => p.Name));
            Assert.Equal("a.exe", loaded.Profiles[1].Rules.Single().Pattern);
            Assert.Equal("auto", loaded.Language);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void CpuIconIsDrawnAndFreed()
    {
        foreach (var cpu in new[] { 0.0, 7, 64, 100 })
        {
            var icon = AppIcon.CpuIcon(cpu, new Size(16, 16));
            Assert.Equal(16, icon.Width);
            AppIcon.Free(icon);
        }
        Assert.NotEqual(AppIcon.LoadColor(10), AppIcon.LoadColor(90));
    }

    [Fact]
    public void OverlayShowsWithoutStealingFocus()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var cfg = new OverlaySettings { X = -50_000, Y = -50_000 }; // hors écran : replacée
                using var overlay = new OverlayWindow(cfg);
                overlay.Show();
                overlay.SetSnapshot(new EngineSnapshot(42, false, Array.Empty<ProcessRow>(), GameMode: true, MemoryUsed: 4, MemoryTotal: 8));
                Application.DoEvents();
                Assert.Contains(Screen.AllScreens, s => s.WorkingArea.Contains(overlay.Bounds));
                Assert.True(overlay.TopMost);
                overlay.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }
}
