using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Corral.Core;

public sealed record PowerPlanInfo(Guid Id, string Name, bool Active);

public interface IPowerPlanApi
{
    Guid? GetActive();
    bool SetActive(Guid plan);
}

/// <summary>Accès aux plans d'alimentation via powercfg.exe.</summary>
public sealed class PowerCfg : IPowerPlanApi
{
    static readonly Regex GuidRegex = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
    static readonly Regex ListLine = new(@"(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\((?<name>.*)\)\s*(?<active>\*)?\s*$");

    static PowerCfg() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static IReadOnlyList<PowerPlanInfo> List() => ParseList(Run("/list") ?? "");

    /// <summary>Plans standard de Windows orientés performance, par ordre de préférence.</summary>
    public static readonly Guid[] PerformancePlans =
    {
        Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61"), // Performances optimales
        Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), // Performances élevées
    };

    public Guid? GetActive()
    {
        var output = Run("/getactivescheme");
        var m = output == null ? null : GuidRegex.Match(output);
        return m is { Success: true } ? Guid.Parse(m.Value) : null;
    }

    public bool SetActive(Guid plan) => Run($"/setactive {plan}") != null;

    public static IReadOnlyList<PowerPlanInfo> ParseList(string text)
    {
        var result = new List<PowerPlanInfo>();
        foreach (var line in text.Split('\n'))
        {
            var m = ListLine.Match(line.TrimEnd('\r'));
            if (!m.Success)
                continue;
            var id = Guid.Parse(m.Groups["id"].Value);
            if (result.Any(p => p.Id == id))
                continue; // powercfg liste parfois un plan en double
            result.Add(new PowerPlanInfo(id, m.Groups["name"].Value.Trim(), m.Groups["active"].Success));
        }
        return result;
    }

    static string? Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = OemEncoding(),
            };
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(); } catch { }
                Log.Warn($"powercfg {args} : délai dépassé");
                return null;
            }
            if (p.ExitCode != 0)
            {
                Log.Warn($"powercfg {args} : code {p.ExitCode} {output.Result.Trim()}");
                return null;
            }
            return output.Result;
        }
        catch (Exception ex)
        {
            Log.Error($"powercfg {args}", ex);
            return null;
        }
    }

    static Encoding OemEncoding()
    {
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
        catch { return Encoding.Default; }
    }
}

/// <summary>
/// Active le plan demandé tant qu'au moins un processus concerné tourne, puis restaure le plan d'origine.
/// Si plusieurs processus demandent des plans différents, le dernier démarré l'emporte.
/// Le plan d'origine est noté dans un fichier pour être restauré après un plantage.
/// </summary>
public sealed class PowerPlanManager
{
    readonly IPowerPlanApi api;
    readonly string? stateFile;
    readonly List<(ProcKey Key, Guid Plan)> requests = new();
    Guid? original;
    Guid? applied;

    public PowerPlanManager(IPowerPlanApi api, string? stateFile)
    {
        this.api = api;
        this.stateFile = stateFile;
    }

    public void OnStart(ProcKey key, Guid plan)
    {
        if (requests.Count == 0)
        {
            original = api.GetActive();
            if (original == null)
            {
                Log.Warn("Plan d'alimentation actif introuvable, changement ignoré", LogCategory.Power);
                return;
            }
            WriteState(original.Value);
        }
        requests.Add((key, plan));
        Apply(plan);
    }

    public void OnExit(ProcKey key)
    {
        if (requests.RemoveAll(r => r.Key == key) == 0)
            return;
        if (requests.Count > 0)
            Apply(requests[^1].Plan);
        else
            RestoreAll();
    }

    public void RestoreAll()
    {
        requests.Clear();
        if (original is { } o && applied != null && applied != o)
        {
            if (api.SetActive(o))
                Log.Info($"Plan d'alimentation restauré : {o}", LogCategory.Power);
        }
        original = null;
        applied = null;
        DeleteState();
    }

    /// <summary>À appeler au démarrage : restaure le plan laissé par une session précédente qui a planté.</summary>
    public static void RecoverFromCrash(IPowerPlanApi api, string stateFile)
    {
        try
        {
            if (!File.Exists(stateFile))
                return;
            if (Guid.TryParse(File.ReadAllText(stateFile).Trim(), out var plan) && api.SetActive(plan))
                Log.Info($"Plan d'alimentation d'origine restauré après un arrêt inattendu : {plan}", LogCategory.Power);
            File.Delete(stateFile);
        }
        catch (Exception ex)
        {
            Log.Error("Restauration du plan d'alimentation", ex);
        }
    }

    void Apply(Guid plan)
    {
        if (applied == plan)
            return;
        if (api.SetActive(plan))
        {
            applied = plan;
            Log.Info($"Plan d'alimentation activé : {plan}", LogCategory.Power);
        }
    }

    void WriteState(Guid plan)
    {
        if (stateFile == null) return;
        try { File.WriteAllText(stateFile, plan.ToString()); } catch (Exception ex) { Log.Error("Écriture état plan", ex); }
    }

    void DeleteState()
    {
        if (stateFile == null) return;
        try { File.Delete(stateFile); } catch { }
    }
}
