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
    readonly bool mirrorLog;
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

    RemoteEngine(string pipeName, bool requireService, bool mirrorLog)
    {
        this.pipeName = pipeName;
        this.requireService = requireService;
        this.mirrorLog = mirrorLog;
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
    /// <param name="mirrorLog">
    /// Recopie le journal du service dans celui de l'interface. Les tests le désactivent : service et interface y partagent
    /// le même journal, que la recopie doublerait.
    /// </param>
    public static RemoteEngine? Connect(TimeSpan timeout, string pipeName = Protocol.PipeName, bool requireService = true, bool mirrorLog = true)
    {
        var remote = new RemoteEngine(pipeName, requireService, mirrorLog);
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            if (remote.TryOpen() is { } welcome)
            {
                remote.InitialSettings = welcome.Settings ?? new Settings();
                remote.InitialSettings.Normalize();
                remote.Fresh = welcome.Flag;
                remote.UpdateInProgress = welcome.Updating;
                remote.StandardUser = welcome.StandardUser;
                remote.ServiceVersion = welcome.Text;
                if (mirrorLog)
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

    /// <summary>Aucune interface n'a encore envoyé de configuration au service (juste après l'installation).</summary>
    public bool Fresh { get; private set; }

    public bool Connected { get; private set; }
    /// <summary>Le service installait une mise à jour au moment de la connexion : il va s'arrêter puis relancer l'interface.</summary>
    public bool UpdateInProgress { get; private set; }

    /// <summary>Compte standard : le service refuse ses modifications (stratégie AllowStandardUsers absente).</summary>
    public bool StandardUser { get; private set; }

    /// <summary>Version du service (x.y.z).</summary>
    public string? ServiceVersion { get; private set; }

    public event Action<EngineSnapshot>? SnapshotReady;
    public event Action<IReadOnlyList<string>, double>? ProBalanceActed;
    public event Action<bool, string?>? GameModeChanged;
    public event Action<string, string>? Notification;
    /// <summary>Le service va installer une mise à jour : l'interface doit se fermer.</summary>
    public event Action? ServiceUpdating;
    public event Action<bool>? ConnectionChanged;
    /// <summary>Configuration modifiée par un autre client (ligne de commande).</summary>
    public event Action<Settings>? SettingsChanged;

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
            if (!read.Wait(TimeSpan.FromSeconds(5)) || read.Result is not { } line || Protocol.Decode(line) is not { Type: MessageType.Welcome } welcome)
                throw new TimeoutException();
            lock (writeLock)
            {
                pipe?.Dispose(); // liaison précédente, coupée
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
        Listen();
        sessionTimer ??= new System.Threading.Timer(_ => SendSession(), null, 0, 1000);
    }

    /// <summary>Reçoit les messages du service sans lui envoyer l'état de la session (ligne de commande).</summary>
    public void Listen()
    {
        if (started)
            return;
        started = true;
        _ = Task.Run(ReadLoop);
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
                    tcs.TrySetResult(NoResponse);
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
            case MessageType.Snapshot when m.Snapshot != null:
                gameManual = m.Flag;
                keepAwake = m.Text;
                SnapshotReady?.Invoke(m.Snapshot);
                break;
            case MessageType.Acted:
                ProBalanceActed?.Invoke(m.Names ?? Array.Empty<string>(), m.Value);
                break;
            case MessageType.GameChanged:
                GameModeChanged?.Invoke(m.Flag, m.Text);
                break;
            case MessageType.Notify:
                Notification?.Invoke(m.Text ?? "", m.Text2 ?? "");
                break;
            case MessageType.Log when mirrorLog:
                foreach (var entry in m.Log ?? Array.Empty<LogEntry>())
                    Log.Append(entry);
                break;
            case MessageType.Result:
                if (pending.TryRemove(m.Id, out var tcs))
                    tcs.TrySetResult(m.Text);
                break;
            case MessageType.Updating:
                ServiceUpdating?.Invoke();
                break;
            case MessageType.Settings when m.Settings != null:
                SettingsChanged?.Invoke(m.Settings);
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

    static string NoResponse => Tr("Le service ne répond pas.", "The service is not responding.");

    /// <summary>Envoie une requête et attend sa réponse « result » ; renvoie un message d'erreur, ou null si c'est fait.</summary>
    async Task<string?> Request(PipeMessage message, TimeSpan timeout)
    {
        message.Id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[message.Id] = tcs;
        if (!Send(message))
        {
            pending.TryRemove(message.Id, out _);
            return NoResponse;
        }
        // ConfigureAwait(false) : SetPriorityOnce attend la réponse en bloquant le thread de l'interface
        if (await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false) == tcs.Task)
            return tcs.Task.Result;
        pending.TryRemove(message.Id, out _); // sans réponse : ne pas garder la demande indéfiniment
        return NoResponse;
    }

    void SendSession()
    {
        try
        {
            Send(new PipeMessage
            {
                Type = MessageType.Session,
                Session = new SessionState(Native.GetForegroundPid(), Idle.UserIdleTime().TotalSeconds, Idle.DisplayRequired()),
            });
        }
        catch { }
    }

    public void Stop() => Dispose();

    public void UpdateSettings(Settings settings) => Send(new PipeMessage { Type = MessageType.Apply, Settings = settings });

    public void Save(Settings settings) => Send(new PipeMessage { Type = MessageType.Save, Settings = settings });

    /// <summary>Applique la configuration et attend la réponse du service ; renvoie un message d'erreur ou null.</summary>
    public Task<string?> ApplyAsync(Settings settings) =>
        Request(new PipeMessage { Type = MessageType.Apply, Settings = settings }, TimeSpan.FromSeconds(10));

    public void SetPaused(bool value) => Send(new PipeMessage { Type = MessageType.Pause, Flag = value });

    public void SetGameMode(bool enabled)
    {
        gameManual = enabled; // en attendant le prochain instantané
        Send(new PipeMessage { Type = MessageType.Game, Flag = enabled });
    }

    public string? SetPriorityOnce(int pid, string name, ProcessPriorityClass priority) =>
        Request(new PipeMessage { Type = MessageType.Priority, Pid = pid, Text = name, Priority = priority }, TimeSpan.FromSeconds(5))
            .GetAwaiter().GetResult();

    public void CleanMemoryNow() => Send(new PipeMessage { Type = MessageType.Clean });

    /// <summary>Demande au service d'installer la dernière version ; renvoie un message d'erreur ou null.</summary>
    public Task<string?> InstallUpdateAsync() =>
        Request(new PipeMessage { Type = MessageType.Update }, TimeSpan.FromMinutes(10));

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
