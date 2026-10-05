using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Corral.Core;

/// <summary>
/// Raccourci « Corral » dans le menu Démarrer de l'utilisateur, pour que la recherche Windows le trouve.
/// Un exe portable n'en a pas ; on le crée au lancement et on le recrée si l'exe a été déplacé.
/// Passe par l'interface COM IShellLink (typée, sans « dynamic », fiable dans un exe en fichier unique).
/// </summary>
public static class StartMenu
{
    public static string DefaultShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Corral.lnk");

    /// <summary>Crée, corrige ou supprime le raccourci selon <paramref name="enabled"/>. Ne lève pas d'exception.</summary>
    public static void Sync(bool enabled, string exePath, string? shortcutPath = null)
    {
        shortcutPath ??= DefaultShortcutPath;
        try
        {
            if (!enabled)
            {
                if (File.Exists(shortcutPath))
                {
                    File.Delete(shortcutPath);
                    Log.Info("Raccourci du menu Démarrer supprimé");
                }
                return;
            }
            if (File.Exists(shortcutPath) && string.Equals(ReadTarget(shortcutPath), exePath, StringComparison.OrdinalIgnoreCase))
                return;
            Create(shortcutPath, exePath);
            Log.Info($"Raccourci du menu Démarrer créé : {exePath}");
        }
        catch (Exception ex)
        {
            Log.Error("Raccourci du menu Démarrer", ex);
        }
    }

    public static string? ReadTarget(string shortcutPath)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(shortcutPath, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.ToString();
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    static void Create(string shortcutPath, string exePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath)!);
            link.SetDescription("Corral - gestion des processus");
            link.SetIconLocation(exePath, 0);
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink
    {
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relPath, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
