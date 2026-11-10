using Corral.Core;
using Corral.Models;

namespace Corral.UI;

// Page « Règles » : liste des règles, profils, import et export.
public sealed partial class MainForm
{
    readonly BufferedListView ruleList = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false };
    readonly Label rulesEmpty = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        Tag = Theme.HintTag,
        Text = Tr("Aucune règle pour l'instant.\nCliquez sur « Ajouter une règle », ou double-cliquez sur un processus.",
            "No rules yet.\nClick “Add a rule”, or double-click a process."),
    };
    readonly ModernButton editRule = new(Tr("Modifier", "Edit")) { Enabled = false };
    readonly ModernButton deleteRule = new(Tr("Supprimer", "Delete")) { Enabled = false };
    readonly ModernButton moveUp = new("↑") { Enabled = false };
    readonly ModernButton moveDown = new("↓") { Enabled = false };
    readonly UndoBanner rulesBanner = new();

    Control BuildRulesPage()
    {
        ruleList.Columns.Add(Tr("Processus", "Process"), 170);
        ruleList.Columns.Add(Tr("État", "Status"), 90);
        ruleList.Columns.Add(Tr("Priorité", "Priority"), 170);
        ruleList.Columns.Add(Tr("Cœurs", "Cores"), 80);
        ruleList.Columns.Add(Tr("Autres réglages", "Other settings"), 330);
        Theme.StyleList(ruleList, (item, col) => col == 1
            ? new Theme.CellStyle(Text: item.SubItems[1].Text == "Inactive" ? "○ Inactive" : "● Active",
                Fore: item.SubItems[1].Text == "Inactive" ? Theme.Current.Muted : Theme.Current.Accent)
            : null);
        ruleList.DoubleClick += (_, _) => EditRule();
        ruleList.SelectedIndexChanged += (_, _) => UpdateRuleButtons();
        ruleList.MouseClick += (_, e) =>
        {
            // Un clic sur l'état active ou désactive la règle
            var hit = ruleList.HitTest(e.Location);
            if (e.Button == MouseButtons.Left && hit.Item != null && hit.Item.SubItems.IndexOf(hit.SubItem) == 1)
                ToggleRule(hit.Item.Index);
        };
        ruleList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete) { DeleteRule(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { EditRule(); e.Handled = true; }
            else if (e.KeyCode == Keys.Space && SelectedRule >= 0) { ToggleRule(SelectedRule); e.Handled = true; }
        };
        ruleList.ContextMenuStrip = BuildRuleMenu();

        editRule.Click += (_, _) => EditRule();
        deleteRule.Click += (_, _) => DeleteRule();
        moveUp.Click += (_, _) => MoveRule(-1);
        moveDown.Click += (_, _) => MoveRule(1);
        var add = new ModernButton(Tr("Ajouter une règle", "Add a rule"), primary: true);
        add.Click += (_, _) => AddRule(null);
        var more = new ModernButton("⋯");
        var moreMenu = new ContextMenuStrip();
        moreMenu.Items.Add(Tr("Importer des règles…", "Import rules…"), null, (_, _) => ImportRules());
        moreMenu.Items.Add(Tr("Exporter les règles…", "Export rules…"), null, (_, _) => ExportRules());
        profileButton.Text = ProfileButtonText();
        profileButton.Click += (_, _) => ShowProfileMenu();
        tips.SetToolTip(profileButton, Tr("Profils : plusieurs jeux de règles (Travail, Jeu, Silencieux…) entre lesquels basculer en un clic, ici ou depuis l'icône de notification.",
            "Profiles: several rule sets (Work, Gaming, Quiet…) you can switch between in one click, here or from the notification icon."));
        more.ContextMenuStrip = moreMenu; // ainsi le thème lui est appliqué avec le reste
        more.Click += (_, _) => moreMenu.Show(more, new Point(0, more.Height + 2));

        tips.SetToolTip(add, Tr("Nouvelle règle (Ctrl+N)", "New rule (Ctrl+N)"));
        tips.SetToolTip(editRule, Tr("Modifier la règle sélectionnée (Entrée ou double-clic)", "Edit the selected rule (Enter or double-click)"));
        tips.SetToolTip(deleteRule, Tr("Supprimer la règle sélectionnée (Suppr). Vous pourrez annuler.", "Delete the selected rule (Del). You can undo."));
        tips.SetToolTip(moveUp, Tr("Monter : la première règle qui correspond l'emporte", "Move up: the first matching rule wins"));
        tips.SetToolTip(moveDown, Tr("Descendre", "Move down"));
        tips.SetToolTip(more, Tr("Importer ou exporter les règles", "Import or export rules"));

        var host = new Panel();
        host.Controls.Add(ruleList);
        host.Controls.Add(rulesEmpty);
        ruleList.Dock = DockStyle.Fill;
        var body = new Panel();
        body.Controls.Add(new Card(host, fill: true) { Dock = DockStyle.Fill });
        body.Controls.Add(rulesBanner);

        return MakePage(Tr("Règles", "Rules"), Tr("La première règle active qui correspond s'applique, y compris aux processus déjà lancés. Cliquez sur l'état pour activer ou désactiver une règle.",
                "The first matching active rule applies, including to processes already running. Click the status to enable or disable a rule."),
            body, profileButton, moveUp, moveDown, editRule, deleteRule, more, add);
    }

    // ---------- Profils ----------

    readonly ModernButton profileButton = new("");

    /// <summary>Le profil actif a changé (l'icône de notification met son menu à jour).</summary>
    public event Action? ProfileChanged;

    string ProfileButtonText() => Tr($"Profil : {settings.ActiveProfile} ▾", $"Profile: {settings.ActiveProfile} ▾");

    void ShowProfileMenu()
    {
        var menu = new ContextMenuStrip();
        foreach (var p in settings.Profiles)
        {
            var name = p.Name;
            var item = new ToolStripMenuItem(name) { Checked = name == settings.ActiveProfile };
            item.Click += (_, _) => SwitchProfile(name);
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Tr("Nouveau profil…", "New profile…"), null, (_, _) => NewProfile());
        menu.Items.Add(Tr("Renommer ce profil…", "Rename this profile…"), null, (_, _) => RenameProfile());
        var delete = new ToolStripMenuItem(Tr("Supprimer un profil", "Delete a profile")) { Enabled = settings.Profiles.Count > 1 };
        foreach (var p in settings.Profiles.Where(p => p.Name != settings.ActiveProfile))
        {
            var name = p.Name;
            delete.DropDownItems.Add(name, null, (_, _) => DeleteProfile(name));
        }
        menu.Items.Add(delete);
        Theme.ApplyTo(menu);
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(profileButton, new Point(0, profileButton.Height + 2));
    }

    /// <summary>Active un profil : ses règles remplacent les règles actuelles (rangées dans l'ancien profil).</summary>
    public void SwitchProfile(string name)
    {
        if (!Profiles.Switch(settings, name))
            return;
        SaveAndApply();
        profileButton.Text = ProfileButtonText();
        Log.Info(Tr($"Profil « {settings.ActiveProfile} » activé", $"Profile “{settings.ActiveProfile}” activated"), LogCategory.Rule);
        rulesBanner.Show(Tr($"Profil « {settings.ActiveProfile} » actif : {settings.Rules.Count} règle(s).", $"Profile “{settings.ActiveProfile}” active: {settings.Rules.Count} rule(s)."));
        ProfileChanged?.Invoke();
    }

    void NewProfile()
    {
        var name = InputBox.Ask(this, Tr("Nouveau profil", "New profile"), Tr("Nom du profil (par exemple Travail, Jeu, Silencieux) :", "Profile name (for example Work, Gaming, Quiet):"), "");
        if (name == null)
            return;
        if (Profiles.Find(settings, name) != null || name.Trim().Length == 0)
        {
            MessageBox.Show(this, Tr("Ce nom est vide ou déjà utilisé.", "This name is empty or already used."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var copy = settings.Rules.Count > 0 && MessageBox.Show(this,
            Tr("Copier les règles actuelles dans le nouveau profil ?\n\nNon : il commence vide.", "Copy the current rules into the new profile?\n\nNo: it starts empty."),
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        var created = Profiles.Create(settings, name, copy);
        if (created != null)
            SwitchProfile(created.Name);
    }

    void RenameProfile()
    {
        var old = settings.ActiveProfile;
        var name = InputBox.Ask(this, Tr("Renommer le profil", "Rename profile"), Tr("Nouveau nom :", "New name:"), old);
        if (name == null || name.Trim() == old)
            return;
        if (!Profiles.Rename(settings, old, name))
        {
            MessageBox.Show(this, Tr("Ce nom est vide ou déjà utilisé.", "This name is empty or already used."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        SaveSettings();
        profileButton.Text = ProfileButtonText();
        ProfileChanged?.Invoke();
    }

    void DeleteProfile(string name)
    {
        var p = Profiles.Find(settings, name);
        if (p == null || MessageBox.Show(this, Tr($"Supprimer le profil « {name} » et ses {p.Rules.Count} règle(s) ?", $"Delete profile “{name}” and its {p.Rules.Count} rule(s)?"),
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        Profiles.Delete(settings, name);
        SaveSettings();
        ProfileChanged?.Invoke();
    }

    ContextMenuStrip BuildRuleMenu()
    {
        var menu = new ContextMenuStrip();
        var edit = new ToolStripMenuItem(Tr("Modifier…", "Edit…"), null, (_, _) => EditRule());
        var duplicate = new ToolStripMenuItem(Tr("Dupliquer", "Duplicate"), null, (_, _) => DuplicateRule());
        var toggle = new ToolStripMenuItem(Tr("Désactiver", "Disable"), null, (_, _) => ToggleRule(SelectedRule));
        var delete = new ToolStripMenuItem(Tr("Supprimer", "Delete"), null, (_, _) => DeleteRule());
        menu.Items.AddRange(new ToolStripItem[] { edit, duplicate, toggle, new ToolStripSeparator(), delete });
        menu.Opening += (_, e) =>
        {
            int i = SelectedRule;
            if (i < 0)
            {
                e.Cancel = true;
                return;
            }
            toggle.Text = settings.Rules[i].Enabled ? Tr("Désactiver", "Disable") : Tr("Activer", "Enable");
        };
        Theme.ApplyTo(menu);
        return menu;
    }

    void UpdateRuleButtons()
    {
        int i = SelectedRule;
        editRule.Enabled = deleteRule.Enabled = i >= 0;
        moveUp.Enabled = i > 0;
        moveDown.Enabled = i >= 0 && i < settings.Rules.Count - 1;
    }

    void RefreshRules(int select = -1)
    {
        ruleList.BeginUpdate();
        ruleList.Items.Clear();
        foreach (var r in settings.Rules)
        {
            ruleList.Items.Add(new ListViewItem(new[]
            {
                r.Pattern,
                r.Enabled ? "Active" : "Inactive",
                r.Priority is { } prio ? RuleText.PriorityLabel(prio) : "—",
                r.AffinityMask is { } m ? $"{System.Numerics.BitOperations.PopCount((ulong)m)}" : Tr("Tous", "All"),
                RuleOptions(r),
            }));
        }
        if (select >= 0 && select < ruleList.Items.Count)
        {
            ruleList.Items[select].Selected = true;
            ruleList.Items[select].EnsureVisible();
        }
        ruleList.EndUpdate();
        bool empty = settings.Rules.Count == 0;
        ruleList.Visible = !empty;
        rulesEmpty.Visible = empty;
        Theme.FitColumns(ruleList);
        UpdateRuleButtons();
        UpdateGameList();
    }

    string PlanName(Guid id) =>
        (plans ??= PowerCfg.List()).FirstOrDefault(p => p.Id == id)?.Name ?? id.ToString();

    int SelectedRule => ruleList.SelectedIndices.Count == 1 ? ruleList.SelectedIndices[0] : -1;

    void AddRule(Rule? template)
    {
        plans = PowerCfg.List();
        using var dlg = new RuleDialog(template, plans, isNew: true, RunningNames());
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        settings.Rules.Add(dlg.Result);
        SaveAndApply(settings.Rules.Count - 1);
        ShowPage("Règles");
    }

    void EditRule()
    {
        int i = SelectedRule;
        if (i < 0)
            return;
        plans = PowerCfg.List();
        using var dlg = new RuleDialog(settings.Rules[i], plans, isNew: false, RunningNames());
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        settings.Rules[i] = dlg.Result;
        SaveAndApply(i);
    }

    void DuplicateRule()
    {
        int i = SelectedRule;
        if (i < 0)
            return;
        var copy = RuleStore.Clone(new Settings { Rules = { settings.Rules[i] } }).Rules[0];
        settings.Rules.Insert(i + 1, copy);
        SaveAndApply(i + 1);
    }

    void ToggleRule(int i)
    {
        if (i < 0 || i >= settings.Rules.Count)
            return;
        settings.Rules[i].Enabled = !settings.Rules[i].Enabled;
        SaveAndApply(i);
    }

    /// <summary>Suppression immédiate, avec « Annuler » pendant quelques secondes plutôt qu'une confirmation.</summary>
    void DeleteRule()
    {
        int i = SelectedRule;
        if (i < 0)
            return;
        var removed = settings.Rules[i];
        settings.Rules.RemoveAt(i);
        SaveAndApply(Math.Min(i, settings.Rules.Count - 1));
        rulesBanner.Show(Tr($"Règle « {removed.Pattern} » supprimée.", $"Rule “{removed.Pattern}” deleted."), () =>
        {
            settings.Rules.Insert(Math.Min(i, settings.Rules.Count), removed);
            SaveAndApply(i);
        });
    }

    void MoveRule(int delta)
    {
        int i = SelectedRule, j = i + delta;
        if (i < 0 || j < 0 || j >= settings.Rules.Count)
            return;
        (settings.Rules[i], settings.Rules[j]) = (settings.Rules[j], settings.Rules[i]);
        SaveAndApply(j);
    }

    void ExportRules()
    {
        if (settings.Rules.Count == 0)
        {
            MessageBox.Show(this, Tr("Il n'y a aucune règle à exporter.", "There are no rules to export."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var dlg = new SaveFileDialog { Filter = Tr("Règles Corral (*.json)|*.json", "Corral rules (*.json)|*.json"), FileName = Tr("regles-corral.json", "corral-rules.json") };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        try
        {
            RuleStore.ExportRules(settings.Rules, dlg.FileName);
            rulesBanner.Show(Tr($"{settings.Rules.Count} règle(s) exportée(s).", $"{settings.Rules.Count} rule(s) exported."));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Tr("Export impossible : ", "Export failed: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ImportRules()
    {
        using var dlg = new OpenFileDialog { Filter = Tr("Règles Corral (*.json)|*.json|Tous les fichiers|*.*", "Corral rules (*.json)|*.json|All files|*.*") };
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;
        List<Rule> imported;
        try
        {
            imported = RuleStore.ImportRules(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (imported.Count == 0)
        {
            MessageBox.Show(this, Tr("Ce fichier ne contient aucune règle utilisable.", "This file contains no usable rule."), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        bool replace = false;
        if (settings.Rules.Count > 0)
        {
            var answer = MessageBox.Show(this,
                Tr($"{imported.Count} règle(s) trouvée(s).\n\nOui : les ajouter aux règles actuelles.\nNon : remplacer les règles actuelles.",
                    $"{imported.Count} rule(s) found.\n\nYes: add them to the current rules.\nNo: replace the current rules."),
                Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel)
                return;
            replace = answer == DialogResult.No;
        }
        var before = settings.Rules.ToList();
        if (replace)
            settings.Rules.Clear();
        settings.Rules.AddRange(imported);
        SaveAndApply();
        rulesBanner.Show(Tr($"{imported.Count} règle(s) importée(s).", $"{imported.Count} rule(s) imported."), () =>
        {
            settings.Rules.Clear();
            settings.Rules.AddRange(before);
            SaveAndApply();
        });
    }

    /// <summary>Règles et profils modifiés ailleurs (ligne de commande) : déjà appliqués par le service, on les affiche.</summary>
    public void ReloadRules(Settings incoming)
    {
        settings.Rules = incoming.Rules;
        settings.Profiles = incoming.Profiles;
        settings.ActiveProfile = incoming.ActiveProfile;
        RefreshRules();
    }

    string RuleOptions(Rule r)
    {
        var parts = new List<string>();
        if (r.IsGame) parts.Add(Tr("Jeu", "Game"));
        if (r.GpuPreference is { } gpu) parts.Add("GPU " + GpuPreferences.Label(gpu).ToLowerInvariant());
        if (r.PowerPlan is { } g) parts.Add(Tr("Plan ", "Plan ") + PlanName(g));
        if (r.CpuLimitPercent is { } c) parts.Add($"CPU max {c} %");
        if (r.MemoryLimitMB is { } mem) parts.Add($"RAM max {mem} {Units.MB}");
        if (r.EfficiencyMode == true) parts.Add(Tr("Efficacité", "Efficiency"));
        if (r.EfficiencyMode == false) parts.Add(Tr("Jamais efficacité", "Never efficiency"));
        if (r.IoPriority is { } io) parts.Add(Tr("Disque ", "Disk ") + RuleText.IoLabel(io).ToLowerInvariant());
        if (r.MemoryPriority is { } m) parts.Add(Tr("Mémoire ", "Memory ") + RuleText.MemoryLabel(m).ToLowerInvariant());
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }
}
