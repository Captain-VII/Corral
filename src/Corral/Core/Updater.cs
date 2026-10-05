using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Corral.Core;

public sealed record UpdateInfo(Version Version, string Notes, string PageUrl, string ExeUrl, string ShaUrl);

/// <summary>
/// Mise à jour via GitHub Releases. Une release doit contenir Corral.exe et Corral.exe.sha256
/// (le workflow .github/workflows/release.yml les produit).
/// Installation : l'exe en cours est renommé en .old (Windows l'autorise), le nouveau prend sa place,
/// puis on relance. En cas d'échec, l'ancien exe est remis en place.
/// </summary>
public static class Updater
{
    public const string ExeAsset = "Corral.exe";
    public const string ShaAsset = "Corral.exe.sha256";
    const string Placeholder = "OWNER/Corral";

    static readonly HttpClient http = CreateClient();

    public static Version CurrentVersion => Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0));

    /// <summary>« propriétaire/nom », ou null si non configuré.</summary>
    public static string? Repository
    {
        get
        {
            var value = typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value;
            return value != null && value != Placeholder && Regex.IsMatch(value, @"^[\w.-]+/[\w.-]+$") ? value : null;
        }
    }

    /// <summary>Vrai pour l'exe publié en fichier unique (constante définie dans Corral.csproj).</summary>
#if SINGLE_FILE
    const bool IsSingleFile = true;
#else
    const bool IsSingleFile = false;
#endif

    /// <summary>Uniquement pour l'exe publié en fichier unique (pas en développement avec dotnet run).</summary>
    /// <summary>Exe publié (fichier unique) : seul lui crée des raccourcis système, jamais une version de développement.</summary>
    public static bool IsPublishedBuild => IsSingleFile && Environment.ProcessPath != null;

    public static bool IsSupported =>Repository != null && IsSingleFile && Environment.ProcessPath != null;

    public static string? UnsupportedReason =>
        Repository == null ? Tr("dépôt GitHub non configuré (version compilée localement)", "GitHub repository not configured (locally built version)")
        : !IsSingleFile ? Tr("version de développement", "development version")
        : null;

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{Repository}/releases/latest";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null; // aucune release publiée
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(ct), CurrentVersion);
    }

    /// <summary>Lit la réponse de l'API GitHub ; null si pas plus récent ou release incomplète.</summary>
    public static UpdateInfo? ParseRelease(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean() ||
            root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            return null;
        var version = ParseVersion(root.GetProperty("tag_name").GetString() ?? "");
        if (version == null || version <= Normalize(current))
            return null;

        string? exe = null, sha = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var link = a.GetProperty("browser_download_url").GetString();
                if (string.Equals(name, ExeAsset, StringComparison.OrdinalIgnoreCase)) exe = link;
                else if (string.Equals(name, ShaAsset, StringComparison.OrdinalIgnoreCase)) sha = link;
            }
        }
        if (exe == null || sha == null)
        {
            Log.Warn($"Release {version} ignorée : {ExeAsset} ou {ShaAsset} manquant", LogCategory.Update);
            return null;
        }
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        var page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "";
        return new UpdateInfo(version, notes.Trim(), page, exe, sha);
    }

    /// <summary>« v1.2.3 » → 1.2.3.0 ; les suffixes (« -beta ») sont ignorés.</summary>
    public static Version? ParseVersion(string tag)
    {
        var m = Regex.Match(tag.Trim(), @"^[vV]?(\d+(\.\d+){1,3})");
        return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? Normalize(v) : null;
    }

    static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>Télécharge l'exe à côté de l'actuel (même volume, pour un renommage atomique) et vérifie son SHA-256.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<int>? progress, CancellationToken ct = default)
    {
        var expected = ParseSha(await http.GetStringAsync(info.ShaUrl, ct))
                       ?? throw new InvalidDataException(Tr("Fichier de somme de contrôle illisible", "Unreadable checksum file"));

        var target = Environment.ProcessPath! + ".download";
        using (var response = await http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(target);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                    progress?.Report((int)(done * 100 / total.Value));
            }
        }

        try
        {
            Verify(target, expected);
        }
        catch
        {
            TryDelete(target);
            throw;
        }
        return target;
    }

    public static string? ParseSha(string text)
    {
        var m = Regex.Match(text, @"\b[0-9a-fA-F]{64}\b");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    /// <summary>Vérifie la somme SHA-256 et qu'il s'agit bien d'un exécutable Windows.</summary>
    public static void Verify(string file, string expectedSha)
    {
        using (var stream = File.OpenRead(file))
        {
            Span<byte> header = stackalloc byte[2];
            if (stream.Read(header) != 2 || header[0] != 'M' || header[1] != 'Z')
                throw new InvalidDataException(Tr("Le fichier téléchargé n'est pas un exécutable", "The downloaded file is not an executable"));
            stream.Position = 0;
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (actual != expectedSha.ToLowerInvariant())
                throw new InvalidDataException(Tr("Somme de contrôle incorrecte : téléchargement corrompu", "Wrong checksum: corrupted download"));
        }
    }

    /// <summary>Remplace l'exe et relance. L'appelant doit ensuite quitter l'application.</summary>
    public static void InstallAndRestart(string downloaded)
    {
        var exe = Environment.ProcessPath!;
        ReplaceFiles(exe, downloaded);
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false });
        }
        catch
        {
            Rollback(exe);
            throw;
        }
        Log.Info(Tr("Mise à jour installée, redémarrage", "Update installed, restarting"), LogCategory.Update);
    }

    /// <summary>exe → exe.old, téléchargé → exe. Annule si la seconde étape échoue.</summary>
    public static void ReplaceFiles(string exe, string downloaded)
    {
        var old = exe + ".old";
        TryDelete(old);
        File.Move(exe, old);
        try
        {
            File.Move(downloaded, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
    }

    static void Rollback(string exe)
    {
        try
        {
            File.Move(exe, exe + ".download", overwrite: true);
            File.Move(exe + ".old", exe);
        }
        catch (Exception ex)
        {
            Log.Error("Annulation de la mise à jour", ex);
        }
    }

    /// <summary>
    /// Au démarrage : supprime les restes d'une mise à jour. L'ancien exe peut encore être en train
    /// de se fermer, d'où les nouvelles tentatives.
    /// </summary>
    public static void CleanupLeftovers()
    {
        var exe = Environment.ProcessPath;
        if (exe == null)
            return;
        _ = Task.Run(async () =>
        {
            TryDelete(exe + ".download");
            for (int i = 0; i < 30 && File.Exists(exe + ".old"); i++)
            {
                TryDelete(exe + ".old");
                await Task.Delay(2000);
            }
        });
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Corral", CurrentVersion.ToString(3)));
        return client;
    }
}
