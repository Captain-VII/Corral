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
    }
}
