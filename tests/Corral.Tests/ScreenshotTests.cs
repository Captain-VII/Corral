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
                settings.Rules.Add(new Rule { Pattern = "jeu.exe", Priority = System.Diagnostics.ProcessPriorityClass.High });
                settings.Rules.Add(new Rule { Pattern = "sauvegarde*", Enabled = false, Priority = System.Diagnostics.ProcessPriorityClass.Idle, CpuLimitPercent = 20, MemoryLimitMB = 2048 });
                using var engine = new Engine(RuleStore.Clone(settings), new NoPower(), foregroundPid: () => -1);
                settings.ProBalance.Enabled = true; // seulement côté interface : affiche le seuil sans que le moteur agisse
                using var form = new MainForm(engine, new RuleStore(Path.Combine(configDir, "config.json")), settings) { TopMost = true, StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
                form.Show();
                engine.Start();
                Pump(2500);
                Capture(form, Path.Combine(outDir, $"shot-{mode}.png"));

                // Onglet Graphique avec 5 min de données de démonstration (dont un pic au-dessus du seuil)
                var start = DateTime.UtcNow.AddMinutes(-5);
                var rnd = new Random(42);
                for (int s = 0; s < 297; s++)
                {
                    double v = 18 + 8 * Math.Sin(s / 20.0) + rnd.NextDouble() * 6;
                    if (s is > 150 and < 190) v = 80 + rnd.NextDouble() * 15;
                    form.History.Add(start.AddSeconds(s), v);
                }
                form.ShowPage("Graphique");
                Pump(1200);
                Capture(form, Path.Combine(outDir, $"shot-{mode}-chart.png"));

                // Survol simulé au milieu du pic
                var chart = Find<CpuChart>(form)!;
                typeof(Control).GetMethod("OnMouseMove", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(chart, new object[] { new MouseEventArgs(MouseButtons.None, 0, chart.Width * 6 / 10, chart.Height / 2, 0) });
                Pump(300);
                Capture(form, Path.Combine(outDir, $"shot-{mode}-chart-hover.png"));

                Log.Info("chrome (1234) : règle « chrome* » → priorité BelowNormal", LogCategory.Rule);
                Log.Info("ProBalance : jeu (4321) abaissé (CPU système 91%)", LogCategory.ProBalance);
                Log.Warn("powercfg : délai dépassé", LogCategory.Power);
                foreach (var page in new[] { "Règles", "ProBalance", "Options", "Journal" })
                {
                    form.ShowPage(page);
                    Pump(400);
                    Capture(form, Path.Combine(outDir, $"shot-{mode}-{page}.png"));
                }

                using var dlg = new RuleDialog(settings.Rules[0], PowerCfg.List(), isNew: false, new[] { "chrome", "chrome", "chrome", "code", "System" }) { TopMost = true, StartPosition = FormStartPosition.Manual, Location = new Point(1200, 40) };
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

    static T? Find<T>(Control root) where T : Control =>
        root is T match ? match : root.Controls.Cast<Control>().Select(Find<T>).FirstOrDefault(c => c != null);

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
