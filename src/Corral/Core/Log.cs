namespace Corral.Core;

/// <summary>Journal fichier (rotation à 1 Mo) et mémoire des 500 dernières lignes. Ne lève jamais d'exception.</summary>
public static class Log
{
    const int MaxRecent = 500;
    const long MaxFileSize = 1_000_000;
    static readonly object sync = new();
    static readonly LinkedList<string> recent = new();
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

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERREUR", ex == null ? message : $"{message} : {ex}");

    public static string[] Recent()
    {
        lock (sync) return recent.ToArray();
    }

    static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (sync)
        {
            recent.AddLast(line);
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
