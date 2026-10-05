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

    readonly TextBox pattern = new() { Width = 280 };
    readonly CheckBox enabled = new() { Text = "Règle active", AutoSize = true, Checked = true };
    readonly ComboBox priority = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    readonly CheckBox affinityOn = new() { Text = "Limiter aux CPU cochés", AutoSize = true };
    readonly CheckedListBox cpus = new() { CheckOnClick = true, Width = 280, Height = 130, MultiColumn = true, ColumnWidth = 65, Enabled = false };
    readonly ComboBox plan = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 280 };
    readonly NumericUpDown cpuLimit = new() { Minimum = 0, Maximum = 99, Width = 80 };
    readonly NumericUpDown memLimit = new() { Minimum = 0, Maximum = 1_048_576, Increment = 256, Width = 100 };
    readonly int cpuCount = Math.Min(Environment.ProcessorCount, 64);

    public RuleDialog(Rule? rule, IReadOnlyList<PowerPlanInfo> plans, bool isNew)
    {
        Text = isNew ? "Nouvelle règle" : "Modifier la règle";
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
        affinityOn.CheckedChanged += (_, _) => cpus.Enabled = affinityOn.Checked;

        var ok = new Button { Text = "OK", AutoSize = true };
        var cancel = new Button { Text = "Annuler", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => OnOk();
        AcceptButton = ok;
        CancelButton = cancel;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10) };
        AddRow(grid, "Processus (ex. jeu.exe, chrome*) :", pattern);
        AddRow(grid, "", enabled);
        AddRow(grid, "Priorité :", priority);
        AddRow(grid, "Affinité :", affinityOn);
        AddRow(grid, "", cpus);
        AddRow(grid, "Plan d'alimentation :", plan);
        AddRow(grid, "CPU max (% du total, 0 = aucun) :", cpuLimit);
        AddRow(grid, "RAM max (Mo, 0 = aucune) :", memLimit);
        AddRow(grid, "", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(280, 0),
            Tag = Theme.HintTag,
            Text = "Les limites CPU/RAM s'appliquent au lancement du processus. Pour les assouplir, relancez-le. " +
                   "Un processus qui dépasse la limite RAM peut planter.",
        });
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
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

    static void AddRow(TableLayoutPanel grid, string label, Control control)
    {
        int row = grid.RowCount++;
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) }, 0, row);
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
