using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using Corral.Core;
using Corral.Models;
using Corral.Service;

namespace Corral.Tests;

public class ServiceTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "CorralSvc_" + Guid.NewGuid().ToString("N"));
    readonly string pipe = "CorralTest_" + Guid.NewGuid().ToString("N");

    public ServiceTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    sealed class NoPower : IPowerPlanApi
    {
        public Guid? GetActive() => null;
        public bool SetActive(Guid plan) => true;
    }

    ServiceHost StartHost(PolicySet? policies = null)
    {
        var host = new ServiceHost(dir, pipe, policies ?? new PolicySet(), powerApi: new NoPower());
        host.Start();
        return host;
    }

    /// <summary>Configuration sans ProBalance ni Mode Jeu automatique : le service ne touche à rien d'autre pendant le test.</summary>
    void WriteQuietConfig()
    {
        var s = new Settings();
        s.ProBalance.Enabled = false;
        s.GameMode.Automatic = false;
        s.GameMode.PowerPlan = Guid.Empty;
        new RuleStore(Path.Combine(dir, "config.json")).Save(s);
    }

    static T Wait<T>(Func<T?> probe, string what) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (probe() is { } value)
                return value;
            Thread.Sleep(50);
        }
        throw new TimeoutException(what);
    }

    [Fact]
    public void InterfaceDrivesEngineThroughPipe()
    {
        WriteQuietConfig();
        using var procs = new TestProcesses();
        var name = procs.Name("svc");
        var ping = procs.Ping(name);
        using var host = StartHost();

        using var remote = RemoteEngine.Connect(TimeSpan.FromSeconds(5), pipe, requireService: false, mirrorLog: false)!;
        Assert.NotNull(remote);
        Assert.False(remote.Fresh);
        Assert.False(remote.InitialSettings.ProBalance.Enabled);

        EngineSnapshot? snap = null;
        remote.SnapshotReady += s => { if (s.Rows.Any(r => r.Pid == ping.Id)) snap = s; };
        remote.Start();
        var row = Wait(() => snap, "instantané").Rows.Single(r => r.Pid == ping.Id);

        // Commande avec réponse
        Assert.Null(remote.SetPriorityOnce(ping.Id, row.Name, ProcessPriorityClass.BelowNormal));
        ping.Refresh();
        Assert.Equal(ProcessPriorityClass.BelowNormal, ping.PriorityClass);
        Assert.NotNull(remote.SetPriorityOnce(4, "System", ProcessPriorityClass.High));

        // Configuration appliquée et enregistrée par le service
        var settings = remote.InitialSettings;
        settings.Rules.Add(new Rule { Pattern = name, Priority = ProcessPriorityClass.AboveNormal });
        remote.UpdateSettings(settings);
        Wait(() => { ping.Refresh(); return ping.PriorityClass == ProcessPriorityClass.AboveNormal ? "" : null; }, "règle appliquée");
        var saved = new RuleStore(Path.Combine(dir, "config.json")).Load();
        Assert.Contains(saved.Rules, r => r.Pattern == name);
    }

    [Fact]
    public void InterfaceReconnectsWhenServiceRestarts()
    {
        WriteQuietConfig();
        var host = StartHost();
        using var remote = RemoteEngine.Connect(TimeSpan.FromSeconds(5), pipe, requireService: false, mirrorLog: false)!;
        var states = new List<bool>();
        remote.ConnectionChanged += c => { lock (states) states.Add(c); };
        remote.Start();

        host.Dispose();
        Wait(() => { lock (states) return states.Contains(false) ? "" : null; }, "déconnexion");
        Assert.NotNull(remote.SetPriorityOnce(1, "x", ProcessPriorityClass.Normal)); // service absent : erreur, pas de blocage

        using var again = StartHost();
        Wait(() => { lock (states) return states.LastOrDefault() ? "" : null; }, "reconnexion");
        Assert.True(remote.Connected);
    }

    [Fact]
    public void FreshServiceAndLockedSettings()
    {
        using var host = StartHost(new PolicySet(LockSettings: true));
        using var remote = RemoteEngine.Connect(TimeSpan.FromSeconds(5), pipe, requireService: false, mirrorLog: false)!;
        Assert.True(remote.Fresh); // aucune configuration : l'interface lui transmettra celle de l'exe portable
        remote.Start();
        var s = remote.InitialSettings;
        s.Rules.Add(new Rule { Pattern = "interdit.exe" });
        remote.UpdateSettings(s);
        Assert.NotNull(remote.SetPriorityOnce(Environment.ProcessId, "x", ProcessPriorityClass.Normal)); // verrouillé
        var saved = new RuleStore(Path.Combine(dir, "config.json")).Load();
        Assert.DoesNotContain(saved.Rules, r => r.Pattern == "interdit.exe");
    }

    [Fact]
    public void PipeNameCannotBeSquatted()
    {
        using var host = StartHost();
        var intruder = new ServiceHost(Path.Combine(dir, "autre"), pipe, new PolicySet(), powerApi: new NoPower());
        Assert.ThrowsAny<SystemException>(() => intruder.Start()); // IOException, ou accès refusé sans droits administrateur
        intruder.Dispose();
    }

    [Fact]
    public async Task ProtocolRoundTripAndLineLimits()
    {
        var snap = new EngineSnapshot(12.5, false, new[] { new ProcessRow(42, "jeu", 30, 1 << 20, "Jeu", true, @"C:\Jeux\jeu.exe") }, GameMode: true);
        var bytes = Protocol.Encode(new PipeMessage { Type = "snapshot", Snapshot = snap, Text = "film\nen cours" });
        Assert.Equal(1, bytes.Count(b => b == '\n')); // une ligne par message, même avec un saut de ligne dans un texte
        var back = Protocol.Decode(Encoding.UTF8.GetString(bytes).TrimEnd('\n'))!;
        Assert.Equal("film\nen cours", back.Text);
        Assert.Equal(snap.Rows[0], back.Snapshot!.Rows[0]);
        Assert.Null(Protocol.Decode("{pas du json"));

        // Lignes découpées n'importe où
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("un\ndeux\n"));
        var reader = new LineReader(stream);
        Assert.Equal("un", await reader.ReadAsync(default));
        Assert.Equal("deux", await reader.ReadAsync(default));
        Assert.Null(await reader.ReadAsync(default));

        var huge = new LineReader(new MemoryStream(new byte[Protocol.MaxMessage + 100_000]));
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => huge.ReadAsync(default));
    }
}

public class PolicyTests
{
    static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public void AdmxTemplatesMatchPolicies()
    {
        var root = SourceFile(); // les tests sont compilés ailleurs (--artifacts-path) : on part du fichier source
        while (!File.Exists(Path.Combine(root, "Corral.sln")))
            root = Path.GetDirectoryName(root)!;
        var folder = Path.Combine(root, "packaging", "policies");
        var admx = System.Xml.Linq.XDocument.Load(Path.Combine(folder, "Corral.admx"));
        var valueNames = admx.Descendants().Select(e => (string?)e.Attribute("valueName")).Where(v => v != null).ToHashSet();
        // Chaque valeur lue par Policies.Read a sa stratégie
        foreach (var name in new[] { "LockSettings", "DisableUpdates", "ProBalance", "RulesFile", "DisableProcessTermination", "DisableStartupManager" })
            Assert.Contains(name, valueNames);
        Assert.All(admx.Descendants().Select(e => (string?)e.Attribute("key")).Where(k => k != null), k => Assert.Equal(Policies.KeyPath, k));

        var refs = admx.Descendants().SelectMany(e => e.Attributes())
            .Select(a => System.Text.RegularExpressions.Regex.Match(a.Value, @"^\$\(string\.(\w+)\)$"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).ToHashSet();
        foreach (var lang in new[] { "fr-FR", "en-US" })
        {
            var adml = System.Xml.Linq.XDocument.Load(Path.Combine(folder, lang, "Corral.adml"));
            var ids = adml.Descendants().Where(e => e.Name.LocalName == "string").Select(e => (string)e.Attribute("id")!).ToHashSet();
            Assert.Empty(refs.Except(ids));
        }
    }

    [Fact]
    public void PoliciesAreReadAndApplied()
    {
        var keyPath = @"Software\CorralTests\Policies_" + Guid.NewGuid().ToString("N");
        var rulesFile = Path.Combine(Path.GetTempPath(), "CorralPolicyRules_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var empty = Registry.CurrentUser.CreateSubKey(keyPath))
                Assert.False(Policies.Read(empty).Any);
            Assert.False(Policies.Read(null).Any);

            RuleStore.ExportRules(new[] { new Rule { Pattern = "impose.exe", Priority = ProcessPriorityClass.High } }, rulesFile);
            PolicySet set;
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key.SetValue("LockSettings", 1, RegistryValueKind.DWord);
                key.SetValue("DisableUpdates", 1, RegistryValueKind.DWord);
                key.SetValue("ProBalance", 0, RegistryValueKind.DWord);
                key.SetValue("RulesFile", rulesFile);
                key.SetValue("DisableStartupManager", 0, RegistryValueKind.DWord);
                set = Policies.Read(key);
            }
            Assert.Equal(new PolicySet(LockSettings: true, DisableUpdates: true, ProBalance: false, RulesFile: rulesFile), set);

            var s = new Settings();
            s.Rules.Add(new Rule { Pattern = "local.exe" });
            Policies.Apply(s, set);
            Assert.False(s.ProBalance.Enabled);
            Assert.False(s.CheckUpdates);
            Assert.Equal("impose.exe", Assert.Single(s.Rules).Pattern);

            Assert.True(set.Locks("Règles"));
            Assert.True(set.Locks("Mode Jeu"));
            Assert.False(set.Locks("Processus"));
            Assert.False(set.Locks("Démarrage"));
            Assert.True(new PolicySet(DisableStartupManager: true).Locks("Démarrage"));
            Assert.True(new PolicySet(ProBalance: true).Locks("ProBalance"));
            Assert.False(new PolicySet(ProBalance: true).Locks("Règles"));

            // Fichier imposé introuvable : les règles locales sont gardées
            var keep = new Settings();
            keep.Rules.Add(new Rule { Pattern = "local.exe" });
            Policies.Apply(keep, new PolicySet(RulesFile: rulesFile + ".absent"));
            Assert.Equal("local.exe", Assert.Single(keep.Rules).Pattern);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            File.Delete(rulesFile);
        }
    }
}
