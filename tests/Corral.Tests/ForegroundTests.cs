using System.Diagnostics;
using System.Runtime.InteropServices;
using Corral.Core;

namespace Corral.Tests;

/// <summary>
/// Premier plan des applications UWP (jeux du Microsoft Store, Minecraft Bedrock…) : la fenêtre active est le cadre
/// d'ApplicationFrameHost, le programme hébergé y place une fenêtre enfant. Simulé ici : un formulaire de ce processus
/// sert de cadre, la fenêtre d'un autre processus devient son enfant.
/// </summary>
public class ForegroundTests
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [Fact]
    public void ProcessHostedInAFrameIsFound()
    {
        using var hosted = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -Command \"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Application]::Run((New-Object System.Windows.Forms.Form))\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            var sw = Stopwatch.StartNew();
            while (hosted.MainWindowHandle == IntPtr.Zero && sw.Elapsed < TimeSpan.FromSeconds(60))
            {
                Thread.Sleep(200);
                hosted.Refresh();
            }
            var child = hosted.MainWindowHandle;
            Assert.NotEqual(IntPtr.Zero, child);

            uint? withChild = null, alone = null;
            bool parented = false;
            var ui = new Thread(() =>
            {
                using var frame = new Form();
                var handle = frame.Handle;
                alone = Native.HostedPid(handle, (uint)Environment.ProcessId); // aucun enfant d'un autre processus
                parented = SetParent(child, handle) != IntPtr.Zero; // pas d'Assert ici : il ferait tomber l'hôte de test
                withChild = Native.HostedPid(handle, (uint)Environment.ProcessId);
                SetParent(child, IntPtr.Zero);
            });
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            ui.Join();

            Assert.True(parented, "SetParent a échoué");
            Assert.Null(alone);
            Assert.Equal((uint)hosted.Id, withChild);
        }
        finally
        {
            try { hosted.Kill(entireProcessTree: true); } catch { }
        }
    }
}
