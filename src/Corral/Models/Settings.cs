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

    /// <summary>Carte graphique préférée (null = ne pas toucher). Prend effet au prochain lancement.</summary>
    public Corral.Core.GpuPreference? GpuPreference { get; set; }

    /// <summary>Quand la règle s'applique (null = toujours).</summary>
    public RuleCondition? When { get; set; }
}

public enum PowerSource { Any, AC, Battery }

/// <summary>
/// Condition d'une règle : tous les critères renseignés doivent être remplis.
/// Quand le résultat change (passage sur batterie, début d'une plage horaire…), le moteur restaure et réapplique les règles.
/// </summary>
public sealed class RuleCondition
{
    public PowerSource Power { get; set; }
    /// <summary>Plage horaire, début inclus et fin exclue ; elle peut passer minuit (22:00 → 06:00). Null = toute la journée.</summary>
    public TimeOnly? From { get; set; }
    public TimeOnly? To { get; set; }
    /// <summary>Jours où la règle s'applique (null ou vide = tous).</summary>
    public List<DayOfWeek>? Days { get; set; }

    bool HasTime => From is { } f && To is { } t && f != t;
    bool HasDays => Days is { Count: > 0 and < 7 };

    /// <summary>La condition ne restreint rien.</summary>
    public bool IsAlways => Power == PowerSource.Any && !HasTime && !HasDays;

    public bool IsMet(DateTime local, bool onBattery)
    {
        if (Power == PowerSource.AC && onBattery || Power == PowerSource.Battery && !onBattery)
            return false;
        if (HasDays && !Days!.Contains(local.DayOfWeek))
            return false;
        if (HasTime)
        {
            var now = TimeOnly.FromDateTime(local);
            var (from, to) = (From!.Value, To!.Value);
            if (from < to ? now < from || now >= to : now < from && now >= to)
                return false;
        }
        return true;
    }

    /// <summary>Condition sans effet → null ; jours en double ou hors plage retirés.</summary>
    public static RuleCondition? Normalize(RuleCondition? c)
    {
        if (c == null)
            return null;
        if (!Enum.IsDefined(c.Power))
            c.Power = PowerSource.Any;
        c.Days = c.Days?.Where(Enum.IsDefined).Distinct().Order().ToList();
        if (!c.HasDays)
            c.Days = null;
        if (!c.HasTime)
            (c.From, c.To) = (null, null);
        return c.IsAlways ? null : c;
    }

    /// <summary>« sur batterie · 09:00-18:00 · lun., mar. »</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Power == PowerSource.AC) parts.Add(Tr("sur secteur", "on AC power"));
        if (Power == PowerSource.Battery) parts.Add(Tr("sur batterie", "on battery"));
        if (HasTime) parts.Add($"{From:HH:mm}-{To:HH:mm}");
        if (HasDays)
        {
            var culture = System.Globalization.CultureInfo.GetCultureInfo(Corral.Core.Lang.English ? "en-US" : "fr-FR");
            parts.Add(string.Join(", ", Days!.Select(d => culture.DateTimeFormat.GetAbbreviatedDayName(d))));
        }
        return string.Join(" · ", parts);
    }
}

/// <summary>La fenêtre au premier plan passe en priorité supérieure le temps qu'on l'utilise.</summary>
public sealed class ForegroundBoostSettings
{
    public bool Enabled { get; set; }
}

/// <summary>Plan Économie d'énergie après N minutes sans clavier ni souris.</summary>
public sealed class IdleSaverSettings
{
    public bool Enabled { get; set; }
    public int Minutes { get; set; } = 10;
    /// <summary>Plan à activer pendant l'absence (Économie d'énergie de Windows par défaut).</summary>
    public Guid Plan { get; set; } = Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");
}

/// <summary>Nettoyage automatique quand la mémoire utilisée dépasse un seuil.</summary>
public sealed class MemoryCleanupSettings
{
    public bool Enabled { get; set; }
    public int ThresholdPercent { get; set; } = 85;
    public bool PurgeStandby { get; set; } = true;
    public bool TrimIdle { get; set; } = true;
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
    /// <summary>Serveur RDS / VDI multi-session : une session qui prend plus que sa part est abaissée (voir Core.SessionFairnessLogic).</summary>
    public bool SessionFairness { get; set; }

    public sealed record Preset(string Name, double System, double Process, double Restore, int Trigger, int RestoreAfter);

    public static readonly Preset[] Presets =
    {
        new(Tr("Doux", "Gentle"), 85, 10, 4, 5, 8),
        new(Tr("Équilibré", "Balanced"), 75, 5, 2, 3, 5),
        new(Tr("Réactif", "Responsive"), 60, 3, 1, 2, 5),
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

/// <summary>Jeu de règles nommé (voir Core.Profiles).</summary>
public sealed class Profile
{
    public string Name { get; set; } = "";
    public List<Rule> Rules { get; set; } = new();
}

/// <summary>Mini-fenêtre toujours visible (CPU et mémoire).</summary>
public sealed class OverlaySettings
{
    public bool Enabled { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
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
    public ForegroundBoostSettings ForegroundBoost { get; set; } = new();
    public IdleSaverSettings IdleSaver { get; set; } = new();
    public MemoryCleanupSettings MemoryCleanup { get; set; } = new();
    /// <summary>Langue de l'interface : « auto » (celle de Windows), « fr » ou « en ».</summary>
    public string Language { get; set; } = "auto";
    /// <summary>L'icône de notification affiche la charge du processeur au lieu du logo.</summary>
    public bool TrayCpuIcon { get; set; }
    public OverlaySettings Overlay { get; set; } = new();
    /// <summary>Nom du profil dont les règles sont dans <see cref="Rules"/>.</summary>
    public string ActiveProfile { get; set; } = "";
    public List<Profile> Profiles { get; set; } = new();
    /// <summary>Pages imposées par la configuration de parc (voir Core.Fleet), en lecture seule dans l'interface.</summary>
    public List<string>? Managed { get; set; }

    /// <summary>Corrige les valeurs nulles ou hors bornes après une lecture JSON.</summary>
    public void Normalize()
    {
        Rules ??= new();
        Rules.RemoveAll(r => r is null);
        foreach (var r in Rules)
        {
            r.Pattern ??= "";
            r.When = RuleCondition.Normalize(r.When);
        }
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
        ForegroundBoost ??= new();
        IdleSaver ??= new();
        IdleSaver.Minutes = Math.Clamp(IdleSaver.Minutes, 1, 240);
        MemoryCleanup ??= new();
        MemoryCleanup.ThresholdPercent = Math.Clamp(MemoryCleanup.ThresholdPercent, 50, 98);
        foreach (var r in Rules)
            r.AlertMinutes = Math.Clamp(r.AlertMinutes, 1, 120);
        GameMode.BackgroundApps ??= new();
        GameMode.BackgroundApps.RemoveAll(string.IsNullOrWhiteSpace);
        if (Language != "auto" && !Corral.Core.Lang.IsKnown(Language ?? ""))
            Language = "auto";
        Overlay ??= new();
        ActiveProfile ??= "";
        Profiles ??= new();
        Profiles.RemoveAll(p => p is null || string.IsNullOrWhiteSpace(p.Name));
        Profiles = Profiles.GroupBy(p => p.Name.Trim(), StringComparer.CurrentCultureIgnoreCase).Select(g => g.First()).ToList();
        foreach (var p in Profiles)
        {
            p.Name = p.Name.Trim();
            p.Rules ??= new();
            p.Rules.RemoveAll(r => r is null);
            foreach (var r in p.Rules)
                r.Pattern ??= "";
        }
        if (Window.Width < 0 || Window.Height < 0 || Window.Width > 20_000 || Window.Height > 20_000)
            Window = new() { LastPage = Window.LastPage };
    }
}
