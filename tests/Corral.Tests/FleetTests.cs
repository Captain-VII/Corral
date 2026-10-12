using System.Diagnostics;
using System.Text.Json;
using Corral.Core;
using Corral.Models;

namespace Corral.Tests;

public class FleetTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "CorralFleet_" + Guid.NewGuid().ToString("N"));

    public FleetTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    /// <summary>Publie dans <paramref name="folder"/> une configuration signée (clé créée au premier appel) ; renvoie la source et la clé publique.</summary>
    public static (string Source, string PublicKey) Publish(string folder, long serial, string rule, bool withProBalance = false)
    {
        var source = Path.Combine(folder, "parc", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        var pem = Path.Combine(folder, "parc.pem");
        if (!File.Exists(pem))
            Fleet.KeyGen(pem);
        var config = new FleetConfig { Serial = serial, Rules = new() { new Rule { Pattern = rule, Priority = ProcessPriorityClass.BelowNormal } } };
        if (withProBalance)
            config.ProBalance = new ProBalanceSettings { Enabled = false };
        File.WriteAllText(source, JsonSerializer.Serialize(config, RuleStore.Options));
        return (source, Fleet.Sign(source, pem));
    }

    string Cache => Path.Combine(dir, "cache");

    [Fact]
    public void SignedConfigurationIsVerified()
    {
        var (source, key) = Publish(dir, 1, "a.exe", withProBalance: true);
        var config = Fleet.Verify(File.ReadAllBytes(source), File.ReadAllText(source + ".sig"), key);
        Assert.Equal(new[] { "Règles", "ProBalance" }, config.Pages());

        // Modifié après signature, ou vérifié avec une autre clé : refusé
        var tampered = File.ReadAllBytes(source);
        tampered[^3] ^= 1;
        Assert.Throws<InvalidDataException>(() => Fleet.Verify(tampered, File.ReadAllText(source + ".sig"), key));
        Assert.Throws<InvalidDataException>(() => Fleet.Verify(File.ReadAllBytes(source), File.ReadAllText(source + ".sig"), Updater.PublicKey));
        Assert.Throws<InvalidDataException>(() => Fleet.Verify(File.ReadAllBytes(source), "pas une signature", key));
        Assert.Throws<IOException>(() => Fleet.KeyGen(Path.Combine(dir, "parc.pem"))); // une clé n'est jamais remplacée

        // Un fichier d'une autre application n'est pas signé
        var other = Path.Combine(dir, "autre.json");
        File.WriteAllText(other, """{ "App": "Autre", "Rules": [] }""");
        Assert.Throws<InvalidDataException>(() => Fleet.Sign(other, Path.Combine(dir, "parc.pem")));
    }

    [Fact]
    public void LoadKeepsLastValidVersion()
    {
        var (source, key) = Publish(dir, 5, "v5.exe");
        Assert.Equal(5, Fleet.Load(source, key, Cache)!.Serial);

        // Source injoignable : le cache prend le relais
        File.Delete(source);
        Assert.Equal(5, Fleet.Load(source, key, Cache)!.Serial);

        // Version plus ancienne (rejeu) : refusée
        Publish(dir, 4, "v4.exe");
        Assert.Equal("v5.exe", Fleet.Load(source, key, Cache)!.Rules![0].Pattern);

        // Version plus récente mais signature abîmée : refusée
        Publish(dir, 6, "v6.exe");
        var sig = File.ReadAllText(source + ".sig");
        File.WriteAllText(source + ".sig", (sig[0] == 'A' ? "B" : "A") + sig[1..]);
        Assert.Equal(5, Fleet.Load(source, key, Cache)!.Serial);

        Assert.Null(Fleet.Load("http://exemple.invalid/config.json", key, Path.Combine(dir, "vide")));
        Assert.Null(Fleet.Load(source, key, Path.Combine(dir, "vide")));
    }

    [Fact]
    public void ApplyReplacesSectionsAndLocksPages()
    {
        var s = new Settings();
        s.Rules.Add(new Rule { Pattern = "local.exe" });
        var config = new FleetConfig
        {
            Rules = new() { new Rule { Pattern = " parc.exe ", CpuLimitPercent = 500 }, new Rule { Pattern = "" } },
            GameMode = new GameModeSettings { Automatic = false },
        };
        Fleet.Apply(s, config);
        var rule = Assert.Single(s.Rules);
        Assert.Equal(("parc.exe", (int?)null), (rule.Pattern, rule.CpuLimitPercent)); // valeurs hors bornes retirées
        Assert.False(s.GameMode.Automatic);
        Assert.True(s.ProBalance.Enabled); // section absente : configuration locale gardée
        Assert.True(new PolicySet().Locks("Règles", s));
        Assert.True(new PolicySet().Locks("Mode Jeu", s));
        Assert.False(new PolicySet().Locks("ProBalance", s));

        config.Rules[0].Pattern = "modifié.exe";
        Assert.Equal("parc.exe", s.Rules[0].Pattern); // copie : la configuration du parc n'est pas liée aux réglages

        Fleet.Apply(s, null);
        Assert.Null(s.Managed);
    }

    [Fact]
    public void CommandLineSignsAndChecks()
    {
        var o = new StringWriter();
        var pem = Path.Combine(dir, "cli.pem");
        Assert.Equal(Cli.Ok, Cli.Run(new[] { "fleet", "keygen", pem }, o, o));
        var publicKey = o.ToString().Trim().Split('\n')[^1].Trim();
        var config = Path.Combine(dir, "config.json");
        File.WriteAllText(config, """{ "App": "Corral", "Serial": 3, "Rules": [ { "Pattern": "x.exe" } ] }""");
        Assert.Equal(Cli.Ok, Cli.Run(new[] { "fleet", "sign", config, pem }, o, o));
        Assert.Equal(Cli.Ok, Cli.Run(new[] { "fleet", "check", config, publicKey }, o, o));
        File.AppendAllText(config, " ");
        Assert.Equal(Cli.Failed, Cli.Run(new[] { "fleet", "check", config, publicKey }, o, o));
        Assert.Equal(Cli.Usage, Cli.Run(new[] { "fleet", "sign" }, o, o));
    }
}
