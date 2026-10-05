using System.Text.Json;
using System.Text.Json.Serialization;
using Corral.Models;

namespace Corral.Core;

public sealed class RuleStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public RuleStore(string filePath) => FilePath = filePath;

    public string FilePath { get; }
    public string ConfigDirectory => Path.GetDirectoryName(FilePath)!;

    /// <summary>Charge la config. Fichier absent → défaut ; fichier corrompu → copie .bak puis défaut.</summary>
    public Settings Load()
    {
        if (!File.Exists(FilePath))
            return new Settings();
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options)
                    ?? throw new JsonException("fichier vide");
            s.Normalize();
            return s;
        }
        catch (Exception ex)
        {
            var bak = $"{FilePath}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            try { File.Copy(FilePath, bak, overwrite: true); } catch { }
            Log.Error($"Configuration illisible, copie dans {bak}, configuration par défaut utilisée", ex);
            return new Settings();
        }
    }

    /// <summary>Écriture atomique (fichier temporaire puis remplacement).</summary>
    public void Save(Settings settings)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }

    sealed class RulesFile
    {
        public string App { get; set; } = "Corral";
        public List<Rule>? Rules { get; set; } // nul si le fichier n'a pas de liste « Rules »
    }

    /// <summary>Export des seules règles (pour sauvegarder ou partager).</summary>
    public static void ExportRules(IEnumerable<Rule> rules, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new RulesFile { Rules = rules.ToList() }, Options));

    /// <summary>
    /// Lit un fichier exporté. Lève <see cref="InvalidDataException"/> si le fichier n'est pas un export de règles.
    /// Les règles sans nom sont écartées et les masques d'affinité invalides retirés.
    /// </summary>
    public static List<Rule> ImportRules(string path)
    {
        RulesFile? file;
        try
        {
            file = JsonSerializer.Deserialize<RulesFile>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(Tr("Ce fichier n'est pas un export de règles Corral.", "This file is not a Corral rules export."), ex);
        }
        if (file?.Rules == null)
            throw new InvalidDataException(Tr("Ce fichier ne contient aucune règle.", "This file contains no rules."));
        var rules = file.Rules.Where(r => r != null && !string.IsNullOrWhiteSpace(r.Pattern)).ToList();
        foreach (var r in rules)
        {
            r.Pattern = r.Pattern.Trim();
            if (r.AffinityMask is { } m && !RuleMatcher.IsValidAffinity(m, Environment.ProcessorCount))
                r.AffinityMask = null;
            if (r.CpuLimitPercent is < 1 or > 99) r.CpuLimitPercent = null;
            if (r.MemoryLimitMB is < 1) r.MemoryLimitMB = null;
        }
        return rules;
    }

    public static Settings Clone(Settings settings)
    {
        var s = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings, Options), Options)!;
        s.Normalize();
        return s;
    }
}
