using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Corral.Core;

/// <summary>Inactivité de l'utilisateur et demandes d'affichage en cours (vidéo, présentation…).</summary>
public static class Idle
{
    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint Size;
        public uint Time;
    }

    /// <summary>Temps écoulé depuis la dernière action clavier ou souris.</summary>
    public static TimeSpan UserIdleTime()
    {
        var info = new LASTINPUTINFO { Size = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }

    const int SystemExecutionState = 16;
    const uint ES_DISPLAY_REQUIRED = 0x2;

    /// <summary>Un programme demande que l'écran reste allumé (lecture vidéo, présentation…).</summary>
    public static bool DisplayRequired()
    {
        return CallNtPowerInformation(SystemExecutionState, IntPtr.Zero, 0, out uint state, sizeof(uint)) == 0
               && (state & ES_DISPLAY_REQUIRED) != 0;
    }

    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("powrprof.dll")]
    static extern int CallNtPowerInformation(int level, IntPtr input, int inputSize, out uint output, int outputSize);
}

/// <summary>
/// Nettoyage mémoire : vider la liste de veille (cache de fichiers non utilisé, droits administrateur requis)
/// et alléger l'espace de travail des programmes inactifs (leurs pages repartent sur disque si besoin).
/// </summary>
public static class MemoryCleaner
{
    const int SystemMemoryListInformation = 80;
    const int MemoryPurgeStandbyList = 4;

    /// <summary>Vide la liste de veille. Lève <see cref="Win32Exception"/> sans droits suffisants.</summary>
    public static void PurgeStandbyList()
    {
        EnablePrivilege("SeProfileSingleProcessPrivilege");
        int command = MemoryPurgeStandbyList;
        int status = NtSetSystemInformation(SystemMemoryListInformation, ref command, sizeof(int));
        if (status != 0)
            throw new Win32Exception(RtlNtStatusToDosError(status));
    }

    /// <summary>Retire de la RAM les pages non utilisées d'un processus (elles reviennent à la demande).</summary>
    public static void TrimWorkingSet(Process process)
    {
        if (!EmptyWorkingSet(process.Handle))
            throw new Win32Exception();
    }

    static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            throw new Win32Exception();
        try
        {
            if (!LookupPrivilegeValue(null, name, out long luid))
                throw new Win32Exception();
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Privilège indisponible (Corral doit tourner en administrateur)");
        }
        finally
        {
            CloseHandle(token);
        }
    }

    const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TOKEN_PRIVILEGES
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, int length, IntPtr previous, IntPtr returned);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

    [DllImport("ntdll.dll")]
    static extern int RtlNtStatusToDosError(int status);

    [DllImport("psapi.dll", SetLastError = true)]
    static extern bool EmptyWorkingSet(IntPtr process);
}

public enum GpuPreference { Default = 0, PowerSaving = 1, HighPerformance = 2 }

/// <summary>
/// Carte graphique préférée par programme, comme dans Paramètres > Affichage > Graphiques
/// (HKCU\Software\Microsoft\DirectX\UserGpuPreferences, une valeur par chemin d'exe).
/// Corral note les chemins qu'il a réglés pour pouvoir les retirer si la règle disparaît.
/// Prend effet au prochain lancement du programme.
/// </summary>
public sealed class GpuPreferences
{
    const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    readonly string? managedFile;
    readonly string keyPath;
    readonly HashSet<string> managed;

    /// <param name="keyPath">Clé de registre (les tests utilisent une clé à part).</param>
    public GpuPreferences(string? managedFile, string keyPath = KeyPath)
    {
        this.managedFile = managedFile;
        this.keyPath = keyPath;
        managed = LoadManaged();
    }

    public IReadOnlyCollection<string> Managed => managed;

    public static string Label(GpuPreference p) => p switch
    {
        GpuPreference.HighPerformance => "Haute performance",
        GpuPreference.PowerSaving => "Économie d'énergie",
        _ => "Laisser Windows décider",
    };

    public GpuPreference? Get(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        if (key?.GetValue(exePath) is not string value)
            return null;
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=');
            if (kv.Length == 2 && kv[0] == "GpuPreference" && int.TryParse(kv[1], out int n) && n is >= 0 and <= 2)
                return (GpuPreference)n;
        }
        return null;
    }

    /// <summary>Règle la préférence (si elle diffère) et note le chemin comme géré par Corral.</summary>
    public bool Set(string exePath, GpuPreference preference)
    {
        if (Get(exePath) == preference && managed.Contains(exePath))
            return false;
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(exePath, $"GpuPreference={(int)preference};");
        managed.Add(exePath);
        SaveManaged();
        return true;
    }

    /// <summary>Retire les préférences posées par Corral pour les chemins qui ne sont plus visés.</summary>
    public List<string> RemoveExcept(IEnumerable<string> stillWanted)
    {
        var keep = new HashSet<string>(stillWanted, StringComparer.OrdinalIgnoreCase);
        var removed = managed.Where(p => !keep.Contains(p)).ToList();
        if (removed.Count == 0)
            return removed;
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
        {
            foreach (var path in removed)
                key?.DeleteValue(path, throwOnMissingValue: false);
        }
        managed.ExceptWith(removed);
        SaveManaged();
        return removed;
    }

    HashSet<string> LoadManaged()
    {
        try
        {
            if (managedFile != null && File.Exists(managedFile))
                return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(managedFile)) ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Error("Lecture des préférences GPU gérées", ex);
        }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    void SaveManaged()
    {
        if (managedFile == null)
            return;
        try { File.WriteAllText(managedFile, JsonSerializer.Serialize(managed.ToList())); }
        catch (Exception ex) { Log.Error("Enregistrement des préférences GPU gérées", ex); }
    }
}
