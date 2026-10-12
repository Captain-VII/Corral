using Microsoft.Win32;
using Corral.Models;

namespace Corral.Core;

/// <summary>
/// Réglages imposés par l'organisation (stratégie de groupe, Intune…), lus dans HKLM\SOFTWARE\Policies\Corral.
/// Les modèles ADMX sont dans packaging/policies. Lus au démarrage : un changement prend effet au prochain lancement.
/// </summary>
/// <param name="LockSettings">Personne ne peut modifier les réglages depuis l'interface.</param>
/// <param name="ProBalance">ProBalance forcé (activé ou désactivé), ou null.</param>
/// <param name="RulesFile">Export de règles imposé : remplace les règles locales.</param>
/// <param name="ConfigFile">Configuration de parc signée (chemin, partage ou https), voir <see cref="Fleet"/>.</param>
/// <param name="ConfigPublicKey">Clé publique de l'organisation qui a signé <paramref name="ConfigFile"/>.</param>
public sealed record PolicySet(bool LockSettings = false, bool DisableUpdates = false, bool? ProBalance = null,
    string? RulesFile = null, bool DisableProcessTermination = false, bool DisableStartupManager = false,
    string? ConfigFile = null, string? ConfigPublicKey = null)
{
    public bool Any => this != new PolicySet();

    /// <summary>Configuration de parc demandée (source et clé renseignées).</summary>
    public bool HasFleet => ConfigFile != null && ConfigPublicKey != null;

    /// <summary>Charge la configuration de parc (cache dans <paramref name="cacheDir"/>), ou null.</summary>
    public FleetConfig? LoadFleet(string cacheDir) => HasFleet ? Fleet.Load(ConfigFile!, ConfigPublicKey!, cacheDir) : null;
}

public static class Policies
{
    public const string KeyPath = @"SOFTWARE\Policies\Corral";

    static PolicySet? current;

    /// <summary>Stratégies en vigueur (lues une fois).</summary>
    public static PolicySet Current => current ??= ReadMachine();

    /// <summary>Remplace les stratégies (tests).</summary>
    public static void Override(PolicySet? set) => current = set;

    static PolicySet ReadMachine()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(KeyPath);
            var set = Read(key);
            if (set.Any)
                Log.Info(Tr($"Réglages imposés par l'organisation : {set}", $"Settings enforced by the organization: {set}"));
            return set;
        }
        catch (Exception ex)
        {
            Log.Error("Lecture des stratégies", ex);
            return new PolicySet();
        }
    }

    public static PolicySet Read(RegistryKey? key)
    {
        if (key == null)
            return new PolicySet();
        bool Flag(string name) => key.GetValue(name) is int v && v != 0;
        bool? Tri(string name) => key.GetValue(name) is int v ? v != 0 : null;
        string? Text(string name) => key.GetValue(name) is string v && !string.IsNullOrWhiteSpace(v) ? Environment.ExpandEnvironmentVariables(v.Trim()) : null;
        return new PolicySet(Flag("LockSettings"), Flag("DisableUpdates"), Tri("ProBalance"), Text("RulesFile"),
            Flag("DisableProcessTermination"), Flag("DisableStartupManager"), Text("ConfigFile"), Text("ConfigPublicKey"));
    }

    /// <summary>Applique les stratégies à la configuration (en place).</summary>
    public static void Apply(Settings settings, PolicySet set)
    {
        if (set.ProBalance is bool pb)
            settings.ProBalance.Enabled = pb;
        if (set.DisableUpdates)
            settings.CheckUpdates = false;
        if (set.RulesFile != null)
        {
            try
            {
                settings.Rules = RuleStore.ImportRules(set.RulesFile);
            }
            catch (Exception ex)
            {
                // Fichier inaccessible (partage réseau absent…) : on garde les règles locales plutôt que de tout perdre
                Log.Warn(Tr($"Règles imposées illisibles ({set.RulesFile}) : {ex.Message}", $"Enforced rules unreadable ({set.RulesFile}): {ex.Message}"));
            }
        }
    }

    /// <summary>La page de réglages <paramref name="page"/> est-elle verrouillée (stratégie, ou configuration de parc) ?</summary>
    public static bool Locks(this PolicySet set, string page, Settings? settings = null) => settings?.Managed?.Contains(page) == true
        || set.LockSettings && page is "Règles" or "ProBalance" or "Mode Jeu" or "Optimisations"
        || page == "Règles" && set.RulesFile != null
        || page == "ProBalance" && set.ProBalance != null
        || page == "Démarrage" && set.DisableStartupManager;
}
