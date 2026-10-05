using System.Diagnostics;

namespace Corral.Models;

/// <summary>Règle appliquée aux processus dont le nom correspond à <see cref="Pattern"/>. Un champ nul n'est pas appliqué.</summary>
public sealed class Rule
{
    public bool Enabled { get; set; } = true;
    public string Pattern { get; set; } = "";
    public ProcessPriorityClass? Priority { get; set; }
    public long? AffinityMask { get; set; }
    public Guid? PowerPlan { get; set; }
    /// <summary>Plafond en % du CPU total (1-99).</summary>
    public int? CpuLimitPercent { get; set; }
    public int? MemoryLimitMB { get; set; }
}

public sealed class ProBalanceSettings
{
    public bool Enabled { get; set; } = true;
    public double SystemThreshold { get; set; } = 75;
    public double ProcessThreshold { get; set; } = 5;
    public double RestoreThreshold { get; set; } = 2;
    public int TriggerSeconds { get; set; } = 3;
    public int RestoreSeconds { get; set; } = 5;
    public List<string> Exclusions { get; set; } = new() { "svchost.exe", "audiodg.exe", "explorer.exe" };
}

public enum ThemeMode { System, Light, Dark }

public sealed class Settings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public bool CheckUpdates { get; set; } = true;
    /// <summary>Version que l'utilisateur a choisi d'ignorer (« 1.2.0 »).</summary>
    public string? SkippedVersion { get; set; }
    public int PollIntervalMs { get; set; } = 1000;
    public List<Rule> Rules { get; set; } = new();
    public ProBalanceSettings ProBalance { get; set; } = new();

    /// <summary>Corrige les valeurs nulles ou hors bornes après une lecture JSON.</summary>
    public void Normalize()
    {
        Rules ??= new();
        Rules.RemoveAll(r => r is null);
        foreach (var r in Rules)
            r.Pattern ??= "";
        ProBalance ??= new();
        ProBalance.Exclusions ??= new();
        ProBalance.Exclusions.RemoveAll(string.IsNullOrWhiteSpace);
        PollIntervalMs = Math.Clamp(PollIntervalMs, 250, 10_000);
        ProBalance.SystemThreshold = Math.Clamp(ProBalance.SystemThreshold, 10, 100);
        ProBalance.ProcessThreshold = Math.Clamp(ProBalance.ProcessThreshold, 1, 100);
        ProBalance.RestoreThreshold = Math.Clamp(ProBalance.RestoreThreshold, 0, 100);
        ProBalance.TriggerSeconds = Math.Clamp(ProBalance.TriggerSeconds, 1, 60);
        ProBalance.RestoreSeconds = Math.Clamp(ProBalance.RestoreSeconds, 1, 60);
    }
}
