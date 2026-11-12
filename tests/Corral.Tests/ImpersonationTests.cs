using System.IO.Pipes;
using System.Security.Principal;
using Corral.Service;

namespace Corral.Tests;

/// <summary>
/// Avant la 3.1.3, l'identité d'un client semblait rester sur un thread du pool après RunAsClient (erreurs 1346).
/// Reproduit l'ancien schéma : RunAsClient appelé directement dans la suite de WaitForConnectionAsync, comme l'ancien Accept.
/// </summary>
public class ImpersonationTests
{
    [Theory]
    [InlineData(false)] // identité seule (v2.1 à 3.0)
    [InlineData(true)]  // avec la vérification administrateur ajoutée en 3.1.0
    public async Task RunAsClientLeavesNoTokenOnTheThread(bool checkAdmin)
    {
        var name = "CorralImp_" + Guid.NewGuid().ToString("N");
        for (int i = 0; i < 100; i++)
        {
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var wait = server.WaitForConnectionAsync();
            using var client = Protocol.OpenClient(name, RemoteEngine.ClientRights, 5000);
            Assert.NotNull(client);
            await wait.ConfigureAwait(false); // suite sur un thread du pool, comme AcceptLoop

            var inside = TokenImpersonationLevel.None;
            Exception? error = null;
            try
            {
                server.RunAsClient(() =>
                {
                    using var id = WindowsIdentity.GetCurrent();
                    inside = id.ImpersonationLevel;
                    if (checkAdmin)
                        PipeServer.IsAdministrator(id);
                });
            }
            catch (Exception ex) { error = ex; } // l'ancien Accept avalait cette exception (échec de RevertToSelf compris)
            using var still = WindowsIdentity.GetCurrent(ifImpersonating: true);

            Assert.True(error == null, $"Itération {i} : RunAsClient a levé {error}");
            Assert.Equal(TokenImpersonationLevel.Identification, inside);
            Assert.True(still == null, $"Itération {i} : thread {Environment.CurrentManagedThreadId} encore sous l'identité {still?.Name} ({still?.ImpersonationLevel})");
        }
    }
}
