using Microsoft.Win32;

namespace Corral.Core;

/// <summary>Corral installé par le MSI (Program Files) plutôt que lancé en exe portable.</summary>
public static class Installation
{
    /// <summary>Clé écrite par l'installeur (packaging/Corral.wxs).</summary>
    public const string KeyPath = @"SOFTWARE\Corral";

    /// <summary>Vrai si l'exe en cours est celui installé par le MSI : le raccourci du menu Démarrer est alors géré par l'installeur.</summary>
    public static bool IsMsi => Updater.IsPublishedBuild && IsInstallDir(ReadInstallDir(), Environment.ProcessPath!);

    public static bool IsInstallDir(string? installDir, string exePath) =>
        !string.IsNullOrWhiteSpace(installDir)
        && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir)),
                         Path.GetDirectoryName(Path.GetFullPath(exePath)), StringComparison.OrdinalIgnoreCase);

    static string? ReadInstallDir()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(KeyPath);
            return key?.GetValue("InstallDir") as string;
        }
        catch
        {
            return null;
        }
    }
}
