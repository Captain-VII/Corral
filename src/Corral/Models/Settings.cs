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
    /// <summary>Mode efficacité Windows 11 (EcoQoS) : true l'impose, false l'interdit.</summary>
    public bool? EfficiencyMode { get; set; }
    public Corral.Core.IoPriorityLevel? IoPriority { get; set; }
    public Corral.Core.MemoryPriorityLevel? MemoryPriority { get; set; }
    /// <summary>Ce programme est un jeu : le Mode Jeu s'active tant qu'il tourne (si l'option est activée).</summary>
    public bool IsGame { get; set; }

    /// <summary>Le PC ne se met pas en veille tant que ce programme tourne.</summary>
    public bool KeepAwake { get; set; }
    public BlockMode Block { get; set; }

    /// <summary>Surveillance : seuil CPU (% du total) ; null = pas de surveillance CPU.</summary>
    public int? AlertCpuPercent { get; set; }
    /// <summary>Surveillance : seuil de mémoire privée en Mo ; null = pas de surveillance mémoire.</summary>
    public int? AlertMemoryMB { get; set; }
    /// <summary>Durée pendant laquelle le seuil doit être dépassé avant d'agir.</summary>
    public int AlertMinutes { get; set; } = 2;
    public AlertAction AlertAction { get; set; }

    public bool HasAlert => AlertCpuPercent is > 0 || AlertMemoryMB is > 0;
}

public enum BlockMode { None, Always, SingleInstance }

public enum AlertAction { Notify, Lower, Close }

/// <summary>
/// Raccourcis clavier globaux, stockés comme valeur entière de System.Windows.Forms.Keys
/// (touche + modificateurs) ; null = aucun.
/// </summary>
public sealed class HotkeySettings
{
    public int? GameMode { get; set; } = (int)(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.G);
    public int? Pause { get; set; } = (int)(System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.Alt | System.Windows.Forms.Keys.P);
    public int? ShowWindow { get; set; }
}

/// <summary>
/// Mode Jeu : plan d'alimentation performant, ProBalance réactif, et programmes de fond abaissés
/// (priorité basse + mode efficacité) le temps de jouer.
/// </summary>
public sealed class GameModeSettings
{
    /// <summary>S'active tout seul quand un programme marqué « jeu » tourne.</summary>
    public bool Automatic { get; set; } = true;
    /// <summary>Plan à activer ; Guid.Empty = ne pas changer ; null = pas encore choisi (Corral propose un plan performant).</summary>
    public Guid? PowerPlan { get; set; }
    public bool ReactiveProBalance { get; set; } = true;
    public bool LowerBackground { get; set; } = true;
    /// <summary>Bulle à l'activation et à la désactivation.</summary>
    public bool Notify { get; set; } = true;
    public List<string> BackgroundApps { get; set; } = new()
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe",
        "OneDrive.exe", "Dropbox.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe",
    };
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

    public sealed record Preset(string Name, double System, double Process, double Restore, int Trigger, int RestoreAfter);

    public static readonly Preset[] Presets =
    {
        new("Doux", 85, 10, 4, 5, 8),
        new("Équilibré", 75, 5, 2, 3, 5),
        new("Réactif", 60, 3, 1, 2, 5),
    };

    public static Preset Default => Presets[1];

    public void ApplyPreset(Preset p) =>
        (SystemThreshold, ProcessThreshold, RestoreThreshold, TriggerSeconds, RestoreSeconds) = (p.System, p.Process, p.Restore, p.Trigger, p.RestoreAfter);

    /// <summary>Préréglage correspondant exactement aux seuils actuels, ou null (« Personnalisé »).</summary>
    public Preset? CurrentPreset() =>
        Presets.FirstOrDefault(p => p.System == SystemThreshold && p.Process == ProcessThreshold && p.Restore == RestoreThreshold
                                    && p.Trigger == TriggerSeconds && p.RestoreAfter == RestoreSeconds);
}

/// <summary>Position et taille de la fenêtre principale, et dernière page ouverte.</summary>
public sealed class WindowSettings
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
    public string? LastPage { get; set; }

    public bool HasBounds => Width > 0 && Height > 0;
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
    /// <summary>Bulle quand ProBalance abaisse un programme.</summary>
    public bool NotifyProBalance { get; set; }
    public bool WelcomeDismissed { get; set; }
    /// <summary>Raccourci dans le menu Démarrer (pour la recherche Windows).</summary>
    public bool StartMenuShortcut { get; set; } = true;
    public WindowSettings Window { get; set; } = new();
    public GameModeSettings GameMode { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();

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
        Window ??= new();
        GameMode ??= new();
        Hotkeys ??= new();
        foreach (var r in Rules)
            r.AlertMinutes = Math.Clamp(r.AlertMinutes, 1, 120);
        GameMode.BackgroundApps ??= new();
        GameMode.BackgroundApps.RemoveAll(string.IsNullOrWhiteSpace);
        if (Window.Width < 0 || Window.Height < 0 || Window.Width > 20_000 || Window.Height > 20_000)
            Window = new() { LastPage = Window.LastPage };
    }
}
