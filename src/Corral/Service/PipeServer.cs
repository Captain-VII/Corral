using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using Corral.Core;

namespace Corral.Service;

/// <summary>Une interface connectée au service.</summary>
public sealed class PipeClient : IDisposable
{
    readonly NamedPipeServerStream pipe;
    // Un client lent ne doit jamais bloquer le moteur : au-delà de 64 messages en attente, les plus anciens sont perdus.
    readonly Channel<byte[]> outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    public PipeClient(NamedPipeServerStream pipe, int sessionId, string? sid)
    {
        this.pipe = pipe;
        SessionId = sessionId;
        Sid = sid;
    }

    public int SessionId { get; }
    /// <summary>SID de l'utilisateur (pour sa ruche de registre), ou null s'il n'a pas pu être lu.</summary>
    public string? Sid { get; }
    public SessionState? Session { get; set; }
    public DateTime SessionTime { get; set; }

    public void Send(PipeMessage message) => Send(Protocol.Encode(message));

    internal void Send(byte[] bytes) => outbox.Writer.TryWrite(bytes);

    internal async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var bytes in outbox.Reader.ReadAllAsync(ct))
            {
                await pipe.WriteAsync(bytes, ct);
                await pipe.FlushAsync(ct);
            }
        }
        catch
        {
            Dispose(); // client parti : la boucle de lecture se termine aussi
        }
    }

    internal LineReader Reader() => new(pipe);

    public void Dispose()
    {
        outbox.Writer.TryComplete();
        try { pipe.Dispose(); } catch { }
    }
}

/// <summary>
/// Serveur du pipe nommé. Accès : SYSTEM et administrateurs, plus les utilisateurs connectés localement ou en bureau à distance
/// (INTERACTIVE). Les accès réseau sont refusés et personne d'autre ne peut créer d'instance du pipe.
/// </summary>
public sealed class PipeServer : IDisposable
{
    const int MaxClients = 32;

    readonly string name;
    readonly CancellationTokenSource cts = new();
    readonly List<PipeClient> clients = new();
    NamedPipeServerStream? listening;
    bool first;

    /// <param name="firstInstance">Échoue si un autre programme a déjà créé le pipe (protection contre l'usurpation).</param>
    public PipeServer(string name, bool firstInstance = true)
    {
        this.name = name;
        first = firstInstance;
    }

    public event Action<PipeClient>? Connected;
    public event Action<PipeClient, PipeMessage>? Received;

    public List<PipeClient> Clients
    {
        get { lock (clients) return clients.ToList(); }
    }

    /// <summary>Crée la première instance (lève une exception si le nom est pris), puis accepte les clients en arrière-plan.</summary>
    public void Start()
    {
        listening = Create();
        _ = Task.Run(AcceptLoop);
    }

    NamedPipeServerStream Create()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // Le compte du serveur lui-même (SYSTEM pour le service) : il crée les instances suivantes
        using (var self = WindowsIdentity.GetCurrent())
            security.AddAccessRule(new PipeAccessRule(self.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // Lecture/écriture sans « créer une instance » (le client demande ces droits exacts, voir RemoteEngine.ClientRights)
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), RemoteEngine.ClientRights, AccessControlType.Allow));
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : 0);
        first = false;
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte, options, 0, 0, security);
    }

    async Task AcceptLoop()
    {
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var pipe = listening!;
                await pipe.WaitForConnectionAsync(cts.Token);
                lock (clients)
                {
                    if (cts.IsCancellationRequested)
                        break;
                    listening = Create(); // une instance reste toujours à l'écoute : le nom du pipe n'est jamais libéré
                }
                Accept(pipe);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Pipe du service", ex);
                try { await Task.Delay(1000, cts.Token); } catch { break; }
            }
        }
    }

    void Accept(NamedPipeServerStream pipe)
    {
        string? sid = null;
        try { pipe.RunAsClient(() => sid = WindowsIdentity.GetCurrent().User?.Value); }
        catch { }
        var client = new PipeClient(pipe, Protocol.ClientSession(pipe.SafePipeHandle), sid);
        lock (clients)
            clients.Add(client);
        _ = Task.Run(() => client.PumpAsync(cts.Token));
        try { Connected?.Invoke(client); }
        catch (Exception ex) { Log.Error("Connexion d'un client", ex); }
        _ = Task.Run(() => ReadLoop(client));
    }

    async Task ReadLoop(PipeClient client)
    {
        try
        {
            var reader = client.Reader();
            string? line;
            while ((line = await reader.ReadAsync(cts.Token)) != null)
            {
                if (Protocol.Decode(line) is not { } message)
                    continue;
                try { Received?.Invoke(client, message); }
                catch (Exception ex) { Log.Error($"Message « {message.Type} »", ex); }
            }
        }
        catch
        {
            // client parti, ou message trop long : on le déconnecte
        }
        finally
        {
            lock (clients)
                clients.Remove(client);
            client.Dispose();
        }
    }

    public void Broadcast(PipeMessage message)
    {
        var targets = Clients;
        if (targets.Count == 0)
            return;
        var bytes = Protocol.Encode(message);
        foreach (var c in targets)
            c.Send(bytes);
    }

    /// <summary>Client de la session affichée à l'écran (sinon le dernier actif), dont l'état récent sert au moteur.</summary>
    public PipeClient? ActiveClient()
    {
        var recent = Clients.Where(c => c.Session != null && DateTime.UtcNow - c.SessionTime < TimeSpan.FromSeconds(5)).ToList();
        int console = Protocol.ConsoleSession;
        return recent.Where(c => c.SessionId == console).MaxBy(c => c.SessionTime) ?? recent.MaxBy(c => c.SessionTime);
    }

    public void Dispose()
    {
        lock (clients)
        {
            cts.Cancel();
            listening?.Dispose();
        }
        foreach (var c in Clients)
            c.Dispose();
    }
}
