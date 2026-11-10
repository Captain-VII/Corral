using System.Diagnostics;
using Corral.Core;
using Corral.Models;

namespace Corral.UI;

// Page « Démarrage » : programmes lancés avec Windows.
public sealed partial class MainForm
{
    readonly BufferedListView startupList = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true };
    readonly ModernButton startupToggle = new(Tr("Désactiver", "Disable")) { Enabled = false };
    readonly ModernButton startupOpen = new(Tr("Ouvrir l'emplacement", "Open location")) { Enabled = false };
    readonly UndoBanner startupBanner = new();
    readonly StartupManager startup = StartupManager.ForSystem();

    Control BuildStartupPage()
    {
        startupList.Columns.Add(Tr("Programme", "Program"), 200);
        startupList.Columns.Add(Tr("État", "Status"), 126);
        startupList.Columns.Add(Tr("Éditeur", "Publisher"), 160);
        startupList.Columns.Add("Source", 190);
        startupList.Columns.Add(Tr("Commande", "Command"), 300);
        Theme.StyleList(startupList, (item, col) => item.Tag is not StartupItem s ? null : col switch
        {
            0 => new Theme.CellStyle(Icon: IconFor(s.Path)),
            1 => new Theme.CellStyle(Text: s.Enabled ? Tr("● Activé", "● Enabled") : Tr("○ Désactivé", "○ Disabled"), Fore: s.Enabled ? Theme.Current.Accent : Theme.Current.Muted),
            _ => null,
        });
        startupList.SelectedIndexChanged += (_, _) => UpdateStartupButtons();
        startupList.MouseClick += (_, e) =>
        {
            // Un clic sur l'état active ou désactive, comme pour les règles
            var hit = startupList.HitTest(e.Location);
            if (e.Button == MouseButtons.Left && hit.Item?.Tag is StartupItem s && hit.Item.SubItems.IndexOf(hit.SubItem) == 1)
                ToggleStartup(s);
        };
        startupList.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space && SelectedStartup is { } s) { ToggleStartup(s); e.Handled = true; }
        };
        startupToggle.Click += (_, _) => ToggleStartup(SelectedStartup);
        startupOpen.Click += (_, _) =>
        {
            if (SelectedStartup?.Path is not { } path)
                return;
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Ouverture de l'emplacement", ex); }
        };
        var refresh = new ModernButton(Tr("Actualiser", "Refresh"));
        refresh.Click += (_, _) => RefreshStartup();
        tips.SetToolTip(startupToggle, Tr("Empêcher ce programme de se lancer avec Windows, ou le réautoriser (Espace)", "Stop this program from starting with Windows, or allow it again (Space)"));
        tips.SetToolTip(startupOpen, Tr("Afficher le fichier dans l'Explorateur", "Show the file in Explorer"));
        tips.SetToolTip(refresh, Tr("Relire la liste (F5)", "Reload the list (F5)"));

        var body = new Panel();
        body.Controls.Add(new Card(startupList, fill: true) { Dock = DockStyle.Fill });
        body.Controls.Add(startupBanner);
        return MakePage(Tr("Démarrage", "Startup"), Tr("Programmes lancés à l'ouverture de votre session. Désactiver un programme l'empêche seulement de démarrer avec Windows : rien n'est supprimé, et vous pouvez le réactiver à tout moment.",
            "Programs launched when you sign in. Disabling a program only stops it from starting with Windows: nothing is deleted, and you can enable it again at any time."),
            body, refresh, startupOpen, startupToggle);
    }

    StartupItem? SelectedStartup => startupList.SelectedItems.Count == 1 ? startupList.SelectedItems[0].Tag as StartupItem : null;

    void UpdateStartupButtons()
    {
        var s = SelectedStartup;
        startupToggle.Enabled = s != null;
        startupToggle.Text = s?.Enabled == false ? Tr("Activer", "Enable") : Tr("Désactiver", "Disable");
        startupOpen.Enabled = s?.Path != null;
    }

    void RefreshStartup()
    {
        var selected = SelectedStartup;
        var items = startup.List();
        startupList.BeginUpdate();
        startupList.Items.Clear();
        foreach (var s in items)
        {
            var item = new ListViewItem(new[] { s.Name, "", s.Publisher ?? "", s.SourceLabel, s.Command }) { Tag = s, ToolTipText = s.Command };
            startupList.Items.Add(item);
            if (selected != null && s.Source == selected.Source && s.Entry == selected.Entry)
                item.Selected = true;
        }
        startupList.EndUpdate();
        Theme.FitColumns(startupList);
        UpdateStartupButtons();
    }

    void ToggleStartup(StartupItem? s)
    {
        if (s == null)
            return;
        try
        {
            startup.SetEnabled(s, !s.Enabled);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Tr("Impossible de modifier ce programme : ", "Unable to change this program: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        RefreshStartup();
        startupBanner.Show(s.Enabled
            ? Tr($"« {s.Name} » ne se lancera plus avec Windows.", $"“{s.Name}” will no longer start with Windows.")
            : Tr($"« {s.Name} » se lancera de nouveau avec Windows.", $"“{s.Name}” will start with Windows again."), () =>
        {
            try { startup.SetEnabled(s, s.Enabled); }
            catch (Exception ex) { Log.Error("Démarrage", ex); }
            RefreshStartup();
        });
    }
}
