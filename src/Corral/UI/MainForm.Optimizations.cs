using Corral.Core;
using Corral.Models;

namespace Corral.UI;

// Page « Optimisations » : boost du premier plan, économie au repos, nettoyage mémoire.
public sealed partial class MainForm
{
    readonly ToggleSwitch optBoost = new();
    readonly ToggleSwitch optIdle = new();
    readonly NumericUpDown optIdleMinutes = Num(1, 240);
    readonly ComboBox optIdlePlan = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    readonly List<Guid> optIdlePlanIds = new();
    readonly ToggleSwitch optMem = new();
    readonly NumericUpDown optMemThreshold = Num(50, 98);
    readonly ToggleSwitch optMemPurge = new();
    readonly ToggleSwitch optMemTrim = new();
    readonly Label optMemNow = new() { AutoSize = true, Tag = Theme.HintTag, Margin = new Padding(0, 6, 0, 0) };

    Control BuildOptimizationPage()
    {
        var cleanNow = new ModernButton(Tr("Nettoyer maintenant", "Clean now"));
        cleanNow.Click += (_, _) =>
        {
            engine.CleanMemoryNow();
            cleanNow.Enabled = false; // le résultat arrive en bulle ; on évite les clics répétés
            var t = new System.Windows.Forms.Timer { Interval = 5000 };
            t.Tick += (_, _) => { cleanNow.Enabled = true; t.Dispose(); };
            t.Start();
        };
        tips.SetToolTip(cleanNow, Tr("Vide le cache mémoire inutilisé et allège les programmes inactifs, tout de suite", "Empties the unused memory cache and trims idle programs, right now"));

        var stack = new CardStack();
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Boost du premier plan", "Foreground boost"),
                Tr("La fenêtre que vous utilisez passe en priorité « supérieure à la normale », et retrouve sa priorité quand vous changez de fenêtre. " +
                   "Sans effet sur les programmes dont une règle fixe la priorité.",
                   "The window you are using gets “above normal” priority, and gets its priority back when you switch windows. " +
                   "No effect on programs whose priority is set by a rule."), optBoost)), Tr("Premier plan", "Foreground")));
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Économie au repos", "Idle saver"),
                Tr("Après un moment sans clavier ni souris, passe sur un plan économique ; le plan habituel revient dès votre retour. " +
                   "Jamais pendant le Mode Jeu, une vidéo, ou quand une règle impose un plan.",
                   "After a while without keyboard or mouse, switches to a power-saving plan; the usual plan comes back as soon as you return. " +
                   "Never during Game Mode, a video, or when a rule sets a plan."), optIdle),
            SettingRow(Tr("Après", "After"), null, WithUnit(optIdleMinutes, Tr("minutes d'inactivité", "idle minutes"))),
            SettingRow(Tr("Plan pendant l'absence", "Plan while away"), null, optIdlePlan)), Tr("Au repos", "When idle")));
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Nettoyage automatique", "Automatic cleanup"),
                Tr("Quand la mémoire utilisée dépasse le seuil (au plus toutes les 5 minutes). Utile en jeu avec 16 Go de RAM ou moins.", "When used memory exceeds the threshold (at most every 5 minutes). Useful for gaming with 16 GB of RAM or less."), optMem),
            SettingRow(Tr("Seuil", "Threshold"), null, WithUnit(optMemThreshold, Tr("% de mémoire utilisée", "% of memory in use"))),
            SettingRow(Tr("Vider le cache inutilisé", "Empty the unused cache"), Tr("La « liste de veille » de Windows : des fichiers gardés en mémoire au cas où.", "Windows' “standby list”: files kept in memory just in case."), optMemPurge),
            SettingRow(Tr("Alléger les programmes inactifs", "Trim idle programs"), Tr("Leurs données peu utilisées repartent sur le disque ; elles reviennent à la demande. " +
                "Jamais la fenêtre active, un jeu, ou un programme qui travaille.", "Their rarely used data goes back to disk; it returns on demand. " +
                "Never the active window, a game, or a busy program."), optMemTrim),
            SettingRow(Tr("Maintenant", "Now"), null, cleanNow),
            optMemNow), Tr("Mémoire", "Memory")));

        var save = new ModernButton(Tr("Enregistrer", "Save"), primary: true);
        save.Click += (_, _) => ApplyOptimizations();
        LoadOptimizations();
        return MakePage(Tr("Optimisations", "Optimizations"), Tr("Des automatismes pour un PC plus réactif et plus économe. Tous sont désactivés par défaut.", "Automations for a more responsive and efficient PC. All are off by default."), stack, save);
    }

    void LoadOptimizations()
    {
        optBoost.Checked = settings.ForegroundBoost.Enabled;
        var idle = settings.IdleSaver;
        optIdle.Checked = idle.Enabled;
        SetNum(optIdleMinutes, idle.Minutes);
        optIdlePlan.Items.Clear();
        optIdlePlanIds.Clear();
        foreach (var p in plans ??= PowerCfg.List())
        {
            optIdlePlan.Items.Add(p.Name);
            optIdlePlanIds.Add(p.Id);
        }
        if (!optIdlePlanIds.Contains(idle.Plan))
        {
            optIdlePlan.Items.Add(Tr($"(introuvable) {idle.Plan}", $"(not found) {idle.Plan}"));
            optIdlePlanIds.Add(idle.Plan);
        }
        optIdlePlan.SelectedIndex = optIdlePlanIds.IndexOf(idle.Plan);
        var mem = settings.MemoryCleanup;
        optMem.Checked = mem.Enabled;
        SetNum(optMemThreshold, mem.ThresholdPercent);
        optMemPurge.Checked = mem.PurgeStandby;
        optMemTrim.Checked = mem.TrimIdle;
    }

    void ApplyOptimizations()
    {
        settings.ForegroundBoost.Enabled = optBoost.Checked;
        settings.IdleSaver.Enabled = optIdle.Checked;
        settings.IdleSaver.Minutes = (int)optIdleMinutes.Value;
        if (optIdlePlan.SelectedIndex >= 0)
            settings.IdleSaver.Plan = optIdlePlanIds[optIdlePlan.SelectedIndex];
        settings.MemoryCleanup.Enabled = optMem.Checked;
        settings.MemoryCleanup.ThresholdPercent = (int)optMemThreshold.Value;
        settings.MemoryCleanup.PurgeStandby = optMemPurge.Checked;
        settings.MemoryCleanup.TrimIdle = optMemTrim.Checked;
        SaveAndApply();
        LoadOptimizations();
    }
}
