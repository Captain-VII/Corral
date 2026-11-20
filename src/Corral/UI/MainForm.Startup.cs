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
        startupList.Columns.Add("Impact", 90);
        startupList.Columns.Add(Tr("Mémoire", "Memory"), 90, HorizontalAlignment.Right);
        startupList.Columns.Add(Tr("Éditeur", "Publisher"), 160);
        startupList.Columns.Add("Source", 190);
        startupList.Columns.Add(Tr("Commande", "Command"), 300);
        Theme.StyleList(startupList, (item, col) => item.Tag is not StartupItem s ? null : col switch
        {
            0 => new Theme.CellStyle(Icon: IconFor(s.Path)),
            1 => new Theme.CellStyle(Text: s.Enabled ? Tr("● Activé", "● Enabled") : Tr("○ Désactivé", "○ Disabled"), Fore: s.Enabled ? Theme.Current.Accent : Theme.Current.Muted),
            2 when item.SubItems[2].Text == ImpactLabel(3) => new Theme.CellStyle(Fore: Theme.IsDark ? Color.FromArgb(240, 110, 100) : Color.FromArgb(196, 43, 28)),
            2 when item.SubItems[2].Text == ImpactLabel(2) => new Theme.CellStyle(Fore: Theme.Current.Warning),
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
        // Résumé en tête : combien de programmes, combien pèsent lourd
        var summary = new Card(startupSummary, Tr("En résumé", "Summary")) { Dock = DockStyle.Fill };
        var top = new Panel { Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 14) };
        top.Controls.Add(summary);
        body.Controls.Add(top);
        body.Resize += (_, _) => top.Height = summary.GetPreferredSize(new Size(body.ClientSize.Width, 0)).Height + 14;
        body.Controls.Add(startupBanner);
        return MakePage(Tr("Démarrage", "Startup"), Tr("Programmes lancés à l'ouverture de votre session. Désactiver un programme l'empêche seulement de démarrer avec Windows : rien n'est supprimé. L'impact est estimé d'après la mémoire utilisée en ce moment (élevé au-delà de 300 Mo).",
            "Programs launched when you sign in. Disabling a program only stops it from starting with Windows: nothing is deleted. Impact is estimated from the memory used right now (high above 300 MB)."),
            body, refresh, startupOpen, startupToggle);
    }

    StartupItem? SelectedStartup => startupList.SelectedItems.Count == 1 ? startupList.SelectedItems[0].Tag as StartupItem : null;

    readonly StatGrid startupSummary = new();

    /// <summary>Mémoire utilisée en ce moment par le programme (tous ses processus), ou null s'il ne tourne pas.</summary>
    long? StartupMemory(StartupItem s) =>
        s.Path == null || lastSnapshot?.Rows.Where(r => string.Equals(r.Path, s.Path, StringComparison.OrdinalIgnoreCase)).ToList() is not { Count: > 0 } rows
            ? null : rows.Sum(r => r.MemoryBytes);

    /// <summary>0 : ne tourne pas, 1 : faible, 2 : moyen, 3 : élevé.</summary>
    static int StartupImpact(long? memory) => memory switch
    {
        null => 0,
        >= 300L << 20 => 3,
        >= 100L << 20 => 2,
        _ => 1,
    };

    static string ImpactLabel(int impact) => impact switch
    {
        3 => Tr("Élevé", "High"),
        2 => Tr("Moyen", "Medium"),
        1 => Tr("Faible", "Low"),
        _ => Tr("Non lancé", "Not running"),
    };

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
        // Les plus lourds d'abord : ce qu'il vaut le mieux désactiver
        var memory = items.ToDictionary(s => s, StartupMemory);
        foreach (var s in items.OrderByDescending(s => memory[s] ?? -1))
        {
            var mem = memory[s];
            var item = new ListViewItem(new[] { s.Name, "", ImpactLabel(StartupImpact(mem)), mem is { } m ? FormatBytes(m) : "—", s.Publisher ?? "", s.SourceLabel, s.Command }) { Tag = s, ToolTipText = s.Command };
            startupList.Items.Add(item);
            if (selected != null && s.Source == selected.Source && s.Entry == selected.Entry)
                item.Selected = true;
        }
        startupList.EndUpdate();
        Theme.FitColumns(startupList);
        UpdateStartupButtons();
        int enabled = items.Count(s => s.Enabled), heavy = items.Count(s => s.Enabled && StartupImpact(memory[s]) == 3);
        double Share(int n) => items.Count == 0 ? 0 : (double)n / items.Count;
        startupSummary.SetItems(new List<(string, string, double?)>
        {
            (Tr("Au démarrage", "At startup"), Tr($"{enabled} sur {items.Count}", $"{enabled} of {items.Count}"), Share(enabled)),
            (Tr("Impact élevé", "High impact"), heavy.ToString(), Share(heavy)),
            (Tr("Désactivés", "Disabled"), (items.Count - enabled).ToString(), Share(items.Count - enabled)),
            (Tr("Mémoire utilisée", "Memory in use"), FormatBytes(memory.Values.Sum(m => m ?? 0)), null),
        });
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
