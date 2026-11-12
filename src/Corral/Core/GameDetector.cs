using Microsoft.Win32;

namespace Corral.Core;

/// <summary>
/// Reconnaît un jeu qui n'a pas de règle : exe installé dans une bibliothèque de jeux (Steam, Epic, GOG, EA, Ubisoft,
/// Xbox, Riot…) ou exe que Windows a reconnu comme jeu (Game Bar). Les lanceurs, rapports de plantage, installeurs,
/// anti-triche et outils rangés dans ces dossiers sont écartés.
/// </summary>
public sealed class GameDetector
{
    /// <summary>Dossiers de jeux des boutiques (bibliothèques Steam et dossiers par défaut des autres).</summary>
    static readonly string[] Libraries =
    {
        @"\steamapps\common\", @"\epic games\", @"\gog galaxy\games\", @"\gog games\", @"\riot games\",
        @"\ea games\", @"\origin games\", @"\ubisoft game launcher\games\", @"\xboxgames\", @"\rockstar games\",
        @"\amazon games\library\", @"\itch\apps\",
    };

    /// <summary>Sous-dossiers de ces bibliothèques qui ne contiennent pas de jeu.</summary>
    static readonly string[] IgnoredFolders =
    {
        @"\_commonredist\", @"\commonredist\", @"\redist\", @"\redistributables\", @"\directx\", @"\__installer\",
        @"\installer\", @"\installers\", @"\support\", @"\easyanticheat\", @"\battleye\", @"\binaries\thirdparty\",
        @"\epic games\launcher\", @"\riot games\riot client\", @"\rockstar games\launcher\", @"\rockstar games\social club\",
        @"\steamapps\common\steamvr\", @"\steamapps\common\steamworks shared\", @"\steamapps\common\steam controller configs\",
        // Logiciels vendus sur Steam qui ne sont pas des jeux
        @"\steamapps\common\wallpaper_engine\", @"\steamapps\common\lossless scaling\", @"\steamapps\common\soundpad\",
    };

    /// <summary>
    /// Morceaux de noms d'exe qui ne sont pas des jeux. Pas de mots trop courts ou trop vagues :
    /// « crash » écarterait Crash Bandicoot, « patch » le jeu Dispatch.
    /// </summary>
    static readonly string[] IgnoredNames =
    {
        "launcher", "crashhandler", "crash_handler", "crashpad", "report", "setup", "unins", "install", "redist",
        "prereq", "update", "patcher", "helper", "service", "anticheat", "battleye", "overlay", "wallpaper",
        "cefsharp", "qtwebengine", "bugsplat", "leagueclient",
    };

    /// <summary>La liste de la Game Bar change rarement : relue toutes les 5 minutes au plus.</summary>
    static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    HashSet<string> knownToWindows = new(StringComparer.OrdinalIgnoreCase);
    DateTime knownAt = DateTime.MinValue;

    /// <summary>Ruche de l'utilisateur (le service passe celle de la session connectée).</summary>
    public Func<RegistryKey?> Hive { get; set; } = () => Registry.CurrentUser;

    public bool IsGame(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        path = path.Replace('/', '\\');
        return !IsTool(path) && (InLibrary(path) || KnownToWindows(path));
    }

    public static bool InLibrary(string path) => Libraries.Any(l => path.Contains(l, StringComparison.OrdinalIgnoreCase));

    /// <summary>Lanceur, installeur, anti-triche, outil… même rangé dans un dossier de jeu.</summary>
    public static bool IsTool(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return IgnoredFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase))
            || IgnoredNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    bool KnownToWindows(string path)
    {
        var now = DateTime.UtcNow;
        if (now - knownAt >= RefreshInterval)
        {
            knownToWindows = ReadGameBarList();
            knownAt = now;
        }
        return knownToWindows.Contains(path);
    }

    /// <summary>Exe que la Game Bar a reconnus comme jeux (HKCU\System\GameConfigStore\Children\*\MatchedExeFullPath).</summary>
    HashSet<string> ReadGameBarList()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var hive = Hive();
            using var children = hive?.OpenSubKey(@"System\GameConfigStore\Children");
            foreach (var id in children?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var child = children!.OpenSubKey(id);
                if (child?.GetValue("MatchedExeFullPath") is string exe && exe.Length > 0)
                    set.Add(exe.Replace('/', '\\'));
            }
        }
        catch { } // ruche illisible (session fermée) : seules les bibliothèques comptent jusqu'à la prochaine lecture
        return set;
    }
}
