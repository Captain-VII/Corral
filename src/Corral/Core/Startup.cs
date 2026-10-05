using System.Diagnostics;
using Microsoft.Win32;

namespace Corral.Core;

public enum StartupSource { UserRun, MachineRun, MachineRun32, UserFolder, CommonFolder }

/// <param name="Entry">Nom de la valeur du Registre, ou nom du fichier dans le dossier Démarrage.</param>
/// <param name="Path">Exécutable lancé (pour l'icône, l'éditeur et « Ouvrir l'emplacement »), null s'il est introuvable.</param>
public sealed record StartupItem(string Name, string Entry, string Command, StartupSource Source, bool Enabled, string? Path, string? Publisher)
{
    public string SourceLabel => Source switch
    {
        StartupSource.UserRun => "Registre (utilisateur)",
        StartupSource.MachineRun => "Registre (tous)",
        StartupSource.MachineRun32 => "Registre (tous, 32 bits)",
        StartupSource.UserFolder => "Dossier Démarrage",
        _ => "Dossier Démarrage (tous)",
    };
}

/// <summary>
/// Programmes lancés à l'ouverture de session (clés Run et dossiers Démarrage). L'activation passe par les clés
/// StartupApproved, exactement comme le Gestionnaire des tâches : rien n'est supprimé, tout reste réversible.
/// </summary>
public sealed class StartupManager
{
    /// <summary>Racine du Registre et préfixe des chemins (« Software\ » ; autre chose pour les tests).</summary>
    public readonly record struct Hive(RegistryKey Root, string Prefix);

    const string RunPath = @"Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedPath = @"Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    readonly Hive user, machine;
    readonly Hive? machine32;
    readonly string? userFolder, commonFolder;

    public StartupManager(Hive user, Hive machine, Hive? machine32, string? userFolder, string? commonFolder)
    {
        this.user = user;
        this.machine = machine;
        this.machine32 = machine32;
        this.userFolder = userFolder;
        this.commonFolder = commonFolder;
    }

    public static StartupManager ForSystem() => new(
        new Hive(Registry.CurrentUser, @"Software\"),
        new Hive(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), @"Software\"),
        Environment.Is64BitOperatingSystem ? new Hive(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32), @"Software\") : null,
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup));

    public List<StartupItem> List()
    {
        var items = new List<StartupItem>();
        ReadRun(items, user, StartupSource.UserRun);
        ReadRun(items, machine, StartupSource.MachineRun);
        if (machine32 is { } m32)
            ReadRun(items, m32, StartupSource.MachineRun32);
        ReadFolder(items, userFolder, StartupSource.UserFolder);
        ReadFolder(items, commonFolder, StartupSource.CommonFolder);
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Active ou désactive un élément. Lève une exception si le Registre refuse l'écriture.</summary>
    public void SetEnabled(StartupItem item, bool enabled)
    {
        var (hive, sub) = Approved(item.Source);
        using var key = hive.Root.CreateSubKey(hive.Prefix + ApprovedPath + sub, writable: true);
        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled)
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4); // date de désactivation, comme Windows
        key.SetValue(item.Entry, data, RegistryValueKind.Binary);
        Log.Info($"Démarrage : « {item.Name} » {(enabled ? "activé" : "désactivé")}");
    }

    (Hive Hive, string Sub) Approved(StartupSource source) => source switch
    {
        StartupSource.UserRun => (user, "Run"),
        StartupSource.MachineRun => (machine, "Run"),
        StartupSource.MachineRun32 => (machine, "Run32"),
        StartupSource.UserFolder => (user, "StartupFolder"),
        _ => (machine, "StartupFolder"),
    };

    bool IsEnabled(StartupSource source, string entry)
    {
        var (hive, sub) = Approved(source);
        try
        {
            using var key = hive.Root.OpenSubKey(hive.Prefix + ApprovedPath + sub);
            // Premier octet pair = activé (absent = activé). Windows écrit 02/06 pour activé, 03/07 pour désactivé.
            return key?.GetValue(entry) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
        }
        catch
        {
            return true;
        }
    }

    void ReadRun(List<StartupItem> items, Hive hive, StartupSource source)
    {
        try
        {
            using var key = hive.Root.OpenSubKey(hive.Prefix + RunPath);
            if (key == null)
                return;
            foreach (var name in key.GetValueNames())
            {
                if (name.Length == 0 || key.GetValue(name) is not string command || command.Trim().Length == 0)
                    continue;
                var path = ExecutableOf(command);
                items.Add(new StartupItem(name, name, command, source, IsEnabled(source, name), path, PublisherOf(path)));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Lecture des programmes au démarrage ({source}) : {ex.Message}");
        }
    }

    void ReadFolder(List<StartupItem> items, string? folder, StartupSource source)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var entry = System.IO.Path.GetFileName(file);
                if (entry.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;
                items.Add(new StartupItem(System.IO.Path.GetFileNameWithoutExtension(file), entry, file, source,
                    IsEnabled(source, entry), file, PublisherOf(file)));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Lecture du dossier Démarrage : {ex.Message}");
        }
    }

    /// <summary>Exécutable d'une ligne de commande : « "C:\a b\x.exe" -arg » ou « C:\a b\x.exe -arg ».</summary>
    public static string? ExecutableOf(string command)
    {
        var c = Environment.ExpandEnvironmentVariables(command.Trim());
        string candidate;
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            candidate = end > 0 ? c[1..end] : c[1..];
        }
        else
        {
            int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            candidate = exe > 0 ? c[..(exe + 4)] : c.Split(' ')[0];
        }
        return File.Exists(candidate) ? candidate : null;
    }

    static string? PublisherOf(string? path)
    {
        if (path == null || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var company = FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim();
            return string.IsNullOrEmpty(company) ? null : company;
        }
        catch
        {
            return null;
        }
    }
}
