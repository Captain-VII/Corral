using System.Text.RegularExpressions;
using Corral.Models;

namespace Corral.Core;

public static class RuleMatcher
{
    /// <summary>Retire les espaces et l'extension « .exe ».</summary>
    public static string Normalize(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>Correspondance insensible à la casse ; « * » et « ? » sont des jokers.</summary>
    public static bool Matches(string pattern, string processName)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;
        var p = Normalize(pattern);
        var n = Normalize(processName);
        if (!p.Contains('*') && !p.Contains('?'))
            return string.Equals(p, n, StringComparison.OrdinalIgnoreCase);
        return Wildcards.GetOrAdd(p, Compile).IsMatch(n);
    }

    /// <summary>
    /// Motifs à jokers déjà convertis. Appelé pour chaque processus à chaque passage (exclusions ProBalance) : le cache de
    /// Regex (15 entrées) ne suffit plus dès quelques motifs, et chaque appel reconstruisait alors l'expression.
    /// </summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Wildcards = new(StringComparer.OrdinalIgnoreCase);

    static Regex Compile(string pattern)
    {
        if (Wildcards.Count > 1000)
            Wildcards.Clear(); // motifs venus des réglages : ne grossit que si on en change sans arrêt
        return new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Première règle active qui correspond (l'ordre de la liste compte).</summary>
    public static Rule? Find(IEnumerable<Rule> rules, string processName) =>
        rules.FirstOrDefault(r => r.Enabled && Matches(r.Pattern, processName));

    /// <summary>Noms des processus en cours visés par un motif (aperçu dans la fenêtre de règle).</summary>
    public static List<string> Preview(string pattern, IEnumerable<string> runningNames) =>
        runningNames.Where(n => Matches(pattern, n)).ToList();

    public static bool IsValidAffinity(long mask, int cpuCount)
    {
        if (mask <= 0)
            return false;
        return cpuCount >= 63 || mask < (1L << cpuCount);
    }
}

/// <summary>Processus jamais modifiés (ni règles, ni ProBalance).</summary>
public static class Exclusions
{
    static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "Secure System", "Memory Compression", "smss", "csrss",
        "wininit", "winlogon", "services", "lsass", "LsaIso", "dwm", "fontdrvhost",
    };

    public static bool IsProtected(string name, int pid, int ownPid) =>
        pid <= 4 || pid == ownPid || Protected.Contains(RuleMatcher.Normalize(name));
}
