using System.Security.Cryptography;
using System.Text.Json;
using Corral.Models;

namespace Corral.Core;

/// <summary>
/// Configuration de parc : fichier JSON publié par l'équipe informatique (partage réseau ou https),
/// signé avec la clé de l'organisation (ECDSA P-256, signature base64 dans « fichier.sig »).
/// Chaque section présente remplace la configuration locale et devient non modifiable sur le poste.
/// </summary>
public sealed class FleetConfig
{
    public string App { get; set; } = "Corral";
    /// <summary>Numéro de version croissant : un fichier plus ancien que celui déjà reçu est refusé (rejeu).</summary>
    public long Serial { get; set; }
    public List<Rule>? Rules { get; set; }
    public ProBalanceSettings? ProBalance { get; set; }
    public GameModeSettings? GameMode { get; set; }

    /// <summary>Pages de l'interface verrouillées par ce fichier (voir <see cref="Policies.Locks"/>).</summary>
    public List<string> Pages()
    {
        var pages = new List<string>();
        if (Rules != null) pages.Add("Règles");
        if (ProBalance != null) pages.Add("ProBalance");
        if (GameMode != null) pages.Add("Mode Jeu");
        return pages;
    }
}

public static class Fleet
{
    public const string CacheName = "fleet.json";
    static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    static readonly HttpClient http = new() { Timeout = FetchTimeout };

    /// <summary>Vérifie la signature et lit le fichier. Lève <see cref="InvalidDataException"/> sinon.</summary>
    public static FleetConfig Verify(byte[] json, string signatureBase64, string publicKey)
    {
        bool valid;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
            valid = key.VerifyData(json, Convert.FromBase64String(signatureBase64.Trim()), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            valid = false;
        }
        if (!valid)
            throw new InvalidDataException(Tr("Signature de la configuration de parc invalide (fichier modifié, ou mauvaise clé publique).",
                "Invalid fleet configuration signature (file modified, or wrong public key)."));
        FleetConfig? config;
        try { config = JsonSerializer.Deserialize<FleetConfig>(json, RuleStore.Options); }
        catch (JsonException ex) { throw new InvalidDataException(Tr("Configuration de parc illisible : ", "Unreadable fleet configuration: ") + ex.Message, ex); }
        if (config == null || config.App != "Corral")
            throw new InvalidDataException(Tr("Ce fichier n'est pas une configuration de parc Corral.", "This file is not a Corral fleet configuration."));
        return config;
    }

    /// <summary>
    /// Télécharge et vérifie la configuration, puis la garde en cache dans <paramref name="cacheDir"/>.
    /// Source injoignable ou invalide : on reprend la dernière version valide du cache. Null si aucune n'est disponible.
    /// </summary>
    public static FleetConfig? Load(string source, string publicKey, string cacheDir)
    {
        var cache = Path.Combine(cacheDir, CacheName);
        var cached = ReadCache(cache, publicKey);
        try
        {
            var json = Fetch(source);
            var sig = System.Text.Encoding.ASCII.GetString(Fetch(source + ".sig"));
            var config = Verify(json, sig, publicKey);
            if (cached != null && config.Serial < cached.Serial)
                throw new InvalidDataException(Tr($"Configuration de parc plus ancienne (n° {config.Serial}) que celle déjà reçue (n° {cached.Serial}) : ignorée.",
                    $"Fleet configuration older (#{config.Serial}) than the one already received (#{cached.Serial}): ignored."));
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(cache, json);
            File.WriteAllText(cache + ".sig", sig);
            return config;
        }
        catch (Exception ex)
        {
            Log.Warn(Tr($"Configuration de parc ({source}) : {ex.Message}", $"Fleet configuration ({source}): {ex.Message}")
                     + (cached != null ? Tr(" Dernière version reçue conservée.", " Last received version kept.") : ""));
            return cached;
        }
    }

    static FleetConfig? ReadCache(string cache, string publicKey)
    {
        try
        {
            return File.Exists(cache) && File.Exists(cache + ".sig")
                ? Verify(File.ReadAllBytes(cache), File.ReadAllText(cache + ".sig"), publicKey)
                : null;
        }
        catch (Exception ex)
        {
            Log.Warn(Tr("Cache de la configuration de parc invalide : ", "Invalid fleet configuration cache: ") + ex.Message);
            return null;
        }
    }

    /// <summary>Fichier local ou partage réseau, ou adresse https (pas http : la configuration doit rester confidentielle et à jour).</summary>
    static byte[] Fetch(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            if (uri.Scheme != "https")
                throw new InvalidDataException(Tr("Seules les adresses https sont acceptées.", "Only https addresses are accepted."));
            return http.GetByteArrayAsync(uri).GetAwaiter().GetResult();
        }
        return File.ReadAllBytes(source);
    }

    /// <summary>Remplace les sections fournies par le parc et note les pages verrouillées.</summary>
    public static void Apply(Settings settings, FleetConfig? config)
    {
        settings.Managed = config?.Pages() is { Count: > 0 } pages ? pages : null;
        if (config == null)
            return;
        if (config.Rules != null)
        {
            settings.Rules = RuleStore.Sanitize(Profiles.CloneRules(config.Rules));
        }
        if (config.ProBalance != null)
            settings.ProBalance = JsonSerializer.Deserialize<ProBalanceSettings>(JsonSerializer.Serialize(config.ProBalance, RuleStore.Options), RuleStore.Options)!;
        if (config.GameMode != null)
            settings.GameMode = JsonSerializer.Deserialize<GameModeSettings>(JsonSerializer.Serialize(config.GameMode, RuleStore.Options), RuleStore.Options)!;
        settings.Normalize();
    }

    /// <summary>Crée la clé privée de l'organisation (PEM) ; renvoie la clé publique à mettre dans la stratégie.</summary>
    public static string KeyGen(string pemPath)
    {
        if (File.Exists(pemPath))
            throw new IOException(Tr($"{pemPath} existe déjà : une clé n'est jamais remplacée.", $"{pemPath} already exists: a key is never replaced."));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(pemPath, key.ExportPkcs8PrivateKeyPem());
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Vérifie que le fichier est une configuration valide, puis écrit « fichier.sig ». Renvoie la clé publique.</summary>
    public static string Sign(string configPath, string pemPath)
    {
        var json = File.ReadAllBytes(configPath);
        try
        {
            if (JsonSerializer.Deserialize<FleetConfig>(json, RuleStore.Options) is not { App: "Corral" })
                throw new InvalidDataException(Tr("Ce fichier n'est pas une configuration de parc Corral.", "This file is not a Corral fleet configuration."));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Tr("Configuration de parc illisible : ", "Unreadable fleet configuration: ") + ex.Message, ex);
        }
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(pemPath));
        File.WriteAllText(configPath + ".sig", Convert.ToBase64String(key.SignData(json, HashAlgorithmName.SHA256)));
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }
}
