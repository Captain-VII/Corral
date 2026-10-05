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

    public static Settings Clone(Settings settings)
    {
        var s = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings, Options), Options)!;
        s.Normalize();
        return s;
    }
}
