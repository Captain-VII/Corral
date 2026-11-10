using System.Diagnostics;
using Corral.Models;

namespace Corral.Core;

/// <summary>Libellés des priorités et effet d'une règle en clair, pour l'interface, la ligne de commande et le journal.</summary>
public static class RuleText
{
    public static string PriorityLabel(ProcessPriorityClass p) => p switch
    {
        ProcessPriorityClass.Idle => Tr("Inactive", "Idle"),
        ProcessPriorityClass.BelowNormal => Tr("Inférieure à la normale", "Below normal"),
        ProcessPriorityClass.Normal => Tr("Normale", "Normal"),
        ProcessPriorityClass.AboveNormal => Tr("Supérieure à la normale", "Above normal"),
        ProcessPriorityClass.High => Tr("Haute", "High"),
        _ => Tr("Temps réel", "Realtime"),
    };

    public static string IoLabel(IoPriorityLevel l) => l switch
    {
        IoPriorityLevel.VeryLow => Tr("Très basse", "Very low"),
        IoPriorityLevel.Low => Tr("Basse", "Low"),
        _ => Tr("Normale", "Normal"),
    };

    public static string MemoryLabel(MemoryPriorityLevel l) => l switch
    {
        MemoryPriorityLevel.VeryLow => Tr("Très basse", "Very low"),
        MemoryPriorityLevel.Low => Tr("Basse", "Low"),
        MemoryPriorityLevel.Medium => Tr("Moyenne", "Medium"),
        MemoryPriorityLevel.BelowNormal => Tr("Inférieure à la normale", "Below normal"),
        _ => Tr("Normale", "Normal"),
    };

    /// <summary>Effet d'une règle en une ligne, pour les infobulles.</summary>
    public static string? Summary(Rule? r)
    {
        if (r == null)
            return null;
        var parts = new List<string>();
        if (r.Priority is { } p) parts.Add(Tr("priorité ", "priority ") + PriorityLabel(p).ToLowerInvariant());
        if (r.AffinityMask is { } m) parts.Add(Tr($"{System.Numerics.BitOperations.PopCount((ulong)m)} cœurs", $"{System.Numerics.BitOperations.PopCount((ulong)m)} cores"));
        if (r.PowerPlan != null) parts.Add(Tr("plan d'alimentation", "power plan"));
        if (r.CpuLimitPercent is { } c) parts.Add($"CPU max {c} %");
        if (r.MemoryLimitMB is { } mem) parts.Add(Tr($"RAM max {mem} Mo", $"RAM max {mem} MB"));
        if (r.EfficiencyMode == true) parts.Add(Tr("mode efficacité", "efficiency mode"));
        if (r.EfficiencyMode == false) parts.Add(Tr("jamais en mode efficacité", "never in efficiency mode"));
        if (r.IoPriority is { } io) parts.Add(Tr("disque ", "disk ") + IoLabel(io).ToLowerInvariant());
        if (r.MemoryPriority is { } mp) parts.Add(Tr("mémoire ", "memory ") + MemoryLabel(mp).ToLowerInvariant());
        if (r.IsGame) parts.Add(Tr("jeu (Mode Jeu)", "game (Game Mode)"));
        if (r.GpuPreference is { } gpu) parts.Add(Tr("carte graphique ", "graphics card ") + GpuPreferences.Label(gpu).ToLowerInvariant());
        if (r.KeepAwake) parts.Add(Tr("empêche la veille", "prevents sleep"));
        if (r.Block == BlockMode.Always) parts.Add(Tr("bloqué", "blocked"));
        if (r.Block == BlockMode.SingleInstance) parts.Add(Tr("une seule instance", "single instance"));
        if (r.HasAlert) parts.Add(Tr("surveillé", "monitored"));
        if (r.When?.Describe() is { Length: > 0 } when) parts.Add(Tr("seulement ", "only ") + when);
        return parts.Count == 0 ? Tr("aucun effet", "no effect") : string.Join(" · ", parts);
    }
}
