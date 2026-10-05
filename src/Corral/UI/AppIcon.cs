namespace Corral.UI;

/// <summary>Icône embarquée (tools/make-icon.ps1 génère corral.ico), à la bonne taille pour chaque usage.</summary>
public static class AppIcon
{
    public static Icon Load(Size size)
    {
        try
        {
            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Corral.corral.ico");
            if (stream != null)
                return new Icon(stream, size);
        }
        catch { }
        return SystemIcons.Application;
    }
}
