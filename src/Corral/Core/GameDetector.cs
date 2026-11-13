using System.Text.Json;
using Microsoft.Win32;

namespace Corral.Core;

/// <summary>
/// Reconnaît un jeu qui n'a pas de règle : exe installé dans une bibliothèque de jeux (Steam, Epic, GOG, EA, Ubisoft,
/// Xbox, Riot…), dans un dossier qu'un lanceur déclare (jeu installé ailleurs que par défaut), ou exe que Windows a
/// reconnu comme jeu (Game Bar). Les lanceurs, rapports de plantage, installeurs, anti-triche et outils rangés dans ces
/// dossiers sont écartés.
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
        @"\epic games\launcher\", @"\epic games\ue_", @"\riot games\riot client\", @"\rockstar games\launcher\",
        @"\rockstar games\social club\",
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
        "cefsharp", "qtwebengine", "bugsplat", "leagueclient", "unrealeditor",
    };

    /// <summary>Dossiers qu'un manifeste abîmé ne doit jamais faire passer pour un dossier de jeu.</summary>
    static readonly HashSet<string> SystemFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Files", "Program Files (x86)", "ProgramData", "Users", "Windows",
    };

    /// <summary>Les listes changent rarement : relues toutes les 5 minutes au plus.</summary>
    static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    HashSet<string> knownToWindows = new(StringComparer.OrdinalIgnoreCase);
    List<string> installFolders = new();
    DateTime readAt = DateTime.MinValue;

    /// <summary>Ruche de l'utilisateur (le service passe celle de la session connectée).</summary>
    public Func<RegistryKey?> Hive { get; set; } = () => Registry.CurrentUser;

    /// <summary>Dossiers d'installation déclarés par les lanceurs (remplaçable par les tests).</summary>
    public Func<IEnumerable<string>> LauncherFolders { get; set; } = ReadLauncherFolders;

    public bool IsGame(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        path = path.Replace('/', '\\');
        if (IsTool(path))
            return false;
        if (InLibrary(path))
            return true;
        Refresh();
        return knownToWindows.Contains(path) || installFolders.Any(f => path.StartsWith(f, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Nom à afficher : dossier du jeu dans sa bibliothèque ou dossier déclaré par le lanceur, ou null (le nom de l'exe
    /// reste alors). L'exe peut n'être qu'un lanceur d'anti-triche (start_protected_game.exe).
    /// </summary>
    public string? Title(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        path = path.Replace('/', '\\');
        foreach (var library in Libraries)
        {
            int i = path.IndexOf(library, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
            {
                var rest = path[(i + library.Length)..];
                int end = rest.IndexOf('\\');
                return end > 0 ? rest[..end] : null; // exe posé à la racine de la bibliothèque : pas de dossier de jeu
            }
        }
        var folder = installFolders.FirstOrDefault(f => path.StartsWith(f, StringComparison.OrdinalIgnoreCase));
        return folder != null ? Path.GetFileName(folder.TrimEnd('\\')) : null;
    }

    public static bool InLibrary(string path) => Libraries.Any(l => path.Contains(l, StringComparison.OrdinalIgnoreCase));

    /// <summary>Lanceur, installeur, anti-triche, outil… même rangé dans un dossier de jeu.</summary>
    public static bool IsTool(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return IgnoredFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase))
            || IgnoredNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Dossier de jeu normalisé (« \ » final), ou null s'il est relatif ou trop large : disque entier, dossier système
    /// ou profil utilisateur feraient de chaque programme un jeu.
    /// </summary>
    public static string? AsInstallFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;
        var f = folder.Trim().Replace('/', '\\').TrimEnd('\\');
        if (!Path.IsPathFullyQualified(f + '\\'))
            return null;
        var parts = f.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || (parts.Length == 2 && SystemFolders.Contains(parts[1]))
            || (parts.Length == 3 && parts[1].Equals("Users", StringComparison.OrdinalIgnoreCase)))
            return null;
        return f + '\\';
    }

    /// <summary>Dossier d'installation d'un manifeste Epic (.item), ou null.</summary>
    public static string? EpicInstallLocation(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("InstallLocation", out var v)
                && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    void Refresh()
    {
        var now = DateTime.UtcNow;
        if (now - readAt < RefreshInterval)
            return;
        readAt = now;
        knownToWindows = ReadGameBarList();
        var folders = new List<string>();
        try
        {
            foreach (var folder in LauncherFolders())
                if (AsInstallFolder(folder) is { } f)
                    folders.Add(f);
        }
        catch { } // liste illisible : les bibliothèques par défaut et la Game Bar restent
        installFolders = folders;
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

    /// <summary>Jeux installés hors des dossiers par défaut : manifestes Epic, registre de GOG Galaxy, Ubisoft Connect et EA.</summary>
    static IEnumerable<string> ReadLauncherFolders()
    {
        var folders = new List<string>();
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        try
        {
            foreach (var file in Directory.Exists(manifests) ? Directory.EnumerateFiles(manifests, "*.item") : Array.Empty<string>())
            {
                try
                {
                    if (EpicInstallLocation(File.ReadAllText(file)) is { } dir)
                        folders.Add(dir);
                }
                catch (IOException) { } // manifeste en cours d'écriture par le lanceur
            }
        }
        catch (Exception) { } // dossier illisible
        folders.AddRange(RegistryValues(@"SOFTWARE\GOG.com\Games", "path"));
        folders.AddRange(RegistryValues(@"SOFTWARE\Ubisoft\Launcher\Installs", "InstallDir"));
        folders.AddRange(RegistryValues(@"SOFTWARE\EA Games", "Install Dir"));
        return folders;
    }

    /// <summary>Valeur <paramref name="value"/> de chaque sous-clé de HKLM\<paramref name="path"/> (vue 32 bits, celle des lanceurs).</summary>
    static List<string> RegistryValues(string path, string value)
    {
        var found = new List<string>();
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var root = hklm.OpenSubKey(path);
            foreach (var name in root?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var key = root!.OpenSubKey(name);
                if (key?.GetValue(value) is string dir)
                    found.Add(dir);
            }
        }
        catch (Exception) { } // clé absente ou illisible
        return found;
    }
}
