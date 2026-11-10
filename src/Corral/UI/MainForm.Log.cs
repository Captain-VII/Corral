using Corral.Core;
using Corral.Models;

namespace Corral.UI;

// Page « Journal ».
public sealed partial class MainForm
{
    readonly BufferedListView logList = new() { View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true };
    readonly List<(ModernButton Button, Func<LogEntry, bool> Filter)> logFilters = new();
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
            (Tr("Erreurs", "Errors"), e => e.Level != LogLevel.Info),
        };
        foreach (var (label, filter) in filters)
        {
            var b = new ModernButton(label) { Toggled = logFilters.Count == 0 };
            b.Click += (_, _) =>
            {
                foreach (var (other, _) in logFilters)
                    other.Toggled = other == b;
                logFilter = filter;
                RefreshLog(force: true);
            };
            logFilters.Add((b, filter));
        }
        var open = new ModernButton(Tr("Ouvrir le dossier", "Open folder"));
        open.Click += (_, _) => OpenConfigFolder();
        tips.SetToolTip(open, Tr("Le journal complet est enregistré dans corral.log", "The full log is saved in corral.log"));

        var actions = logFilters.Select(f => (Control)f.Button).Append(open).ToArray();
        return MakePage(Tr("Journal", "Log"), Tr("Ce que Corral a fait : règles appliquées, interventions de ProBalance, erreurs. Les plus récents en haut.", "What Corral did: rules applied, ProBalance actions, errors. Most recent first."),
            new Card(logList, fill: true), actions);
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
        logList.BeginUpdate();
        logList.Items.Clear();
        foreach (var e in entries.Reverse().Where(logFilter))
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
