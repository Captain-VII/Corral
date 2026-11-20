using Corral.Core;
using Corral.Models;

namespace Corral.UI;

// Page « Journal ».
public sealed partial class MainForm
{
    readonly BufferedListView logList = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true };
    readonly List<(ModernButton Button, string Label, Func<LogEntry, bool> Filter)> logFilters = new();
    readonly TextBox logSearch = new() { PlaceholderText = Tr("Rechercher dans le journal", "Search the log"), Width = 220, Margin = new Padding(4, 5, 8, 0), AccessibleName = Tr("Rechercher dans le journal", "Search the log") };
    readonly HourBars logHours = new() { Height = 70, Cursor = Cursors.Hand, EmptyText = Tr("Rien ces dernières 24 heures.", "Nothing in the last 24 hours.") };
    Func<LogEntry, bool> logFilter = _ => true;
    LogEntry? lastLogShown;

    Control BuildLogPage()
    {
        logList.Columns.Add(Tr("Heure", "Time"), 90);
        logList.Columns.Add("Type", 130);
        logList.Columns.Add("Message", 640);
        Theme.StyleList(logList, (item, col) =>
            col == 1 && item.Tag is LogEntry entry ? new Theme.CellStyle(Text: "● " + item.SubItems[1].Text, Fore: LogColor(entry)) : null);

        var filters = new (string Label, Func<LogEntry, bool> Filter)[]
        {
            (Tr("Tout", "All"), _ => true),
            (Tr("Règles", "Rules"), e => e.Category == LogCategory.Rule),
            ("ProBalance", e => e.Category == LogCategory.ProBalance),
            (Tr("Alimentation", "Power"), e => e.Category == LogCategory.Power),
            (Tr("Erreurs", "Errors"), e => e.Level != LogLevel.Info),
        };
        foreach (var (label, filter) in filters)
        {
            var b = new ModernButton(label) { Toggled = logFilters.Count == 0 };
            b.Click += (_, _) =>
            {
                foreach (var (other, _, _) in logFilters)
                    other.Toggled = other == b;
                logFilter = filter;
                RefreshLog(force: true);
            };
            logFilters.Add((b, label, filter));
        }
        logSearch.TextChanged += (_, _) => RefreshLog(force: true);
        // Un clic sur une heure n'affiche qu'elle ; un second clic sur la même rend tout
        logHours.BarClicked += i =>
        {
            logHours.Highlight = logHours.Highlight == i ? -1 : i;
            RefreshLog(force: true);
        };
        tips.SetToolTip(logHours, Tr("Événements par heure. Cliquez sur une heure pour ne voir qu'elle.", "Events per hour. Click an hour to see only that one."));
        var open = new ModernButton(Tr("Ouvrir le dossier", "Open folder"));
        open.Click += (_, _) => OpenConfigFolder();
        tips.SetToolTip(open, Tr("Le journal complet est enregistré dans corral.log", "The full log is saved in corral.log"));

        var body = new Panel();
        body.Controls.Add(new Card(logList, fill: true) { Dock = DockStyle.Fill });
        var hours = new Panel { Dock = DockStyle.Top, Height = 70 + 36 + 14 + 32, Padding = new Padding(0, 0, 0, 14) };
        hours.Controls.Add(new Card(logHours, Tr("Événements par heure", "Events per hour")) { Dock = DockStyle.Fill });
        body.Controls.Add(hours);
        var actions = logFilters.Select(f => (Control)f.Button).Prepend(logSearch).Append(open).ToArray();
        return MakePage(Tr("Journal", "Log"), Tr("Ce que Corral a fait : règles appliquées, interventions de ProBalance, erreurs. Les plus récents en haut.", "What Corral did: rules applied, ProBalance actions, errors. Most recent first."),
            body, actions);
    }

    /// <summary>Rang de l'heure d'un événement dans la frise (0 = il y a 23 h, 23 = l'heure en cours), ou -1 s'il est plus ancien.</summary>
    static int HourSlot(DateTime time, DateTime now)
    {
        int ago = (int)(now.Date.AddHours(now.Hour) - time.Date.AddHours(time.Hour)).TotalHours;
        return ago is >= 0 and < 24 ? 23 - ago : -1;
    }

    static string CategoryLabel(LogEntry e) => e.Level switch
    {
        LogLevel.Error => Tr("Erreur", "Error"),
        LogLevel.Warning when e.Category == LogCategory.General => Tr("Avertissement", "Warning"),
        _ => e.Category switch
        {
            LogCategory.Rule => Tr("Règle", "Rule"),
            LogCategory.ProBalance => "ProBalance",
            LogCategory.Power => Tr("Alimentation", "Power"),
            LogCategory.Update => Tr("Mise à jour", "Update"),
            _ => Tr("Général", "General"),
        },
    };

    static Color LogColor(LogEntry e)
    {
        var p = Theme.Current;
        if (e.Level == LogLevel.Error)
            return Theme.IsDark ? Color.FromArgb(240, 110, 100) : Color.FromArgb(196, 43, 28);
        if (e.Level == LogLevel.Warning || e.Category == LogCategory.ProBalance)
            return p.Warning;
        return e.Category is LogCategory.Rule or LogCategory.Update ? p.Accent : p.Muted;
    }

    /// <summary>Recharge le journal ; sans <paramref name="force"/>, seulement s'il y a du nouveau.</summary>
    void RefreshLog(bool force)
    {
        var entries = Log.Recent();
        var newest = entries.Length > 0 ? entries[^1] : null;
        if (!force && ReferenceEquals(newest, lastLogShown))
            return;
        lastLogShown = newest;
        var now = DateTime.Now;
        var perHour = new int[24];
        foreach (var e in entries)
            if (HourSlot(e.Time, now) is >= 0 and var slot)
                perHour[slot]++;
        logHours.SetValues(perHour);
        foreach (var (button, label, filter) in logFilters)
            button.Text = $"{label} ({entries.Count(filter)})";
        var search = logSearch.Text.Trim();
        int hour = logHours.Highlight;
        logList.BeginUpdate();
        logList.Items.Clear();
        foreach (var e in entries.Reverse().Where(logFilter)
                     .Where(e => search.Length == 0 || e.Message.Contains(search, StringComparison.OrdinalIgnoreCase))
                     .Where(e => hour < 0 || HourSlot(e.Time, now) == hour))
        {
            logList.Items.Add(new ListViewItem(new[] { e.Time.ToString("HH:mm:ss"), CategoryLabel(e), e.Message })
            {
                Tag = e,
                ToolTipText = $"{e.Time:dd/MM HH:mm:ss}\n{e.Message}",
            });
        }
        logList.EndUpdate();
        Theme.FitColumns(logList);
    }
}
