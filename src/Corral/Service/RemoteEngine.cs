using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Microsoft.Win32;
using Corral.Core;
using Corral.Models;

namespace Corral.Service;

/// <summary>
/// Le moteur vu depuis l'interface quand le service est installé : chaque commande part sur le pipe,
/// instantanés et notifications reviennent par le même chemin. Se reconnecte seul si le service redémarre.
/// </summary>
public sealed class RemoteEngine : IEngine, ISettingsStore, IDisposable
{
    readonly string pipeName;
    readonly bool requireService;
    readonly CancellationTokenSource cts = new();
    readonly ConcurrentDictionary<long, TaskCompletionSource<string?>> pending = new();
    readonly object writeLock = new();
    NamedPipeClientStream? pipe;
    LineReader? reader;
    System.Threading.Timer? sessionTimer;
    long nextId;
    volatile bool gameManual;
    volatile string? keepAwake;
    bool started;

    RemoteEngine(string pipeName, bool requireService)
    {
        this.pipeName = pipeName;
        this.requireService = requireService;
    }

    /// <summary>Le service Corral est-il installé sur ce PC ?</summary>
    public static bool ServiceInstalled
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + CorralService.Name);
                return key != null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Se connecte au service (en réessayant jusqu'à <paramref name="timeout"/>), ou null.</summary>
    /// <param name="requireService">Refuse un pipe qui ne serait pas servi depuis la session 0 (les tests le désactivent).</param>
    public static RemoteEngine? Connect(TimeSpan timeout, string pipeName = Protocol.PipeName, bool requireService = true)
    {
        var remote = new RemoteEngine(pipeName, requireService);
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (remote.TryOpen() is { } welcome)
            {
                remote.InitialSettings = welcome.Settings ?? new Settings();
                remote.InitialSettings.Normalize();
                remote.Fresh = welcome.Flag;
                foreach (var entry in welcome.Log ?? Array.Empty<LogEntry>())
                    Log.Append(entry);
                return remote;
            }
            Thread.Sleep(500);
        } while (DateTime.UtcNow < deadline);
        remote.Dispose();
        return null;
    }

    /// <summary>Configuration tenue par le service au moment de la connexion.</summary>
    public Settings InitialSettings { get; private set; } = new();

    /// <summary>Le service n'avait encore aucune configuration (premier démarrage après l'installation).</summary>
    public bool Fresh { get; private set; }

    public bool Connected { get; private set; }

    public event Action<EngineSnapshot>? SnapshotReady;
    public event Action<IReadOnlyList<string>, double>? ProBalanceActed;
    public event Action<bool, string?>? GameModeChanged;
    public event Action<string, string>? Notification;
    /// <summary>Le service va installer une mise à jour : l'interface doit se fermer.</summary>
    public event Action? ServiceUpdating;
    public event Action<bool>? ConnectionChanged;

    public string? KeepAwakeReason => keepAwake;
    public bool GameModeManual => gameManual;

    /// <summary>Statistiques et journal de l'interface : dans le profil de l'utilisateur.</summary>
    public string ConfigDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Corral");

    /// <summary>
    /// Droits demandés sur le pipe. Une écriture « générique » demanderait aussi le droit de créer une instance du pipe,
    /// que seuls SYSTEM et les administrateurs ont (sinon un utilisateur pourrait se faire passer pour le service).
    /// </summary>
    public const PipeAccessRights ClientRights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;

    PipeMessage? TryOpen()
    {
        if (Protocol.OpenClient(pipeName, ClientRights, 1000) is not { } p)
            return null;
        try
        {
            if (requireService && !Protocol.ServedByService(p.SafePipeHandle))
            {
                Log.Warn(Tr("Le pipe Corral n'est pas servi par le service : connexion refusée", "The Corral pipe is not served by the service: connection refused"));
                p.Dispose();
                return null;
            }
            var r = new LineReader(p);
            var read = r.ReadAsync(cts.Token);
            if (!read.Wait(TimeSpan.FromSeconds(5)) || read.Result is not { } line || Protocol.Decode(line) is not { Type: "welcome" } welcome)
                throw new TimeoutException();
            lock (writeLock)
            {
                pipe?.Dispose(); // liaison prÃ©cÃ©dente, coupÃ©e
                pipe = p;
                reader = r;
            }
            Connected = true;
            return welcome;
        }
        catch
        {
            p.Dispose();
            return null;
        }
    }

    public void Start()
    {
        if (started)
            return;
        started = true;
        _ = Task.Run(ReadLoop);
        sessionTimer = new System.Threading.Timer(_ => SendSession(), null, 0, 1000);
    }

    async Task ReadLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                string? line;
                while ((line = await reader!.ReadAsync(cts.Token)) != null)
                {
                    if (Protocol.Decode(line) is { } m)
                        Dispatch(m);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warn(Tr("Liaison avec le service interrompue : ", "Link with the service lost: ") + ex.Message);
            }
            if (cts.IsCancellationRequested)
                return;
            Connected = false;
            ConnectionChanged?.Invoke(false);
            foreach (var id in pending.Keys)
                if (pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(Tr("Le service ne répond pas.", "The service is not responding."));
            // Le service redémarre (mise à jour, arrêt manuel…) : on attend son retour
            while (!cts.IsCancellationRequested && TryOpen() == null)
            {
                try { await Task.Delay(2000, cts.Token); }
                catch (OperationCanceledException) { return; }
            }
            ConnectionChanged?.Invoke(true);
        }
    }

    void Dispatch(PipeMessage m)
    {
        switch (m.Type)
        {
            case "snapshot" when m.Snapshot != null:
                gameManual = m.Flag;
                keepAwake = m.Text;
                SnapshotReady?.Invoke(m.Snapshot);
                break;
            case "acted":
                ProBalanceActed?.Invoke(m.Names ?? Array.Empty<string>(), m.Value);
                break;
            case "game-changed":
                GameModeChanged?.Invoke(m.Flag, m.Text);
                break;
            case "notify":
                Notification?.Invoke(m.Text ?? "", m.Text2 ?? "");
                break;
            case "log":
                foreach (var entry in m.Log ?? Array.Empty<LogEntry>())
                    Log.Append(entry);
                break;
            case "result":
                if (pending.TryRemove(m.Id, out var tcs))
                    tcs.TrySetResult(m.Text);
                break;
            case "updating":
                ServiceUpdating?.Invoke();
                break;
        }
    }

    bool Send(PipeMessage message)
    {
        var bytes = Protocol.Encode(message);
        lock (writeLock)
        {
            if (pipe == null || !Connected)
                return false;
            try
            {
                pipe.Write(bytes);
                pipe.Flush();
                return true;
            }
            catch
            {
                return false; // la boucle de lecture voit la coupure et se reconnecte
            }
        }
    }

    Task<string?> Request(PipeMessage message)
    {
        message.Id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[message.Id] = tcs;
        if (!Send(message))
        {
            pending.TryRemove(message.Id, out _);
            tcs.TrySetResult(Tr("Le service ne répond pas.", "The service is not responding."));
        }
        return tcs.Task;
    }

    void SendSession()
    {
        try
        {
            Send(new PipeMessage
            {
                Type = "session",
                Session = new SessionState(Native.GetForegroundPid(), Idle.UserIdleTime().TotalSeconds, Idle.DisplayRequired()),
            });
        }
        catch { }
    }

    public void Stop() => Dispose();

    public void UpdateSettings(Settings settings) => Send(new PipeMessage { Type = "apply", Settings = settings });

    public void Save(Settings settings) => Send(new PipeMessage { Type = "save", Settings = settings });

    public void SetPaused(bool value) => Send(new PipeMessage { Type = "pause", Flag = value });

    public void SetGameMode(bool enabled)
    {
        gameManual = enabled; // en attendant le prochain instantané
        Send(new PipeMessage { Type = "game", Flag = enabled });
    }

    public string? SetPriorityOnce(int pid, string name, ProcessPriorityClass priority)
    {
        var task = Request(new PipeMessage { Type = "priority", Pid = pid, Text = name, Priority = priority });
        return task.Wait(TimeSpan.FromSeconds(5)) ? task.Result : Tr("Le service ne répond pas.", "The service is not responding.");
    }

    public void CleanMemoryNow() => Send(new PipeMessage { Type = "clean" });

    /// <summary>Demande au service d'installer la dernière version ; renvoie un message d'erreur ou null.</summary>
    public async Task<string?> InstallUpdateAsync()
    {
        var task = Request(new PipeMessage { Type = "update" });
        return await Task.WhenAny(task, Task.Delay(TimeSpan.FromMinutes(10))) == task
            ? task.Result
            : Tr("Le service ne répond pas.", "The service is not responding.");
    }

    public void Dispose()
    {
        cts.Cancel();
        sessionTimer?.Dispose();
        lock (writeLock)
        {
            Connected = false;
            pipe?.Dispose();
        }
    }
}
