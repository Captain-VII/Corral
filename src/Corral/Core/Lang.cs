using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Corral.Core;

/// <summary>
/// Langue de l'interface. Chaque texte est écrit en français et en anglais là où il est utilisé : Tr("Fermer", "Close").
/// Les autres langues viennent de catalogues JSON (dossier translations du dépôt, traduits sur Weblate) :
/// clé = texte français tel qu'écrit dans le code, valeur = traduction. Un texte absent du catalogue s'affiche en anglais.
/// </summary>
public static class Lang
{
    /// <summary>Code de la langue en cours : « fr », « en », ou celui d'un catalogue (« de », « es »…).</summary>
    public static string Code { get; private set; } = "fr";

    /// <summary>Vrai dès que l'interface n'est pas en français (l'anglais sert de repli et fixe les formats).</summary>
    public static bool English => Code != "fr";

    static Catalog? catalog;

    /// <param name="setting">« auto » (langue de Windows), « fr », « en » ou le code d'un catalogue.</param>
    public static void Set(string? setting)
    {
        var code = setting is null or "auto" ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : setting;
        catalog = code is "fr" or "en" ? null : Catalog.Load(code);
        Code = code == "fr" || code == "en" || catalog != null ? code : "en";
    }

    public static string Tr(string fr, string en) => Code switch
    {
        "fr" => fr,
        "en" => en,
        _ => catalog?.Translate(fr) ?? en,
    };

    /// <summary>Langues proposées : français, anglais, puis les catalogues intégrés ou déposés à côté de l'exe.</summary>
    public static IReadOnlyList<(string Code, string Name)> Available()
    {
        var list = new List<(string, string)> { ("fr", "Français"), ("en", "English") };
        foreach (var code in Catalog.Codes().Where(c => c is not ("fr" or "en")).Order())
        {
            string name;
            try { name = CultureInfo.GetCultureInfo(code).NativeName; }
            catch (CultureNotFoundException) { name = code; }
            list.Add((code, name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : code));
        }
        return list;
    }

    public static bool IsKnown(string code) => code is "fr" or "en" || Catalog.Codes().Contains(code);
}

/// <summary>Traductions d'une langue. Les textes à trous (« Démarrage : « {item.Name} » ») sont reconnus après coup.</summary>
public sealed class Catalog
{
    const string ResourcePrefix = "Corral.Translations.";
    const int MaxCache = 5000;

    readonly Dictionary<string, string> exact = new();
    readonly List<(Regex Pattern, string[] Holes, string Translation)> templates = new();
    readonly Dictionary<string, string?> cache = new();
    readonly object sync = new();

    /// <summary>Dossiers où déposer un catalogue (lang\xx.json) sans recompiler : à côté de l'exe, puis dans le profil.</summary>
    static IEnumerable<string> Folders()
    {
        if (AppContext.BaseDirectory is { Length: > 0 } exe)
            yield return Path.Combine(exe, "lang");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Corral", "lang");
    }

    public static IEnumerable<string> Codes()
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            if (name.StartsWith(ResourcePrefix) && name.EndsWith(".json"))
                codes.Add(name[ResourcePrefix.Length..^5]);
        foreach (var folder in Folders())
        {
            try
            {
                if (Directory.Exists(folder))
                    foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
                        codes.Add(Path.GetFileNameWithoutExtension(file));
            }
            catch { }
        }
        return codes;
    }

    /// <summary>Catalogue de la langue, ou null s'il n'existe pas ou est illisible.</summary>
    public static Catalog? Load(string code)
    {
        if (!Regex.IsMatch(code, @"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})?$"))
            return null;
        try
        {
            foreach (var folder in Folders())
            {
                var file = Path.Combine(folder, code + ".json");
                if (File.Exists(file))
                    return Parse(File.ReadAllText(file));
            }
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + code + ".json");
            if (stream != null)
                return Parse(new StreamReader(stream, Encoding.UTF8).ReadToEnd());
        }
        catch (Exception ex)
        {
            Log.Warn($"Catalogue de langue « {code} » illisible : {ex.Message}");
        }
        return null;
    }

    public static Catalog Parse(string json)
    {
        var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        var catalog = new Catalog();
        foreach (var (source, translation) in entries)
        {
            if (string.IsNullOrEmpty(translation))
                continue; // pas encore traduit : l'anglais s'affichera
            var holes = Holes(source);
            if (holes.Count == 0)
                catalog.exact[source] = translation;
            else
                catalog.templates.Add((TemplatePattern(source), holes.Select(h => h.Value).ToArray(), translation));
        }
        return catalog;
    }

    /// <summary>Trous « {expression} » d'un texte (« {{ » et « }} » sont des accolades littérales).</summary>
    static List<Match> Holes(string text) => Regex.Matches(text, @"(?<!\{)\{(?!\{)[^{}]+\}(?!\})").ToList();

    static Regex TemplatePattern(string source)
    {
        var sb = new StringBuilder("^");
        int last = 0;
        foreach (var hole in Holes(source))
        {
            sb.Append(Regex.Escape(Unescape(source[last..hole.Index]))).Append("(.*?)");
            last = hole.Index + hole.Length;
        }
        sb.Append(Regex.Escape(Unescape(source[last..]))).Append('$');
        return new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    static string Unescape(string literal) => literal.Replace("{{", "{").Replace("}}", "}");

    /// <summary>Traduction du texte français (déjà formaté), ou null.</summary>
    public string? Translate(string fr)
    {
        if (exact.TryGetValue(fr, out var direct))
            return direct;
        lock (sync)
        {
            if (cache.TryGetValue(fr, out var cached))
                return cached;
            string? result = null;
            foreach (var (pattern, holes, translation) in templates)
            {
                var m = pattern.Match(fr);
                if (!m.Success)
                    continue;
                result = Unescape(Regex.Replace(translation, @"(?<!\{)\{(?!\{)[^{}]+\}(?!\})", h =>
                {
                    int i = Array.IndexOf(holes, h.Value);
                    return i >= 0 ? m.Groups[i + 1].Value.Replace("{", "{{").Replace("}", "}}") : h.Value;
                }));
                break;
            }
            if (cache.Count >= MaxCache)
                cache.Clear(); // textes à trous très variés (noms de processus) : on repart de zéro plutôt que de grossir
            cache[fr] = result;
            return result;
        }
    }
}
