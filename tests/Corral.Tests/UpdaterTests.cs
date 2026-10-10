using System.Security.Cryptography;
using Corral.Core;

namespace Corral.Tests;

public class UpdaterTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "CorralUpd_" + Guid.NewGuid().ToString("N"));

    public UpdaterTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    static string Release(string tag, bool withExe = true, bool withSha = true, bool prerelease = false, bool withSig = true)
    {
        var assets = new List<string>();
        if (withExe) assets.Add("""{ "name": "Corral.exe", "browser_download_url": "https://example/Corral.exe" }""");
        if (withSha) assets.Add("""{ "name": "Corral.exe.sha256", "browser_download_url": "https://example/Corral.exe.sha256" }""");
        if (withSig) assets.Add("""{ "name": "Corral.exe.sig", "browser_download_url": "https://example/Corral.exe.sig" }""");
        return $$"""
            { "tag_name": "{{tag}}", "draft": false, "prerelease": {{(prerelease ? "true" : "false")}},
              "body": "Nouveautés", "html_url": "https://example/release",
              "assets": [{{string.Join(",", assets)}}] }
            """;
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData("V2.0.1-beta", "2.0.1.0")]
    [InlineData("release", null)]
    public void ParseVersion(string tag, string? expected) =>
        Assert.Equal(expected, Updater.ParseVersion(tag)?.ToString());

    [Fact]
    public void EachArchitectureGetsItsOwnFiles()
    {
        var json = """
            { "tag_name": "v3.0.0", "draft": false, "prerelease": false, "body": "", "html_url": "",
              "assets": [
                { "name": "Corral.exe", "browser_download_url": "x64.exe" },
                { "name": "Corral.exe.sha256", "browser_download_url": "x64.sha" },
                { "name": "Corral.exe.sig", "browser_download_url": "x64.sig" },
                { "name": "Corral-arm64.exe", "browser_download_url": "arm.exe" },
                { "name": "Corral-arm64.exe.sha256", "browser_download_url": "arm.sha" },
                { "name": "Corral-arm64.exe.sig", "browser_download_url": "arm.sig" },
                { "name": "Corral-arm64.msi", "browser_download_url": "arm.msi" },
                { "name": "Corral-arm64.msi.sig", "browser_download_url": "arm.msi.sig" } ] }
            """;
        var x64 = Updater.ParseRelease(json, new Version(2, 0), System.Runtime.InteropServices.Architecture.X64)!;
        Assert.Equal(("x64.exe", "x64.sig", null), (x64.ExeUrl, x64.SigUrl, x64.MsiUrl));
        var arm = Updater.ParseRelease(json, new Version(2, 0), System.Runtime.InteropServices.Architecture.Arm64)!;
        Assert.Equal(("arm.exe", "arm.sha", "arm.msi", "arm.msi.sig"), (arm.ExeUrl, arm.ShaUrl, arm.MsiUrl, arm.MsiSigUrl));
    }

    [Fact]
    public void NewerReleaseIsOffered()
    {
        var info = Updater.ParseRelease(Release("v1.1.0"), new Version(1, 0, 0, 0));
        Assert.NotNull(info);
        Assert.Equal(new Version(1, 1, 0, 0), info!.Version);
        Assert.Equal("https://example/Corral.exe", info.ExeUrl);
        Assert.Equal("https://example/Corral.exe.sig", info.SigUrl);
        Assert.Equal("Nouveautés", info.Notes);
    }

    [Theory]
    [InlineData("v1.0.0")] // même version (1.0.0 == 1.0.0.0)
    [InlineData("v0.9.0")]
    public void SameOrOlderIsIgnored(string tag) =>
        Assert.Null(Updater.ParseRelease(Release(tag), new Version(1, 0, 0)));

    [Fact]
    public void IncompleteOrPrereleaseIsIgnored()
    {
        var current = new Version(1, 0, 0);
        Assert.Null(Updater.ParseRelease(Release("v2.0.0", withExe: false), current));
        Assert.Null(Updater.ParseRelease(Release("v2.0.0", withSha: false), current));
        Assert.Null(Updater.ParseRelease(Release("v2.0.0", withSig: false), current));
        Assert.Null(Updater.ParseRelease(Release("v2.0.0", prerelease: true), current));
    }

    [Fact]
    public void ShaParsing()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, Updater.ParseSha($"{hash.ToUpperInvariant()}  Corral.exe\n"));
        Assert.Null(Updater.ParseSha("pas un hash"));
    }

    [Fact]
    public void VerifyChecksHashAndExeHeader()
    {
        var file = Path.Combine(dir, "x.exe");
        File.WriteAllBytes(file, new byte[] { (byte)'M', (byte)'Z', 1, 2, 3 });
        var good = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
        Updater.Verify(file, good);
        Assert.Throws<InvalidDataException>(() => Updater.Verify(file, new string('0', 64)));

        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        Assert.Throws<InvalidDataException>(() => Updater.Verify(file, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))));
    }

    [Fact]
    public void SignatureMustMatchFileAndKey()
    {
        var file = Path.Combine(dir, "x.exe");
        File.WriteAllBytes(file, new byte[] { (byte)'M', (byte)'Z', 1, 2, 3 });
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var signature = Convert.ToBase64String(key.SignData(File.ReadAllBytes(file), HashAlgorithmName.SHA256));

        Updater.VerifySignature(file, signature + "\n", publicKey);
        // Clé du projet : une signature faite avec une autre clé est refusée
        Assert.Throws<InvalidDataException>(() => Updater.VerifySignature(file, signature, Updater.PublicKey));
        Assert.Throws<InvalidDataException>(() => Updater.VerifySignature(file, "pas du base64 !", publicKey));
        File.AppendAllText(file, "piège");
        Assert.Throws<InvalidDataException>(() => Updater.VerifySignature(file, signature, publicKey));
    }

    [Fact]
    public void ReplaceFilesSwapsAndKeepsOld()
    {
        var exe = Path.Combine(dir, "Corral.exe");
        var dl = exe + ".download";
        File.WriteAllText(exe, "ancien");
        File.WriteAllText(dl, "nouveau");
        File.WriteAllText(exe + ".old", "reste d'une mise à jour précédente");

        Updater.ReplaceFiles(exe, dl);

        Assert.Equal("nouveau", File.ReadAllText(exe));
        Assert.Equal("ancien", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(dl));
    }

    [Fact]
    public void ReplaceFilesRollsBackOnFailure()
    {
        var exe = Path.Combine(dir, "Corral.exe");
        File.WriteAllText(exe, "ancien");
        Assert.ThrowsAny<IOException>(() => Updater.ReplaceFiles(exe, Path.Combine(dir, "absent.download")));
        Assert.Equal("ancien", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".old"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Corral", @"C:\Program Files\Corral\Corral.exe", true)]
    [InlineData(@"c:\program files\corral\", @"C:\Program Files\Corral\Corral.exe", true)]
    [InlineData(@"C:\Program Files\Corral", @"C:\Outils\Corral.exe", false)]
    [InlineData(null, @"C:\Program Files\Corral\Corral.exe", false)]
    public void MsiInstallIsDetectedByFolder(string? installDir, string exe, bool expected) =>
        Assert.Equal(expected, Installation.IsInstallDir(installDir, exe));

    [Fact]
    public void LocalBuildHasUpdatesDisabled()
    {
        // Compilé hors workflow : le dépôt vaut OWNER/Corral, donc pas de mise à jour.
        Assert.Null(Updater.Repository);
        Assert.False(Updater.IsSupported);
    }
}
