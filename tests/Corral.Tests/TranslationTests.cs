using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Corral.Core;

namespace Corral.Tests;

/// <summary>
/// Catalogues de traduction (dossier translations) : fr.json (base Weblate, clé = valeur = texte français du code)
/// et en.json (clé = texte français, valeur = anglais). Ils sont générés depuis les appels Tr("…", "…") du code :
///   set CORRAL_UPDATE_TRANSLATIONS=1 &amp;&amp; dotnet test --filter TranslationTests
/// </summary>
public class TranslationTests
{
    static string Root([CallerFilePath] string path = "")
    {
        var dir = Path.GetDirectoryName(path)!;
        while (!File.Exists(Path.Combine(dir, "Corral.sln")))
            dir = Path.GetDirectoryName(dir)!;
        return dir;
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // accents et guillemets lisibles pour les traducteurs
    };

    [Fact]
    public void CatalogsMatchTheCode()
    {
        var root = Root();
        var strings = StringExtractor.FromFolder(Path.Combine(root, "src", "Corral"));
        Assert.True(strings.Count > 400, $"seulement {strings.Count} textes trouvés");

        var en = JsonSerializer.Serialize(strings, JsonOptions);
        var fr = JsonSerializer.Serialize(strings.ToDictionary(kv => kv.Key, kv => kv.Key), JsonOptions);
        var folder = Path.Combine(root, "translations");
        if (Environment.GetEnvironmentVariable("CORRAL_UPDATE_TRANSLATIONS") == "1")
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "en.json"), en + "\n");
            File.WriteAllText(Path.Combine(folder, "fr.json"), fr + "\n");
        }
        Assert.True(File.Exists(Path.Combine(folder, "en.json")) && File.ReadAllText(Path.Combine(folder, "en.json")).TrimEnd() == en
                    && File.ReadAllText(Path.Combine(folder, "fr.json")).TrimEnd() == fr,
            "Catalogues à régénérer : CORRAL_UPDATE_TRANSLATIONS=1 dotnet test --filter TranslationTests");

        // Toute autre langue fournie doit être un catalogue valide sur les mêmes clés
        foreach (var file in Directory.GetFiles(folder, "*.json").Where(f => Path.GetFileName(f) is not ("fr.json" or "en.json")))
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
            Assert.All(entries.Keys, k => Assert.True(strings.ContainsKey(k), $"{Path.GetFileName(file)} : clé inconnue « {k} »"));
            Catalog.Parse(File.ReadAllText(file));
        }
    }

    [Fact]
    public void ExtractorReadsLiteralsAndInterpolations()
    {
        var source = """
            var a = Tr("Fermer", "Close");
            var b = Tr($"Démarrage : « {item.Name} » {(on ? "activé" : "désactivé")}", $"Startup: “{item.Name}”");
            var c = Tr("Guillemet \" et\nsaut", "Quote");
            var d = Tr(dynamic, "ignoré");
            var e = Tr("concaténé " + x, "ignored");
            var f = Tr($"{{littéral}} {n:0} %", $"{n:0} %");
            var g = MyTr("pas un appel", "no");
            """;
        var found = StringExtractor.FromSource(source);
        Assert.Equal("Close", found["Fermer"]);
        Assert.Equal("Startup: “{item.Name}”", found["Démarrage : « {item.Name} » {(on ? \"activé\" : \"désactivé\")}"]);
        Assert.Equal("Quote", found["Guillemet \" et\nsaut"]);
        Assert.Equal("{n:0} %", found["{{littéral}} {n:0} %"]);
        Assert.Equal(4, found.Count);
    }

    [Fact]
    public void CatalogTranslatesExactAndTemplatedTexts()
    {
        var catalog = Catalog.Parse("""
            {
              "Fermer": "Schließen",
              "Démarrage : « {item.Name} » {(on ? \"activé\" : \"désactivé\")}": "Autostart: „{item.Name}“ {(on ? \"activé\" : \"désactivé\")}",
              "{n:0} % de {total}": "{total}: {n:0} %",
              "{{littéral}} {x}": "{{literal}} {x}",
              "Pas traduit": ""
            }
            """);
        Assert.Equal("Schließen", catalog.Translate("Fermer"));
        Assert.Equal("Autostart: „Steam“ activé", catalog.Translate("Démarrage : « Steam » activé"));
        Assert.Equal("CPU: 42 %", catalog.Translate("42 % de CPU"));
        Assert.Equal("{literal} {a}", catalog.Translate("{littéral} {a}"));
        Assert.Null(catalog.Translate("Pas traduit"));
        Assert.Null(catalog.Translate("Inconnu"));
    }

    [Fact]
    public void UnknownLanguageHasNoCatalog()
    {
        // Sans Lang.Set : la langue est globale et d'autres tests, en parallèle, vérifient des textes français
        Assert.Null(Catalog.Load("zz"));
        Assert.Null(Catalog.Load("../config")); // pas de chemin détourné
        Assert.False(Lang.IsKnown("zz"));
        Assert.True(Lang.IsKnown("en"));
        Assert.Equal(new[] { "fr", "en" }, Lang.Available().Take(2).Select(l => l.Code));
    }
}

/// <summary>Lit les appels Tr("français", "anglais") du code source (littéraux simples ou interpolés).</summary>
public static class StringExtractor
{
    public static SortedDictionary<string, string> FromFolder(string folder)
    {
        var all = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                     .Order(StringComparer.Ordinal))
            foreach (var (fr, en) in FromSource(File.ReadAllText(file)))
                all.TryAdd(fr, en);
        return all;
    }

    public static Dictionary<string, string> FromSource(string code)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        int i = 0;
        while ((i = code.IndexOf("Tr(", i, StringComparison.Ordinal)) >= 0)
        {
            int start = i;
            i += 3;
            if (start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] is '_' or '.'))
                continue;
            int p = i;
            if (ReadLiteral(code, ref p) is not { } fr || !Expect(code, ref p, ',') || ReadLiteral(code, ref p) is not { } en || !Expect(code, ref p, ')'))
                continue;
            found.TryAdd(fr, en);
            i = p;
        }
        return found;
    }

    static bool Expect(string code, ref int p, char c)
    {
        while (p < code.Length && char.IsWhiteSpace(code[p])) p++;
        if (p >= code.Length || code[p] != c)
            return false;
        p++;
        return true;
    }

    /// <summary>Texte d'un littéral "…" ou $"…" (séquences d'échappement décodées, trous {…} gardés tels quels).</summary>
    static string? ReadLiteral(string code, ref int p)
    {
        while (p < code.Length && char.IsWhiteSpace(code[p])) p++;
        bool interpolated = p < code.Length && code[p] == '$';
        if (interpolated) p++;
        if (p >= code.Length || code[p] != '"')
            return null;
        p++;
        var sb = new StringBuilder();
        while (p < code.Length)
        {
            char c = code[p++];
            if (c == '"')
                return sb.ToString();
            if (c == '\\' && p < code.Length)
            {
                char e = code[p++];
                sb.Append(e switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '0' => '\0', _ => e });
            }
            else if (interpolated && c == '{' && p < code.Length && code[p] != '{')
            {
                // Trou : jusqu'à l'accolade fermante de même niveau, en sautant les chaînes qu'il contient
                sb.Append('{');
                int depth = 1;
                while (p < code.Length && depth > 0)
                {
                    char h = code[p++];
                    if (h == '"')
                    {
                        sb.Append(h);
                        while (p < code.Length && code[p] != '"')
                        {
                            if (code[p] == '\\') sb.Append(code[p++]);
                            sb.Append(code[p++]);
                        }
                        if (p < code.Length) sb.Append(code[p++]);
                        continue;
                    }
                    if (h == '{') depth++;
                    else if (h == '}') depth--;
                    sb.Append(h);
                }
            }
            else if (interpolated && (c == '{' || c == '}') && p < code.Length && code[p] == c)
            {
                sb.Append(c).Append(c); // {{ ou }} : accolade littérale, gardée échappée dans la clé
                p++;
            }
            else
            {
                sb.Append(c);
            }
        }
        return null;
    }
}
