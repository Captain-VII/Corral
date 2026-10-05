using System.Diagnostics;
using Corral.Core;
using Corral.Models;

namespace Corral.UI;

public sealed class RuleDialog : Form
{
    sealed record Choice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    public static readonly (string Label, ProcessPriorityClass? Value)[] Priorities =
    {
        ("(inchangée)", null),
        ("Inactive", ProcessPriorityClass.Idle),
        ("Inférieure à la normale", ProcessPriorityClass.BelowNormal),
        ("Normale", ProcessPriorityClass.Normal),
        ("Supérieure à la normale", ProcessPriorityClass.AboveNormal),
        ("Haute", ProcessPriorityClass.High),
    };

    static readonly Dictionary<ProcessPriorityClass, string> PriorityHelp = new()
    {
        [ProcessPriorityClass.Idle] = "Le programme n'utilise le processeur que quand rien d'autre n'en a besoin. Pour ce qui peut attendre.",
        [ProcessPriorityClass.BelowNormal] = "Laisse passer les autres programmes. Idéal pour les tâches de fond : sauvegarde, compression, téléchargements.",
        [ProcessPriorityClass.Normal] = "La priorité par défaut de Windows.",
        [ProcessPriorityClass.AboveNormal] = "Passe un peu avant les autres. Bien pour le streaming, l'enregistrement ou la musique.",
        [ProcessPriorityClass.High] = "Passe avant presque tout. Idéal pour un jeu ; à éviter pour un programme qui tourne en permanence.",
    };

    // Plans d'alimentation standard de Windows, proposés par le modèle « Jeu »
    static readonly Guid[] PerformancePlans =
    {
        Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61"), // Performances optimales
        Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), // Performances élevées
    };

    readonly TextBox pattern = new() { Width = 300 };
    readonly ToggleSwitch enabled = new() { Checked = true };
    readonly ComboBox priority = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly ToggleSwitch affinityOn = new();
    readonly CheckedListBox cpus = new() { CheckOnClick = true, Width = 300, Height = 120, MultiColumn = true, ColumnWidth = 70, Enabled = false };
    readonly ComboBox plan = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly NumericUpDown cpuLimit = new() { Minimum = 0, Maximum = 99, Width = 80, TextAlign = HorizontalAlignment.Right };
    readonly NumericUpDown memLimit = new() { Minimum = 0, Maximum = 1_048_576, Increment = 256, Width = 100, TextAlign = HorizontalAlignment.Right };
    readonly Label preview = Help();
    readonly Label priorityHelp = Help();
    readonly Label affinityHelp = Help();
    readonly Label planHelp = Help();
    readonly ToolTip tips = Theme.CreateToolTip();
    readonly IReadOnlyList<string> running;
    readonly int cpuCount = Math.Min(Environment.ProcessorCount, 64);

    public RuleDialog(Rule? rule, IReadOnlyList<PowerPlanInfo> plans, bool isNew, IReadOnlyList<string>? runningNames = null)
    {
        running = runningNames ?? Array.Empty<string>();
        Text = isNew ? "Nouvelle règle" : "Modifier la règle";
        Font = Ui.Base;
        Icon = AppIcon.Load(new Size(32, 32));
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        foreach (var (label, value) in Priorities)
            priority.Items.Add(new Choice<ProcessPriorityClass?>(label, value));
        for (int i = 0; i < cpuCount; i++)
            cpus.Items.Add($"CPU {i}");
        plan.Items.Add(new Choice<Guid?>("(inchangé)", null));
        foreach (var p in plans)
            plan.Items.Add(new Choice<Guid?>(p.Name, p.Id));

        // Autocomplétion avec les processus en cours
        pattern.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        pattern.AutoCompleteSource = AutoCompleteSource.CustomSource;
        pattern.AutoCompleteCustomSource.AddRange(running.Distinct(StringComparer.OrdinalIgnoreCase).Select(n => n + ".exe").ToArray());

        var all = new ModernButton("Tous") { Height = 28, Enabled = false };
        var none = new ModernButton("Aucun") { Height = 28, Enabled = false };
        all.Click += (_, _) => SetAllCpus(true);
        none.Click += (_, _) => SetAllCpus(false);
        affinityOn.CheckedChanged += (_, _) =>
        {
            cpus.Enabled = all.Enabled = none.Enabled = affinityOn.Checked;
            UpdateHelp();
        };
        // ItemCheck est levé avant le changement : on relit après
        cpus.ItemCheck += (_, _) =>
        {
            if (IsHandleCreated) // pendant la construction, UpdateHelp est appelé à la fin
                BeginInvoke(UpdateHelp);
        };
        pattern.TextChanged += (_, _) => UpdatePreview();
        priority.SelectedIndexChanged += (_, _) => UpdateHelp();
        plan.SelectedIndexChanged += (_, _) => UpdateHelp();

        var ok = new ModernButton("Enregistrer", primary: true);
        var cancel = new ModernButton("Annuler") { DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => OnOk();
        AcceptButton = ok;
        CancelButton = cancel;

        var templates = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(310, 0), Margin = new Padding(0) };
        AddTemplate(templates, "Jeu", "Priorité haute et plan Performances élevées s'il existe.", () =>
        {
            Reset(ProcessPriorityClass.High);
            var perf = plan.Items.Cast<Choice<Guid?>>().ToList().FindIndex(c => c.Value is { } id && PerformancePlans.Contains(id));
            if (perf >= 0)
                plan.SelectedIndex = perf;
        });
        AddTemplate(templates, "Streaming", "Priorité supérieure à la normale : fluide sans gêner le reste.", () => Reset(ProcessPriorityClass.AboveNormal));
        AddTemplate(templates, "Tâche de fond", "Priorité inférieure à la normale : passe après vos programmes.", () => Reset(ProcessPriorityClass.BelowNormal));
        AddTemplate(templates, "Brider", "Priorité basse et 25 % du processeur au maximum, pour un programme trop gourmand.", () =>
        {
            Reset(ProcessPriorityClass.BelowNormal);
            cpuLimit.Value = 25;
        });

        tips.SetToolTip(enabled, "Une règle inactive est conservée mais n'est plus appliquée.");
        tips.SetToolTip(affinityOn, "Réserver certains cœurs du processeur à ce programme.");
        tips.SetToolTip(cpuLimit, "Plafond strict d'usage du processeur, en % du total.");
        tips.SetToolTip(memLimit, "Mémoire maximale que le programme peut réserver.");

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(20, 12, 20, 16) };
        AddSection(grid, "Processus");
        AddRow(grid, "Nom de l'exécutable", pattern);
        AddRow(grid, "", preview);
        AddRow(grid, "Règle active", enabled);
        AddRow(grid, "Partir d'un modèle", templates);

        AddSection(grid, "Performance");
        AddRow(grid, "Priorité", priority);
        AddRow(grid, "", priorityHelp);
        AddRow(grid, "Limiter à certains cœurs", affinityOn);
        AddRow(grid, "", affinityHelp);
        AddRow(grid, "", cpus);
        var quick = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        quick.Controls.Add(all);
        quick.Controls.Add(none);
        AddRow(grid, "", quick);

        AddSection(grid, "Alimentation et limites");
        AddRow(grid, "Plan d'alimentation", plan);
        AddRow(grid, "", planHelp);
        AddRow(grid, "CPU max (% du total)", WithHint(cpuLimit, "0 = aucune limite"));
        AddRow(grid, "RAM max (Mo)", WithHint(memLimit, "0 = aucune limite"));
        var limitsHelp = Help();
        limitsHelp.Text = "Ces limites s'appliquent au lancement du processus ; pour les assouplir, relancez-le. " +
                          "Un processus qui dépasse la limite de RAM peut planter.";
        AddRow(grid, "", limitsHelp);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 18, 0, 0) };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        grid.Controls.Add(buttons, 0, grid.RowCount);
        grid.SetColumnSpan(buttons, 2);
        Controls.Add(grid);

        LoadRule(rule);
        UpdatePreview();
        UpdateHelp();
        Theme.Apply(this);
    }

    static Label Help() => new() { AutoSize = true, MaximumSize = new Size(300, 0), Tag = Theme.HintTag, Margin = new Padding(3, 0, 3, 8) };

    void AddTemplate(FlowLayoutPanel panel, string name, string description, Action apply)
    {
        var b = new ModernButton(name) { Height = 28, Margin = new Padding(0, 0, 6, 6) };
        b.Click += (_, _) => apply();
        tips.SetToolTip(b, description);
        panel.Controls.Add(b);
    }

    /// <summary>Remet les réglages à zéro avant d'appliquer un modèle (le nom du processus est conservé).</summary>
    void Reset(ProcessPriorityClass p)
    {
        priority.SelectedIndex = Math.Max(0, Array.FindIndex(Priorities, x => x.Value == p));
        affinityOn.Checked = false;
        plan.SelectedIndex = 0;
        cpuLimit.Value = 0;
        memLimit.Value = 0;
    }

    void UpdatePreview()
    {
        var text = pattern.Text.Trim();
        if (text.Length == 0)
        {
            preview.Text = "Exemples : jeu.exe, chrome* (tout ce qui commence par chrome), jeu?.exe";
            return;
        }
        var matches = RuleMatcher.Preview(text, running);
        int system = matches.Count(n => Exclusions.IsProtected(n, int.MaxValue, 0));
        var usable = matches.Where(n => !Exclusions.IsProtected(n, int.MaxValue, 0))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() > 1 ? $"{g.Key} (×{g.Count()})" : g.Key)
            .ToList();
        preview.Text = usable.Count == 0 && system == 0
            ? "Aucun processus en cours ne correspond : la règle s'appliquera à leur lancement."
            : $"S'applique à {matches.Count - system} processus en cours" +
              (usable.Count > 0 ? " : " + string.Join(", ", usable.Take(4)) + (usable.Count > 4 ? "…" : "") : "") +
              (system > 0 ? $". {system} processus système ignoré(s)." : ".");
    }

    void UpdateHelp()
    {
        var p = ((Choice<ProcessPriorityClass?>?)priority.SelectedItem)?.Value;
        priorityHelp.Text = p is { } v && PriorityHelp.TryGetValue(v, out var help) ? help : "La priorité du programme n'est pas modifiée.";

        int checkedCount = cpus.CheckedIndices.Count;
        affinityHelp.Text = !affinityOn.Checked
            ? $"Le programme peut utiliser les {cpuCount} cœurs."
            : $"Limité à {checkedCount} cœur(s) sur {cpuCount}. Utile pour un vieux jeu ou pour garder des cœurs libres.";

        var chosen = (Choice<Guid?>?)plan.SelectedItem;
        planHelp.Text = chosen?.Value == null
            ? "Le plan d'alimentation n'est pas changé."
            : $"« {chosen.Label} » est activé tant que ce programme tourne, puis le plan d'origine revient.";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            tips.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    public Rule Result { get; private set; } = new();

    void SetAllCpus(bool value)
    {
        for (int i = 0; i < cpus.Items.Count; i++)
            cpus.SetItemChecked(i, value);
    }

    static Control WithHint(Control c, string hint)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        f.Controls.Add(c);
        f.Controls.Add(new Label { Text = hint, AutoSize = true, Tag = Theme.HintTag, Margin = new Padding(6, 6, 0, 0) });
        return f;
    }

    static void AddSection(TableLayoutPanel grid, string title)
    {
        int row = grid.RowCount++;
        var label = new Label { Text = title, Font = Ui.Section, AutoSize = true, Margin = new Padding(0, row == 0 ? 4 : 18, 0, 6) };
        grid.Controls.Add(label, 0, row);
        grid.SetColumnSpan(label, 2);
    }

    static void AddRow(TableLayoutPanel grid, string label, Control control)
    {
        int row = grid.RowCount++;
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 24, 3) }, 0, row);
        grid.Controls.Add(control, 1, row);
    }

    void LoadRule(Rule? rule)
    {
        rule ??= new Rule();
        pattern.Text = rule.Pattern;
        enabled.Checked = rule.Enabled;

        priority.SelectedIndex = Math.Max(0, Array.FindIndex(Priorities, p => p.Value == rule.Priority));

        affinityOn.Checked = rule.AffinityMask != null;
        long mask = rule.AffinityMask ?? -1;
        for (int i = 0; i < cpuCount; i++)
            cpus.SetItemChecked(i, ((mask >> i) & 1) == 1);

        plan.SelectedIndex = 0;
        if (rule.PowerPlan is { } id)
        {
            int idx = plan.Items.Cast<Choice<Guid?>>().ToList().FindIndex(c => c.Value == id);
            if (idx < 0)
                idx = plan.Items.Add(new Choice<Guid?>($"(introuvable) {id}", id));
            plan.SelectedIndex = idx;
        }

        cpuLimit.Value = Math.Clamp(rule.CpuLimitPercent ?? 0, 0, 99);
        memLimit.Value = Math.Clamp(rule.MemoryLimitMB ?? 0, 0, 1_048_576);
    }

    void OnOk()
    {
        var name = pattern.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Indiquez un nom de processus.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        long? mask = null;
        if (affinityOn.Checked)
        {
            long m = 0;
            foreach (int i in cpus.CheckedIndices)
                m |= 1L << i;
            if (m == 0)
            {
                MessageBox.Show(this, "Cochez au moins un CPU.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            mask = m;
        }

        Result = new Rule
        {
            Pattern = name,
            Enabled = enabled.Checked,
            Priority = ((Choice<ProcessPriorityClass?>)priority.SelectedItem!).Value,
            AffinityMask = mask,
            PowerPlan = ((Choice<Guid?>)plan.SelectedItem!).Value,
            CpuLimitPercent = cpuLimit.Value > 0 ? (int)cpuLimit.Value : null,
            MemoryLimitMB = memLimit.Value > 0 ? (int)memLimit.Value : null,
        };
        DialogResult = DialogResult.OK;
    }
}
