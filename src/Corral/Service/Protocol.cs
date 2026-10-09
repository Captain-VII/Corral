using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Text;
using System.Text.Json;
using Corral.Core;
using Corral.Models;

namespace Corral.Service;

/// <summary>État de la session de l'utilisateur, envoyé chaque seconde par l'interface (le service, en session 0, ne le voit pas).</summary>
public sealed record SessionState(int ForegroundPid, double IdleSeconds, bool DisplayRequired);

/// <summary>
/// Message échangé sur le pipe, une ligne JSON par message. Seuls les champs utiles au type sont remplis.
/// Interface → service : save, apply, pause, game, clean, priority, session, update.
/// Service → interface : welcome, snapshot, acted, game-changed, notify, log, result, updating.
/// </summary>
public sealed class PipeMessage
{
    public string Type { get; set; } = "";
    /// <summary>Numéro de requête, repris par la réponse « result ».</summary>
    public long Id { get; set; }
    public Settings? Settings { get; set; }
    public EngineSnapshot? Snapshot { get; set; }
    public LogEntry[]? Log { get; set; }
    public string[]? Names { get; set; }
    public double Value { get; set; }
    public bool Flag { get; set; }
    public string? Text { get; set; }
    public string? Text2 { get; set; }
    public int Pid { get; set; }
    public ProcessPriorityClass Priority { get; set; } = ProcessPriorityClass.Normal;
    public SessionState? Session { get; set; }
}

public static class Protocol
{
    public const string PipeName = "Corral";
    /// <summary>Taille maximale d'un message (une configuration fait quelques Ko, un instantané quelques dizaines).</summary>
    public const int MaxMessage = 8 << 20;

    static readonly JsonSerializerOptions options = new(RuleStore.Options) { WriteIndented = false };

    public static byte[] Encode(PipeMessage message) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, options) + "\n");

    public static PipeMessage? Decode(string line)
    {
        try { return JsonSerializer.Deserialize<PipeMessage>(line, options); }
        catch (JsonException) { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientSessionId(SafeHandle pipe, out uint sessionId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll")]
    static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafePipeHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WaitNamedPipe(string name, uint timeout);

    const uint OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;
    // Le service peut lire l'identité du client (pour sa ruche de registre), pas agir en son nom
    const uint SECURITY_SQOS_PRESENT = 0x00100000, SECURITY_IDENTIFICATION = 0x00010000;
    const int ERROR_PIPE_BUSY = 231;

    /// <summary>
    /// Ouvre le pipe avec exactement <paramref name="rights"/> (NamedPipeClientStream demanderait une écriture « générique »).
    /// Null si le pipe n'existe pas ou reste occupé.
    /// </summary>
    public static NamedPipeClientStream? OpenClient(string pipeName, PipeAccessRights rights, int timeoutMs)
    {
        var path = @"\\.\pipe\" + pipeName;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var handle = CreateFile(path, (uint)rights, 0, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, IntPtr.Zero);
            if (!handle.IsInvalid)
                return new NamedPipeClientStream(PipeDirection.InOut, isAsync: true, isConnected: true, handle);
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error != ERROR_PIPE_BUSY || !WaitNamedPipe(path, (uint)timeoutMs))
                return null;
        }
        return null;
    }

    public static int ClientSession(SafeHandle pipe) => GetNamedPipeClientSessionId(pipe, out var s) ? (int)s : -1;

    /// <summary>Le pipe est-il servi par un service (session 0) ? Évite qu'un autre programme se fasse passer pour Corral.</summary>
    public static bool ServedByService(SafeHandle pipe) =>
        GetNamedPipeServerProcessId(pipe, out var pid) && ProcessIdToSessionId(pid, out var session) && session == 0;

    public static int ConsoleSession => (int)WTSGetActiveConsoleSessionId();
}

/// <summary>Lecture ligne par ligne d'un flux, avec une taille maximale.</summary>
public sealed class LineReader
{
    readonly Stream stream;
    readonly byte[] buffer = new byte[64 * 1024];
    readonly MemoryStream pending = new();
    int start, end;

    public LineReader(Stream stream) => this.stream = stream;

    /// <summary>Ligne suivante, ou null en fin de flux.</summary>
    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            int nl = Array.IndexOf(buffer, (byte)'\n', start, end - start);
            if (nl >= 0)
            {
                pending.Write(buffer, start, nl - start);
                start = nl + 1;
                var line = Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)pending.Length);
                pending.SetLength(0);
                return line;
            }
            pending.Write(buffer, start, end - start);
            start = end = 0;
            if (pending.Length > Protocol.MaxMessage)
                throw new InvalidDataException("message trop long");
            int n = await stream.ReadAsync(buffer, ct);
            if (n == 0)
                return null;
            end = n;
        }
    }
}
