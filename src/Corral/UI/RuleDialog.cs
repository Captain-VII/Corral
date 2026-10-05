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
        (Tr("(inchangée)", "(unchanged)"), null),
        (Tr("Inactive", "Idle"), ProcessPriorityClass.Idle),
        (Tr("Inférieure à la normale", "Below normal"), ProcessPriorityClass.BelowNormal),
        (Tr("Normale", "Normal"), ProcessPriorityClass.Normal),
        (Tr("Supérieure à la normale", "Above normal"), ProcessPriorityClass.AboveNormal),
        (Tr("Haute", "High"), ProcessPriorityClass.High),
    };

    static readonly Dictionary<ProcessPriorityClass, string> PriorityHelp = new()
    {
        [ProcessPriorityClass.Idle] = Tr("Le programme n'utilise le processeur que quand rien d'autre n'en a besoin. Pour ce qui peut attendre.", "The program only uses the CPU when nothing else needs it. For things that can wait."),
        [ProcessPriorityClass.BelowNormal] = Tr("Laisse passer les autres programmes. Idéal pour les tâches de fond : sauvegarde, compression, téléchargements.", "Lets other programs go first. Ideal for background tasks: backup, compression, downloads."),
        [ProcessPriorityClass.Normal] = Tr("La priorité par défaut de Windows.", "Windows' default priority."),
        [ProcessPriorityClass.AboveNormal] = Tr("Passe un peu avant les autres. Bien pour le streaming, l'enregistrement ou la musique.", "Goes a little ahead of others. Good for streaming, recording or music."),
        [ProcessPriorityClass.High] = Tr("Passe avant presque tout. Idéal pour un jeu ; à éviter pour un programme qui tourne en permanence.", "Goes ahead of almost everything. Ideal for a game; avoid for a program that runs all the time."),
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
    readonly ToggleSwitch isGame = new();
    readonly ComboBox efficiency = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly ComboBox ioPriority = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly ComboBox memoryPriority = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly Label efficiencyHelp = Help();
    readonly Label ioHelp = Help();
    readonly Label memoryHelp = Help();
    readonly ComboBox gpu = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly Label gpuHelp = Help();
    static readonly GpuPreference?[] GpuChoices = { null, GpuPreference.HighPerformance, GpuPreference.PowerSaving, GpuPreference.Default };
    readonly ToggleSwitch keepAwake = new();
    readonly ComboBox block = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly NumericUpDown alertCpu = new() { Minimum = 0, Maximum = 100, Width = 80, TextAlign = HorizontalAlignment.Right };
    readonly NumericUpDown alertMemory = new() { Minimum = 0, Maximum = 1_048_576, Increment = 512, Width = 100, TextAlign = HorizontalAlignment.Right };
    readonly NumericUpDown alertMinutes = new() { Minimum = 1, Maximum = 120, Value = 2, Width = 80, TextAlign = HorizontalAlignment.Right };
    readonly ComboBox alertAction = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    readonly Label blockHelp = Help();
    readonly Label alertHelp = Help();
    readonly ToolTip tips = Theme.CreateToolTip();
    readonly IReadOnlyList<string> running;
    readonly int cpuCount = Math.Min(Environment.ProcessorCount, 64);

    public RuleDialog(Rule? rule, IReadOnlyList<PowerPlanInfo> plans, bool isNew, IReadOnlyList<string>? runningNames = null)
    {
        running = runningNames ?? Array.Empty<string>();
        Text = isNew ? Tr("Nouvelle règle", "New rule") : Tr("Modifier la règle", "Edit rule");
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
        plan.Items.Add(new Choice<Guid?>(Tr("(inchangé)", "(unchanged)"), null));
        foreach (var p in plans)
            plan.Items.Add(new Choice<Guid?>(p.Name, p.Id));

        // Autocomplétion avec les processus en cours
        pattern.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        pattern.AutoCompleteSource = AutoCompleteSource.CustomSource;
        pattern.AutoCompleteCustomSource.AddRange(running.Distinct(StringComparer.OrdinalIgnoreCase).Select(n => n + ".exe").ToArray());

        var all = new ModernButton(Tr("Tous", "All")) { Height = 28, Enabled = false };
        var none = new ModernButton(Tr("Aucun", "None")) { Height = 28, Enabled = false };
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

        var ok = new ModernButton(Tr("Enregistrer", "Save"), primary: true);
        var cancel = new ModernButton(Tr("Annuler", "Cancel")) { DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => OnOk();
        AcceptButton = ok;
        CancelButton = cancel;

        // Choix de cœurs adaptés à ce processeur (cœurs P, V-Cache, sans SMT…)
        var topology = CpuTopology.Current?.Presets() ?? Array.Empty<AffinityPreset>();
        var gamePreset = topology.FirstOrDefault(p => p.Name == CpuTopology.VCacheName || p.Name == CpuTopology.PerformanceCoresName);

        var templates = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(310, 0), Margin = new Padding(0) };
        AddTemplate(templates, Tr("Jeu", "Game"),
            Tr("Priorité haute, marqué comme jeu (active le Mode Jeu), plan Performances élevées s'il existe", "High priority, marked as a game (turns on Game Mode), High performance plan if it exists") +
            (gamePreset != null ? Tr($" et « {gamePreset.Name} ».", $" and “{gamePreset.Name}”.") : "."), () =>
        {
            Reset(ProcessPriorityClass.High);
            isGame.Checked = true;
            var perf = plan.Items.Cast<Choice<Guid?>>().ToList().FindIndex(c => c.Value is { } id && PowerCfg.PerformancePlans.Contains(id));
            if (perf >= 0)
                plan.SelectedIndex = perf;
            if (gamePreset != null)
                SetMask(gamePreset.Mask);
        });
        AddTemplate(templates, "Streaming", Tr("Priorité supérieure à la normale, jamais en mode efficacité : l'encodage reste fluide.", "Above normal priority, never in efficiency mode: encoding stays smooth."), () =>
        {
            Reset(ProcessPriorityClass.AboveNormal);
            efficiency.SelectedIndex = 2;
        });
        AddTemplate(templates, Tr("Tâche de fond", "Background task"), Tr("Priorité basse, mode efficacité et disque en priorité basse : passe après tout le reste.", "Low priority, efficiency mode and low disk priority: goes after everything else."), () =>
        {
            Reset(ProcessPriorityClass.BelowNormal);
            efficiency.SelectedIndex = 1;
            ioPriority.SelectedIndex = 2;
        });
        AddTemplate(templates, Tr("Brider", "Throttle"), Tr("Priorité basse, mode efficacité et 25 % du processeur au maximum, pour un programme trop gourmand.", "Low priority, efficiency mode and at most 25 % of the CPU, for a program that is too greedy."), () =>
        {
            Reset(ProcessPriorityClass.BelowNormal);
            efficiency.SelectedIndex = 1;
            cpuLimit.Value = 25;
        });

        foreach (var (label, _) in EfficiencyChoices) efficiency.Items.Add(label);
        foreach (var (label, _) in IoChoices) ioPriority.Items.Add(label);
        foreach (var (label, _) in MemoryChoices) memoryPriority.Items.Add(label);
        efficiency.SelectedIndexChanged += (_, _) => UpdateHelp();
        ioPriority.SelectedIndexChanged += (_, _) => UpdateHelp();
        memoryPriority.SelectedIndexChanged += (_, _) => UpdateHelp();
        gpu.Items.AddRange(new object[] { Tr("(inchangée)", "(unchanged)"), Tr("Haute performance", "High performance"), Tr("Économie d'énergie", "Power saving"), Tr("Laisser Windows décider", "Let Windows decide") });
        gpu.SelectedIndexChanged += (_, _) => UpdateHelp();

        tips.SetToolTip(enabled, Tr("Une règle inactive est conservée mais n'est plus appliquée.", "A disabled rule is kept but no longer applied."));
        tips.SetToolTip(isGame, Tr("Tant que ce programme tourne, le Mode Jeu s'active (s'il est en automatique).", "While this program runs, Game Mode turns on (if set to automatic)."));
        tips.SetToolTip(affinityOn, Tr("Réserver certains cœurs du processeur à ce programme.", "Reserve some CPU cores for this program."));
        tips.SetToolTip(cpuLimit, Tr("Plafond strict d'usage du processeur, en % du total.", "Hard cap on CPU usage, in % of the total."));
        tips.SetToolTip(memLimit, Tr("Mémoire maximale que le programme peut réserver.", "Maximum memory the program can commit."));

        var left = Grid();
        AddSection(left, Tr("Processus", "Process"));
        AddRow(left, Tr("Nom de l'exécutable", "Executable name"), pattern);
        AddRow(left, "", preview);
        AddRow(left, Tr("Règle active", "Rule enabled"), enabled);
        AddRow(left, Tr("C'est un jeu", "This is a game"), isGame);
        AddRow(left, Tr("Partir d'un modèle", "Start from a template"), templates);

        AddSection(left, "Performance");
        AddRow(left, Tr("Priorité", "Priority"), priority);
        AddRow(left, "", priorityHelp);
        AddRow(left, Tr("Limiter à certains cœurs", "Limit to some cores"), affinityOn);
        AddRow(left, "", affinityHelp);
        AddRow(left, "", cpus);
        var quick = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(310, 0), Margin = new Padding(0, 4, 0, 0) };
        quick.Controls.Add(all);
        quick.Controls.Add(none);
        foreach (var preset in topology)
        {
            var b = new ModernButton(preset.Name) { Height = 28, Enabled = false, Margin = new Padding(0, 0, 6, 6) };
            b.Click += (_, _) => SetMask(preset.Mask);
            tips.SetToolTip(b, preset.Description);
            affinityOn.CheckedChanged += (_, _) => b.Enabled = affinityOn.Checked;
            quick.Controls.Add(b);
        }
        AddRow(left, "", quick);

        var right = Grid();
        AddSection(right, Tr("Alimentation et limites", "Power and limits"));
        AddRow(right, Tr("Plan d'alimentation", "Power plan"), plan);
        AddRow(right, "", planHelp);
        AddRow(right, Tr("CPU max (% du total)", "Max CPU (% of total)"), WithHint(cpuLimit, Tr("0 = aucune limite", "0 = no limit")));
        AddRow(right, Tr("RAM max (Mo)", "Max RAM (MB)"), WithHint(memLimit, Tr("0 = aucune limite", "0 = no limit")));
        var limitsHelp = Help();
        limitsHelp.Text = Tr("Ces limites s'appliquent au lancement du processus ; pour les assouplir, relancez-le. " +
                             "Un processus qui dépasse la limite de RAM peut planter.",
            "These limits apply when the process starts; to loosen them, restart it. " +
            "A process that exceeds the RAM limit may crash.");
        AddRow(right, "", limitsHelp);

        AddSection(right, Tr("Avancé", "Advanced"));
        AddRow(right, Tr("Mode efficacité", "Efficiency mode"), efficiency);
        AddRow(right, "", efficiencyHelp);
        AddRow(right, Tr("Priorité disque", "Disk priority"), ioPriority);
        AddRow(right, "", ioHelp);
        AddRow(right, Tr("Priorité mémoire", "Memory priority"), memoryPriority);
        AddRow(right, "", memoryHelp);
        AddRow(right, Tr("Carte graphique", "Graphics card"), gpu);
        AddRow(right, "", gpuHelp);

        block.Items.AddRange(new object[] { Tr("Non", "No"), Tr("Toujours (fermé dès son lancement)", "Always (closed as soon as it starts)"), Tr("Une seule instance", "Single instance") });
        alertAction.Items.AddRange(new object[] { Tr("Me prévenir", "Notify me"), Tr("Baisser sa priorité au minimum", "Lower its priority to the minimum"), Tr("Le fermer", "Close it") });
        block.SelectedIndexChanged += (_, _) => UpdateHelp();
        foreach (var n in new[] { alertCpu, alertMemory, alertMinutes })
            n.ValueChanged += (_, _) => UpdateHelp();
        alertAction.SelectedIndexChanged += (_, _) => UpdateHelp();
        tips.SetToolTip(keepAwake, Tr("Le PC ne se met pas en veille tant que ce programme tourne (l'écran peut s'éteindre).", "The PC does not go to sleep while this program runs (the screen may turn off)."));

        AddSection(right, Tr("Automatisations", "Automations"));
        AddRow(right, Tr("Empêcher la mise en veille", "Prevent sleep"), keepAwake);
        AddRow(right, Tr("Bloquer le programme", "Block the program"), block);
        AddRow(right, "", blockHelp);
        AddRow(right, Tr("Surveiller : CPU au-delà de", "Watch: CPU above"), WithHint(alertCpu, Tr("% (0 = non)", "% (0 = off)")));
        AddRow(right, Tr("ou mémoire au-delà de", "or memory above"), WithHint(alertMemory, Tr("Mo (0 = non)", "MB (0 = off)")));
        AddRow(right, Tr("pendant", "for"), WithHint(alertMinutes, "minutes"));
        AddRow(right, Tr("alors", "then"), alertAction);
        AddRow(right, "", alertHelp);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 0) };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        var outer = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 16) };
        outer.Controls.Add(left, 0, 0);
        outer.Controls.Add(right, 1, 0);
        outer.Controls.Add(buttons, 0, 1);
        outer.SetColumnSpan(buttons, 2);
        Controls.Add(outer);

        LoadRule(rule);
        UpdatePreview();
        UpdateHelp();
        Theme.Apply(this);
    }

    static Label Help() => new() { AutoSize = true, MaximumSize = new Size(300, 0), Tag = Theme.HintTag, Margin = new Padding(3, 0, 3, 8) };

    static TableLayoutPanel Grid() => new()
    {
        ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(12, 8, 12, 0), Anchor = AnchorStyles.Top | AnchorStyles.Left,
    };

    static readonly (string Label, bool? Value)[] EfficiencyChoices =
        { (Tr("(inchangé)", "(unchanged)"), null), (Tr("Activé", "On"), true), (Tr("Jamais", "Never"), false) };

    static readonly (string Label, IoPriorityLevel? Value)[] IoChoices =
        { (Tr("(inchangée)", "(unchanged)"), null), (Tr("Très basse", "Very low"), IoPriorityLevel.VeryLow), (Tr("Basse", "Low"), IoPriorityLevel.Low), (Tr("Normale", "Normal"), IoPriorityLevel.Normal) };

    static readonly (string Label, MemoryPriorityLevel? Value)[] MemoryChoices =
    {
        (Tr("(inchangée)", "(unchanged)"), null), (Tr("Très basse", "Very low"), MemoryPriorityLevel.VeryLow), (Tr("Basse", "Low"), MemoryPriorityLevel.Low),
        (Tr("Moyenne", "Medium"), MemoryPriorityLevel.Medium), (Tr("Inférieure à la normale", "Below normal"), MemoryPriorityLevel.BelowNormal), (Tr("Normale", "Normal"), MemoryPriorityLevel.Normal),
    };

    void SetMask(long mask)
    {
        affinityOn.Checked = true;
        for (int i = 0; i < cpus.Items.Count; i++)
            cpus.SetItemChecked(i, ((mask >> i) & 1) == 1);
        UpdateHelp();
    }

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
        isGame.Checked = false;
        efficiency.SelectedIndex = ioPriority.SelectedIndex = memoryPriority.SelectedIndex = gpu.SelectedIndex = 0;
        keepAwake.Checked = false;
        block.SelectedIndex = 0;
        alertCpu.Value = alertMemory.Value = 0;
    }

    /// <summary>Sur un petit écran, la fenêtre défile au lieu de dépasser.</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.FromControl(this).WorkingArea;
        if (Height > area.Height)
        {
            AutoSize = false;
            AutoScroll = true;
            Size = new Size(Width + SystemInformation.VerticalScrollBarWidth, area.Height);
            Location = new Point(Location.X, area.Top);
        }
    }

    void UpdatePreview()
    {
        var text = pattern.Text.Trim();
        if (text.Length == 0)
        {
            preview.Text = Tr("Exemples : jeu.exe, chrome* (tout ce qui commence par chrome), jeu?.exe", "Examples: game.exe, chrome* (anything starting with chrome), game?.exe");
            return;
        }
        var matches = RuleMatcher.Preview(text, running);
        int system = matches.Count(n => Exclusions.IsProtected(n, int.MaxValue, 0));
        var usable = matches.Where(n => !Exclusions.IsProtected(n, int.MaxValue, 0))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() > 1 ? $"{g.Key} (×{g.Count()})" : g.Key)
            .ToList();
        preview.Text = usable.Count == 0 && system == 0
            ? Tr("Aucun processus en cours ne correspond : la règle s'appliquera à leur lancement.", "No running process matches: the rule will apply when they start.")
            : Tr($"S'applique à {matches.Count - system} processus en cours", $"Applies to {matches.Count - system} running process(es)") +
              (usable.Count > 0 ? Tr(" : ", ": ") + string.Join(", ", usable.Take(4)) + (usable.Count > 4 ? "…" : "") : "") +
              (system > 0 ? Tr($". {system} processus système ignoré(s).", $". {system} system process(es) ignored.") : ".");
    }

    void UpdateHelp()
    {
        var p = ((Choice<ProcessPriorityClass?>?)priority.SelectedItem)?.Value;
        priorityHelp.Text = p is { } v && PriorityHelp.TryGetValue(v, out var help) ? help : Tr("La priorité du programme n'est pas modifiée.", "The program's priority is not changed.");

        int checkedCount = cpus.CheckedIndices.Count;
        affinityHelp.Text = !affinityOn.Checked
            ? Tr($"Le programme peut utiliser les {cpuCount} cœurs.", $"The program can use all {cpuCount} cores.")
            : Tr($"Limité à {checkedCount} cœur(s) sur {cpuCount}. Utile pour un vieux jeu ou pour garder des cœurs libres.", $"Limited to {checkedCount} core(s) out of {cpuCount}. Useful for an old game or to keep cores free.");

        var chosen = (Choice<Guid?>?)plan.SelectedItem;
        planHelp.Text = chosen?.Value == null
            ? Tr("Le plan d'alimentation n'est pas changé.", "The power plan is not changed.")
            : Tr($"« {chosen.Label} » est activé tant que ce programme tourne, puis le plan d'origine revient.", $"“{chosen.Label}” is active while this program runs, then the original plan comes back.");

        efficiencyHelp.Text = efficiency.SelectedIndex switch
        {
            1 => Tr("Windows ralentit ce programme pour économiser l'énergie (comme le Mode efficacité du Gestionnaire des tâches). Idéal pour ce qui tourne en arrière-plan.", "Windows slows this program down to save power (like Task Manager's Efficiency mode). Ideal for what runs in the background."),
            2 => Tr("Windows ne le mettra jamais en mode efficacité, même réduit ou en arrière-plan.", "Windows will never put it in efficiency mode, even minimized or in the background."),
            _ => Tr("Windows décide seul (Windows 11 uniquement).", "Windows decides on its own (Windows 11 only)."),
        };
        ioHelp.Text = ioPriority.SelectedIndex switch
        {
            1 or 2 => Tr("Ses lectures et écritures passent après celles des autres programmes : une sauvegarde ne ralentit plus un jeu.", "Its reads and writes go after those of other programs: a backup no longer slows down a game."),
            3 => Tr("Accès au disque normal.", "Normal disk access."),
            _ => Tr("L'accès au disque n'est pas modifié.", "Disk access is not changed."),
        };
        blockHelp.Text = block.SelectedIndex switch
        {
            1 => Tr("Le programme est fermé dans la seconde qui suit son lancement, à chaque fois. Les processus déjà lancés sont fermés en enregistrant.", "The program is closed within a second of starting, every time. Processes already running are closed when you save."),
            2 => Tr("Si le programme est lancé une deuxième fois, la nouvelle copie est fermée. Ne convient pas aux navigateurs, qui lancent plusieurs processus.", "If the program is started a second time, the new copy is closed. Not suitable for browsers, which start several processes."),
            _ => Tr("Le programme peut se lancer normalement.", "The program can start normally."),
        };
        bool watching = alertCpu.Value > 0 || alertMemory.Value > 0;
        alertHelp.Text = !watching
            ? Tr("Pas de surveillance. Utile pour repérer un programme bloqué (CPU) ou une fuite de mémoire.", "No monitoring. Useful to spot a stuck program (CPU) or a memory leak.")
            : Tr($"Si le seuil est dépassé sans interruption pendant {alertMinutes.Value} min, Corral ", $"If the threshold is exceeded continuously for {alertMinutes.Value} min, Corral ") +
              (alertAction.SelectedIndex switch
              {
                  1 => Tr("baisse sa priorité au minimum", "lowers its priority to the minimum"),
                  2 => Tr("le ferme", "closes it"),
                  _ => Tr("vous prévient", "notifies you"),
              }) +
              Tr(", une seule fois jusqu'à ce qu'il repasse sous le seuil.", ", only once until it drops back below the threshold.");

        gpuHelp.Text = gpu.SelectedIndex switch
        {
            1 => Tr("Utilise la carte graphique la plus puissante (PC portable ou PC avec deux cartes). Prend effet au prochain lancement du programme.", "Uses the most powerful graphics card (laptop or PC with two cards). Takes effect the next time the program starts."),
            2 => Tr("Utilise la carte graphique économique, souvent intégrée au processeur. Prend effet au prochain lancement.", "Uses the power-saving graphics card, often built into the processor. Takes effect at next start."),
            3 => Tr("Retire tout choix : Windows décide. Prend effet au prochain lancement.", "Removes any choice: Windows decides. Takes effect at next start."),
            _ => Tr("Le choix de carte graphique n'est pas modifié.", "The graphics card choice is not changed."),
        };
        memoryHelp.Text = memoryPriority.SelectedIndex switch
        {
            1 or 2 or 3 or 4 => Tr("Quand la mémoire manque, ses données quittent la RAM avant celles des autres programmes.", "When memory runs low, its data leaves RAM before that of other programs."),
            5 => Tr("Priorité mémoire normale.", "Normal memory priority."),
            _ => Tr("La priorité mémoire n'est pas modifiée.", "Memory priority is not changed."),
        };
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
                idx = plan.Items.Add(new Choice<Guid?>(Tr($"(introuvable) {id}", $"(not found) {id}"), id));
            plan.SelectedIndex = idx;
        }

        cpuLimit.Value = Math.Clamp(rule.CpuLimitPercent ?? 0, 0, 99);
        memLimit.Value = Math.Clamp(rule.MemoryLimitMB ?? 0, 0, 1_048_576);
        isGame.Checked = rule.IsGame;
        efficiency.SelectedIndex = Math.Max(0, Array.FindIndex(EfficiencyChoices, c => c.Value == rule.EfficiencyMode));
        ioPriority.SelectedIndex = Math.Max(0, Array.FindIndex(IoChoices, c => c.Value == rule.IoPriority));
        memoryPriority.SelectedIndex = Math.Max(0, Array.FindIndex(MemoryChoices, c => c.Value == rule.MemoryPriority));
        keepAwake.Checked = rule.KeepAwake;
        block.SelectedIndex = (int)rule.Block;
        alertCpu.Value = Math.Clamp(rule.AlertCpuPercent ?? 0, 0, 100);
        alertMemory.Value = Math.Clamp(rule.AlertMemoryMB ?? 0, 0, 1_048_576);
        alertMinutes.Value = Math.Clamp(rule.AlertMinutes, 1, 120);
        alertAction.SelectedIndex = (int)rule.AlertAction;
        gpu.SelectedIndex = Math.Max(0, Array.IndexOf(GpuChoices, rule.GpuPreference));
    }

    void OnOk()
    {
        var name = pattern.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, Tr("Indiquez un nom de processus.", "Enter a process name."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                MessageBox.Show(this, Tr("Cochez au moins un CPU.", "Check at least one CPU."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
            IsGame = isGame.Checked,
            EfficiencyMode = EfficiencyChoices[Math.Max(0, efficiency.SelectedIndex)].Value,
            IoPriority = IoChoices[Math.Max(0, ioPriority.SelectedIndex)].Value,
            MemoryPriority = MemoryChoices[Math.Max(0, memoryPriority.SelectedIndex)].Value,
            KeepAwake = keepAwake.Checked,
            Block = (BlockMode)Math.Max(0, block.SelectedIndex),
            AlertCpuPercent = alertCpu.Value > 0 ? (int)alertCpu.Value : null,
            AlertMemoryMB = alertMemory.Value > 0 ? (int)alertMemory.Value : null,
            AlertMinutes = (int)alertMinutes.Value,
            AlertAction = (AlertAction)Math.Max(0, alertAction.SelectedIndex),
            GpuPreference = GpuChoices[Math.Max(0, gpu.SelectedIndex)],
        };

        // Garde-fous du blocage : jamais sur un motif qui viserait tout, confirmation si des programmes tournent
        if (Result.Block != BlockMode.None && Result.Enabled)
        {
            if (Engine.IsCatchAll(name))
            {
                MessageBox.Show(this, Tr("Ce motif viserait tous les programmes : le blocage est refusé.", "This pattern would target every program: blocking is refused."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var running = RuleMatcher.Preview(name, this.running).Where(n => !Exclusions.IsProtected(n, int.MaxValue, 0)).ToList();
            if (Result.Block == BlockMode.Always && running.Count > 0 &&
                MessageBox.Show(this, Tr($"{running.Count} processus en cours ({string.Join(", ", running.Distinct().Take(3))}) seront fermés dès l'enregistrement.\nContinuer ?",
                        $"{running.Count} running process(es) ({string.Join(", ", running.Distinct().Take(3))}) will be closed when you save.\nContinue?"),
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
        }
        if (Result.AlertAction == AlertAction.Close && Result.HasAlert && Result.Enabled && Engine.IsCatchAll(name))
        {
            MessageBox.Show(this, Tr("Fermer automatiquement tous les programmes : refusé. Choisissez un motif plus précis.", "Automatically closing every program: refused. Choose a more specific pattern."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.OK;
    }
}
