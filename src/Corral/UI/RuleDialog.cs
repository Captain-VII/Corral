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

    readonly TextBox pattern = new() { Width = 300 };
    readonly ToggleSwitch enabled = new() { Checked = true };
    readonly ComboBox priority = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly ToggleSwitch affinityOn = new();
    readonly CheckedListBox cpus = new() { CheckOnClick = true, Width = 300, Height = 120, MultiColumn = true, ColumnWidth = 70, Enabled = false };
    readonly ComboBox plan = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly NumericUpDown cpuLimit = new() { Minimum = 0, Maximum = 99, Width = 80, TextAlign = HorizontalAlignment.Right };
    readonly NumericUpDown memLimit = new() { Minimum = 0, Maximum = 1_048_576, Increment = 256, Width = 100, TextAlign = HorizontalAlignment.Right };
    readonly int cpuCount = Math.Min(Environment.ProcessorCount, 64);

    public RuleDialog(Rule? rule, IReadOnlyList<PowerPlanInfo> plans, bool isNew)
    {
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

        var all = new ModernButton("Tous") { Height = 28, Enabled = false };
        var none = new ModernButton("Aucun") { Height = 28, Enabled = false };
        all.Click += (_, _) => SetAllCpus(true);
        none.Click += (_, _) => SetAllCpus(false);
        affinityOn.CheckedChanged += (_, _) => cpus.Enabled = all.Enabled = none.Enabled = affinityOn.Checked;

        var ok = new ModernButton("Enregistrer", primary: true);
        var cancel = new ModernButton("Annuler") { DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => OnOk();
        AcceptButton = ok;
        CancelButton = cancel;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(20, 12, 20, 16) };
        AddSection(grid, "Processus");
        AddRow(grid, "Nom de l'exécutable", pattern);
        AddRow(grid, "", new Label { Text = "Jokers acceptés : chrome* , jeu?.exe", AutoSize = true, Tag = Theme.HintTag, Margin = new Padding(3, 0, 3, 6) });
        AddRow(grid, "Règle active", enabled);

        AddSection(grid, "Performance");
        AddRow(grid, "Priorité", priority);
        AddRow(grid, "Limiter à certains cœurs", affinityOn);
        AddRow(grid, "", cpus);
        var quick = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        quick.Controls.Add(all);
        quick.Controls.Add(none);
        AddRow(grid, "", quick);

        AddSection(grid, "Alimentation et limites");
        AddRow(grid, "Plan d'alimentation", plan);
        AddRow(grid, "CPU max (% du total)", WithHint(cpuLimit, "0 = aucune limite"));
        AddRow(grid, "RAM max (Mo)", WithHint(memLimit, "0 = aucune limite"));
        AddRow(grid, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            Tag = Theme.HintTag,
            Margin = new Padding(3, 2, 3, 3),
            Text = "Ces limites s'appliquent au lancement du processus ; pour les assouplir, relancez-le. " +
                   "Un processus qui dépasse la limite de RAM peut planter.",
        });

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 18, 0, 0) };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        grid.Controls.Add(buttons, 0, grid.RowCount);
        grid.SetColumnSpan(buttons, 2);
        Controls.Add(grid);

        LoadRule(rule);
        Theme.Apply(this);
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
