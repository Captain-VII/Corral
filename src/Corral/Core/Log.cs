using System.Diagnostics;

namespace Corral.Core;

public enum LogLevel { Info, Warning, Error }

public enum LogCategory { General, Rule, ProBalance, Power, Update }

public sealed record LogEntry(DateTime Time, LogLevel Level, LogCategory Category, string Message);

/// <summary>Journal fichier (rotation à 1 Mo) et mémoire des 500 dernières entrées. Ne lève jamais d'exception.</summary>
public static class Log
{
    const int MaxRecent = 500;
    const long MaxFileSize = 1_000_000;
    static readonly object sync = new();
    static readonly LinkedList<LogEntry> recent = new();
    static string? path;
    static bool eventLog;

    const string EventSource = "Corral";

    /// <summary>Chaque nouvelle entrée (le service les relaie à l'interface). Levé hors verrou.</summary>
    public static event Action<LogEntry>? Written;

    /// <summary>
    /// Copie aussi les avertissements, les erreurs et les événements généraux dans l'Observateur d'événements
    /// (journal Application, source « Corral »), lisible par les outils de supervision. Nécessite les droits administrateur.
    /// </summary>
    public static void EnableEventLog()
    {
        try
        {
            if (!EventLog.SourceExists(EventSource))
                EventLog.CreateEventSource(EventSource, "Application");
            eventLog = true;
        }
        catch (Exception ex)
        {
            Warn("Observateur d'événements indisponible : " + ex.Message);
        }
    }

    /// <summary>Ajoute une entrée venue du service (mémoire seulement : le service a son propre fichier).</summary>
    public static void Append(LogEntry entry)
    {
        lock (sync)
        {
            recent.AddLast(entry);
            if (recent.Count > MaxRecent)
                recent.RemoveFirst();
        }
    }

    public static void Init(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            lock (sync) path = Path.Combine(directory, "corral.log");
        }
        catch { }
    }

    public static void Info(string message, LogCategory category = LogCategory.General) => Write(LogLevel.Info, category, message);
    public static void Warn(string message, LogCategory category = LogCategory.General) => Write(LogLevel.Warning, category, message);
    public static void Error(string message, Exception? ex = null) =>
        Write(LogLevel.Error, LogCategory.General, ex == null ? message : $"{message} : {ex}");

    public static LogEntry[] Recent()
    {
        lock (sync) return recent.ToArray();
    }

    static void Write(LogLevel level, LogCategory category, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, category, message);
        var tag = level switch { LogLevel.Warning => "WARN", LogLevel.Error => "ERREUR", _ => "INFO" };
        var line = $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{tag}] [{category}] {message}";
        lock (sync)
        {
            recent.AddLast(entry);
            if (recent.Count > MaxRecent)
                recent.RemoveFirst();
            if (path == null)
                return;
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxFileSize)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch { }
        }
        if (eventLog && (level != LogLevel.Info || category == LogCategory.General))
        {
            try
            {
                var type = level switch { LogLevel.Warning => EventLogEntryType.Warning, LogLevel.Error => EventLogEntryType.Error, _ => EventLogEntryType.Information };
                EventLog.WriteEntry(EventSource, message.Length > 30_000 ? message[..30_000] : message, type, 1000 + (int)category);
            }
            catch { }
        }
        try { Written?.Invoke(entry); } catch { }
    }
}
