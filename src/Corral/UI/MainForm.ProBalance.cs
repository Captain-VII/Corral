using Corral.Core;
using Corral.Models;

namespace Corral.UI;

// Page « ProBalance » : seuils, préréglages, exclusions et statistiques.
public sealed partial class MainForm
{
    readonly ToggleSwitch pbEnabled = new();
    readonly ToggleSwitch pbNotify = new();
    readonly ToggleSwitch pbFairness = new();
    readonly Label pbSessions = new() { AutoSize = true, MaximumSize = new Size(520, 0), Tag = Theme.HintTag, Margin = new Padding(0, 4, 0, 0) };
    readonly NumericUpDown pbSystem = Num(10, 100);
    readonly NumericUpDown pbProcess = Num(1, 100);
    readonly NumericUpDown pbRestore = Num(0, 100);
    readonly NumericUpDown pbTrigger = Num(1, 60);
    readonly NumericUpDown pbRestoreSec = Num(1, 60);
    readonly TextBox pbExclusions = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 110, Dock = DockStyle.Fill, AccessibleName = Tr("Programmes jamais abaissés par ProBalance, un par ligne", "Programs ProBalance never lowers, one per line") };
    readonly List<(ModernButton Button, ProBalanceSettings.Preset Preset)> presetButtons = new();
    readonly Label presetState = new() { AutoSize = true, Tag = Theme.HintTag, Margin = new Padding(8, 8, 0, 0) };

    Control BuildProBalancePage()
    {
        var presets = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        foreach (var preset in ProBalanceSettings.Presets)
        {
            var b = new ModernButton(preset == ProBalanceSettings.Default ? Tr($"{preset.Name} (défaut)", $"{preset.Name} (default)") : preset.Name);
            b.Click += (_, _) =>
            {
                SetNum(pbSystem, preset.System);
                SetNum(pbProcess, preset.Process);
                SetNum(pbRestore, preset.Restore);
                SetNum(pbTrigger, preset.Trigger);
                SetNum(pbRestoreSec, preset.RestoreAfter);
                UpdatePresetButtons();
            };
            presetButtons.Add((b, preset));
            presets.Controls.Add(b);
        }
        presets.Controls.Add(presetState);
        tips.SetToolTip(presetButtons[0].Button, Tr("Doux : n'intervient que sur un processeur très chargé.", "Gentle: only acts when the CPU is very busy."));
        tips.SetToolTip(presetButtons[1].Button, Tr("Équilibré : le bon compromis pour la plupart des PC.", "Balanced: the right trade-off for most PCs."));
        tips.SetToolTip(presetButtons[2].Button, Tr("Réactif : intervient tôt pour garder la fenêtre active toujours fluide.", "Responsive: acts early to keep the active window smooth."));
        foreach (var n in new[] { pbSystem, pbProcess, pbRestore, pbTrigger, pbRestoreSec })
            n.ValueChanged += (_, _) => UpdatePresetButtons();

        pbNotify.CheckedChanged += (_, _) =>
        {
            if (loading)
                return;
            settings.NotifyProBalance = pbNotify.Checked;
            SaveSettings();
        };

        var stack = new CardStack();
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Activer ProBalance", "Enable ProBalance"), Tr("Abaisse temporairement la priorité des processus qui saturent le processeur. La fenêtre que vous utilisez n'est jamais touchée.", "Temporarily lowers the priority of processes that saturate the CPU. The window you are using is never touched."), pbEnabled),
            SettingRow(Tr("Me prévenir quand ProBalance intervient", "Notify me when ProBalance acts"), Tr("Affiche une bulle près de l'horloge (au plus une toutes les 30 secondes).", "Shows a balloon near the clock (at most one every 30 seconds)."), pbNotify),
            SettingRow(Tr("Équité entre les sessions", "Fairness between sessions"), Tr("Serveur RDS ou VDI multi-session : quand le processeur sature, une session qui prend plus d'une fois et demie sa part voit ses programmes abaissés. Utilise les seuils ci-dessous.", "RDS or multi-session VDI server: when the CPU is saturated, a session taking more than one and a half times its share has its programs lowered. Uses the thresholds below."), pbFairness),
            pbSessions), Tr("Fonctionnement", "Behavior")));

        var resetStats = new ModernButton(Tr("Remettre à zéro", "Reset")) { Height = 28 };
        resetStats.Click += (_, _) =>
        {
            if (MessageBox.Show(this, Tr("Effacer les statistiques de ProBalance ?", "Clear ProBalance statistics?"), Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                stats.Reset();
                RefreshStats();
            }
        };
        var statsGrid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 0, 0, 8) };
        for (int i = 0; i < 3; i++)
            statsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        foreach (var (label, value) in new[] { (Tr("Aujourd'hui", "Today"), statsToday), (Tr("7 derniers jours", "Last 7 days"), statsWeek), (Tr("30 derniers jours", "Last 30 days"), statsMonth) })
        {
            var cell = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
            cell.Controls.Add(new Label { Text = label, AutoSize = true, Tag = Theme.HintTag, Margin = new Padding(0) });
            cell.Controls.Add(value);
            statsGrid.Controls.Add(cell);
        }
        stack.Controls.Add(new Card(Rows(
            statsGrid,
            SettingRow(Tr("Les plus souvent abaissés (7 jours)", "Most often lowered (7 days)"), null, resetStats),
            statsTop), Tr("Statistiques", "Statistics")));
        RefreshStats();

        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Préréglage", "Preset"), Tr("Remplit les seuils ci-dessous. Pensez à enregistrer.", "Fills in the thresholds below. Remember to save."), presets)), Tr("Sensibilité", "Sensitivity")));
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Charge totale du processeur", "Total CPU load"), Tr("ProBalance n'agit qu'au-delà de ce seuil.", "ProBalance only acts above this threshold."), WithUnit(pbSystem, "%")),
            SettingRow(Tr("Charge d'un processus", "Load of one process"), Tr("Part du processeur total utilisée par un processus pour être visé.", "Share of the total CPU a process must use to be targeted."), WithUnit(pbProcess, "%")),
            SettingRow(Tr("Durée avant d'agir", "Delay before acting"), Tr("Les pics plus courts sont ignorés.", "Shorter spikes are ignored."), WithUnit(pbTrigger, "s"))), Tr("Déclenchement", "Trigger")));
        stack.Controls.Add(new Card(Rows(
            SettingRow(Tr("Charge du processus sous", "Process load below"), Tr("La priorité d'origine est rendue en dessous de ce seuil, ou quand le système se calme.", "The original priority is restored below this threshold, or when the system calms down."), WithUnit(pbRestore, "%")),
            SettingRow(Tr("Durée avant de restaurer", "Delay before restoring"), null, WithUnit(pbRestoreSec, "s"))), Tr("Restauration", "Restore")));
        var exclusions = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        exclusions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        exclusions.Controls.Add(new Label
        {
            AutoSize = true,
            Tag = Theme.HintTag,
            MaximumSize = new Size(640, 0),
            Margin = new Padding(0, 0, 0, 8),
            Text = Tr("Un nom d'exécutable par ligne (jokers acceptés). Astuce : clic droit sur un processus → « Exclure de ProBalance ». " +
                      "Les processus système et ceux dont une règle fixe la priorité sont toujours exclus.",
                "One executable name per line (wildcards allowed). Tip: right-click a process → “Exclude from ProBalance”. " +
                "System processes and those whose priority is set by a rule are always excluded."),
        });
        exclusions.Controls.Add(pbExclusions);
        stack.Controls.Add(new Card(exclusions, "Exclusions"));

        var save = new ModernButton(Tr("Enregistrer", "Save"), primary: true);
        save.Click += (_, _) => ApplyProBalance();
        tips.SetToolTip(save, Tr("Appliquer les réglages de ProBalance", "Apply the ProBalance settings"));
        return MakePage("ProBalance", Tr("Garde le système réactif quand un programme accapare le processeur.", "Keeps the system responsive when a program hogs the CPU."), stack, save);
    }

    void UpdatePresetButtons()
    {
        var current = new ProBalanceSettings
        {
            SystemThreshold = (double)pbSystem.Value,
            ProcessThreshold = (double)pbProcess.Value,
            RestoreThreshold = (double)pbRestore.Value,
            TriggerSeconds = (int)pbTrigger.Value,
            RestoreSeconds = (int)pbRestoreSec.Value,
        }.CurrentPreset();
        foreach (var (button, preset) in presetButtons)
            button.Toggled = preset == current;
        presetState.Text = current == null ? Tr("Personnalisé", "Custom") : "";
    }

    readonly Label statsToday = StatValue();
    readonly Label statsWeek = StatValue();
    readonly Label statsMonth = StatValue();
    readonly Label statsTop = new() { AutoSize = true, Tag = Theme.HintTag, MaximumSize = new Size(640, 0), Margin = new Padding(0, 0, 0, 4) };

    static Label StatValue() => new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 18f), Margin = new Padding(0, 2, 0, 0) };

    void RefreshStats()
    {
        static string Interventions(int n) => n == 0 ? "0" : n.ToString("N0");
        statsToday.Text = Interventions(stats.CountToday());
        statsWeek.Text = Interventions(stats.Count(7));
        statsMonth.Text = Interventions(stats.Count(30));
        var top = stats.TopPrograms(7);
        statsTop.Text = top.Count == 0
            ? Tr("Aucune intervention ces 7 derniers jours : le processeur n'a pas été saturé, ou ProBalance est désactivé.", "No action in the last 7 days: the CPU was not saturated, or ProBalance is off.")
            : string.Join("   ·   ", top.Select(t => Tr($"{t.Name} ({t.Count} fois)", $"{t.Name} ({t.Count}×)")));
    }

    void LoadProBalance()
    {
        var pb = settings.ProBalance;
        pbEnabled.Checked = pb.Enabled;
        pbNotify.Checked = settings.NotifyProBalance;
        pbFairness.Checked = pb.SessionFairness;
        chart.Threshold = pb.Enabled ? pb.SystemThreshold : null;
        chart.Invalidate();
        SetNum(pbSystem, pb.SystemThreshold);
        SetNum(pbProcess, pb.ProcessThreshold);
        SetNum(pbRestore, pb.RestoreThreshold);
        SetNum(pbTrigger, pb.TriggerSeconds);
        SetNum(pbRestoreSec, pb.RestoreSeconds);
        pbExclusions.Text = string.Join(Environment.NewLine, pb.Exclusions);
        UpdatePresetButtons();
    }

    void ApplyProBalance()
    {
        var pb = settings.ProBalance;
        pb.Enabled = pbEnabled.Checked;
        pb.SessionFairness = pbFairness.Checked;
        pb.SystemThreshold = (double)pbSystem.Value;
        pb.ProcessThreshold = (double)pbProcess.Value;
        pb.RestoreThreshold = (double)pbRestore.Value;
        pb.TriggerSeconds = (int)pbTrigger.Value;
        pb.RestoreSeconds = (int)pbRestoreSec.Value;
        pb.Exclusions = pbExclusions.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SaveAndApply();
        LoadProBalance();
    }

    /// <summary>Activer ou désactiver ProBalance depuis le menu de l'icône de notification.</summary>
    public void SetProBalanceEnabled(bool enabled)
    {
        settings.ProBalance.Enabled = enabled;
        SaveAndApply();
        LoadProBalance();
    }
}
