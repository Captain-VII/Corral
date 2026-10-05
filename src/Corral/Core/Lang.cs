using System.Globalization;

namespace Corral.Core;

/// <summary>
/// Langue de l'interface : français ou anglais, choisie au démarrage (langue de Windows par défaut).
/// Chaque texte est écrit dans les deux langues là où il est utilisé : Tr("Fermer", "Close").
/// </summary>
public static class Lang
{
    public static bool English { get; private set; }

    /// <param name="setting">« auto » (langue de Windows), « fr » ou « en ».</param>
    public static void Set(string? setting) => English = setting switch
    {
        "en" => true,
        "fr" => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "fr",
    };

    public static string Tr(string fr, string en) => English ? en : fr;
}
