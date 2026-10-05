using System.Text.Json;
using Corral.Models;

namespace Corral.Core;

/// <summary>
/// Profils : plusieurs jeux de règles (Travail, Jeu, Silencieux…) dont un seul est actif.
/// Les règles du profil actif vivent dans Settings.Rules ; les autres profils gardent les leurs.
/// </summary>
public static class Profiles
{
    /// <summary>Garantit qu'il existe une entrée pour le profil actif.</summary>
    public static void Ensure(Settings s, string defaultName)
    {
        if (string.IsNullOrWhiteSpace(s.ActiveProfile))
            s.ActiveProfile = s.Profiles.FirstOrDefault()?.Name ?? defaultName;
        if (Find(s, s.ActiveProfile) == null)
            s.Profiles.Insert(0, new Profile { Name = s.ActiveProfile });
    }

    public static Profile? Find(Settings s, string name) =>
        s.Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Change de profil : les règles actuelles sont rangées, celles du profil choisi deviennent actives.</summary>
    public static bool Switch(Settings s, string name)
    {
        var target = Find(s, name);
        var current = Find(s, s.ActiveProfile);
        if (target == null || target == current)
            return false;
        if (current != null)
            current.Rules = s.Rules;
        s.Rules = target.Rules;
        target.Rules = new();
        s.ActiveProfile = target.Name;
        return true;
    }

    /// <summary>Nouveau profil, vide ou copie des règles actuelles. Renvoie null si le nom est vide ou déjà pris.</summary>
    public static Profile? Create(Settings s, string name, bool copyCurrent)
    {
        name = name.Trim();
        if (name.Length == 0 || Find(s, name) != null)
            return null;
        var p = new Profile { Name = name, Rules = copyCurrent ? CloneRules(s.Rules) : new() };
        s.Profiles.Add(p);
        return p;
    }

    public static bool Rename(Settings s, string oldName, string newName)
    {
        newName = newName.Trim();
        var p = Find(s, oldName);
        var clash = Find(s, newName);
        if (p == null || newName.Length == 0 || (clash != null && clash != p))
            return false;
        if (string.Equals(s.ActiveProfile, p.Name, StringComparison.CurrentCultureIgnoreCase))
            s.ActiveProfile = newName;
        p.Name = newName;
        return true;
    }

    /// <summary>Supprime un profil inactif.</summary>
    public static bool Delete(Settings s, string name)
    {
        var p = Find(s, name);
        if (p == null || p == Find(s, s.ActiveProfile))
            return false;
        s.Profiles.Remove(p);
        return true;
    }

    static List<Rule> CloneRules(List<Rule> rules) =>
        JsonSerializer.Deserialize<List<Rule>>(JsonSerializer.Serialize(rules, RuleStore.Options), RuleStore.Options) ?? new();
}
