using Corral.Core;
using Corral.Models;
using Microsoft.Win32;

namespace Corral.Tests;

public class MetricsTests : IDisposable
{
    sealed class FakePower : IPowerPlanApi
    {
        public Guid? GetActive() => null;
        public bool SetActive(Guid plan) => true;
    }

    readonly TestProcesses procs = new();
    readonly string root = @"Software\CorralTests\Startup_" + Guid.NewGuid().ToString("N");
    readonly string folder = Path.Combine(Path.GetTempPath(), "CorralStartup_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        procs.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false); } catch { }
        try { Directory.Delete(folder, true); } catch { }
    }

    [Fact]
    public void IoCountersGrowWhenWriting()
    {
        ulong before = ProcessIo.Read(Environment.ProcessId)!.Value;
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, new byte[4 << 20]);
        }
        finally
        {
            File.Delete(file);
        }
        ulong after = ProcessIo.Read(Environment.ProcessId)!.Value;
        Assert.True(after - before >= 4 << 20, $"{after - before}");
        Assert.Null(ProcessIo.Read(-5));
    }

    [Fact]
    public void GpuInstancesAreGroupedPerProcess()
    {
        var usage = GpuSampler.Aggregate(new[]
        {
            ("pid_100_luid_0x0_0xD1E5_phys_0_eng_0_engtype_3D", 20.0),
            ("pid_100_luid_0x0_0xD1E5_phys_0_eng_1_engtype_3D", 15.0),
            ("pid_100_luid_0x0_0xD1E5_phys_0_eng_5_engtype_VideoDecode", 30.0),
            ("pid_200_luid_0x0_0xD1E5_phys_0_eng_0_engtype_Copy", 5.0),
            ("pid_0_luid_0x0_0xD1E5_phys_0_eng_0_engtype_3D", 50.0),
            ("_Total", 99.0),
        });
        Assert.Equal(35.0, usage[100]); // 3D (20 + 15) l'emporte sur VideoDecode (30)
        Assert.Equal(5.0, usage[200]);
        Assert.Equal(2, usage.Count);
    }

    [Fact]
    public void GpuSamplerNeverThrows()
    {
        using var sampler = new GpuSampler();
        sampler.Sample();
        Thread.Sleep(200);
        Assert.All(sampler.Sample().Values, v => Assert.InRange(v, 0, 100));
    }

    [Fact]
    public void EngineReportsIoAndGpu()
    {
        var p = procs.Ping(procs.Name("io"));
        var s = new Settings();
        s.ProBalance.Enabled = false;
        var engine = new Engine(s, new FakePower(), foregroundPid: () => -1)
        {
            GpuUsage = () => new Dictionary<int, double> { [p.Id] = 42 },
        };
        engine.TickOnce();
        var snap = engine.TickOnce();
        var row = snap.Rows.Single(r => r.Pid == p.Id);
        Assert.Equal(42, row.Gpu);
        Assert.True(row.IoBytesPerSec >= 0);
        engine.Stop();
    }

    [Fact]
    public void ProcessHistoryKeepsRecentSamplesOfLiveProcesses()
    {
        var h = new ProcessHistory(TimeSpan.FromSeconds(10));
        var t = DateTime.UtcNow;
        for (int i = 0; i < 20; i++)
            h.Add(t.AddSeconds(i), new[]
            {
                new ProcessRow(1, "a", i, 100, null, false, IoBytesPerSec: 5, Gpu: 2),
                new ProcessRow(2, "b", 1, 100, null, false),
            });
        var a = h.Get(1, "a");
        Assert.Equal(11, a.Count);
        Assert.Equal(19f, a[^1].Cpu);
        Assert.Equal(5, a[^1].Io);
        Assert.Empty(h.Get(1, "autre")); // PID réutilisé par un autre programme

        h.Add(t.AddSeconds(20), new[] { new ProcessRow(1, "a", 0, 100, null, false) });
        Assert.Empty(h.Get(2, "b")); // fermé : historique libéré
    }

    StartupManager Manager()
    {
        var hive = new StartupManager.Hive(Registry.CurrentUser, root + @"\User\");
        var machine = new StartupManager.Hive(Registry.CurrentUser, root + @"\Machine\");
        return new StartupManager(hive, machine, null, folder, null);
    }

    [Fact]
    public void StartupItemsCanBeDisabledAndEnabled()
    {
        var exe = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        using (var run = Registry.CurrentUser.CreateSubKey(root + @"\User\Microsoft\Windows\CurrentVersion\Run"))
        {
            run.SetValue("Bloc-notes", $"\"{exe}\" /x");
            run.SetValue("Vide", "");
        }
        using (var run = Registry.CurrentUser.CreateSubKey(root + @"\Machine\Microsoft\Windows\CurrentVersion\Run"))
            run.SetValue("Outil", @"%SystemRoot%\System32\cmd.exe /c echo");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Raccourci.lnk"), "");
        File.WriteAllText(Path.Combine(folder, "desktop.ini"), "");

        var m = Manager();
        var items = m.List();
        Assert.Equal(new[] { "Bloc-notes", "Outil", "Raccourci" }, items.Select(i => i.Name));
        Assert.All(items, i => Assert.True(i.Enabled));
        var notepad = items[0];
        Assert.Equal(exe, notepad.Path, ignoreCase: true);
        Assert.Equal(StartupSource.UserRun, notepad.Source);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), items[1].Path, ignoreCase: true);

        m.SetEnabled(notepad, false);
        m.SetEnabled(items[2], false);
        using (var approved = Registry.CurrentUser.OpenSubKey(root + @"\User\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"))
            Assert.Equal(3, ((byte[])approved!.GetValue("Bloc-notes")!)[0]);
        items = m.List();
        Assert.False(items[0].Enabled);
        Assert.True(items[1].Enabled);
        Assert.False(items[2].Enabled);

        m.SetEnabled(items[0], true);
        Assert.True(m.List()[0].Enabled);
    }

    [Theory]
    [InlineData("\"C:\\Windows\\System32\\cmd.exe\" /c", true)]
    [InlineData("C:\\Windows\\System32\\cmd.exe /c x", true)]
    [InlineData("C:\\Introuvable\\x.exe", false)]
    public void ExecutableIsExtractedFromCommand(string command, bool found)
    {
        Assert.Equal(found, StartupManager.ExecutableOf(command) != null);
    }
}
