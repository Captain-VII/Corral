using Corral.Core;
using Corral.Models;
using Corral.UI;

namespace Corral.Tests;

/// <summary>
/// Captures d'écran de l'interface (thèmes clair et sombre) dans tools/.
/// Désactivé par défaut : CORRAL_SCREENSHOTS=1 dotnet test --filter Screenshot
/// </summary>
public class ScreenshotTests
{
    [Fact]
    public void Screenshot()
    {
        if (Environment.GetEnvironmentVariable("CORRAL_SCREENSHOTS") != "1")
            return;
        var outDir = Environment.GetEnvironmentVariable("CORRAL_SCREENSHOTS_DIR") ?? Path.GetTempPath();
        var configDir = Path.Combine(Path.GetTempPath(), "CorralShots_" + Guid.NewGuid().ToString("N"));
        var thread = new Thread(() =>
        {
            foreach (var mode in new[] { ThemeMode.Dark, ThemeMode.Light })
            {
                Theme.Set(mode);
                var settings = new Settings { Theme = mode };
                settings.ProBalance.Enabled = false;
                settings.Rules.Add(new Rule { Pattern = "chrome*", Priority = System.Diagnostics.ProcessPriorityClass.BelowNormal, AffinityMask = 0xFF });
                using var engine = new Engine(RuleStore.Clone(settings), new NoPower(), foregroundPid: () => -1);
                using var form = new MainForm(engine, new RuleStore(Path.Combine(configDir, "config.json")), settings) { TopMost = true, StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
                form.Show();
                engine.Start();
                Pump(2500);
                Capture(form, Path.Combine(outDir, $"shot-{mode}.png"));

                using var dlg = new RuleDialog(settings.Rules[0], PowerCfg.List(), isNew: false) { TopMost = true, StartPosition = FormStartPosition.Manual, Location = new Point(1040, 40) };
                dlg.Show();
                Pump(500);
                Capture(dlg, Path.Combine(outDir, $"shot-{mode}-dialog.png"));
                dlg.Close();
                engine.Stop();
                form.CloseForReal();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    static void Pump(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
    }

    static void Capture(Form f, string path)
    {
        using var bmp = new Bitmap(f.Width, f.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(f.Location, Point.Empty, f.Size);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    sealed class NoPower : IPowerPlanApi
    {
        public Guid? GetActive() => null;
        public bool SetActive(Guid plan) => true;
    }
}
