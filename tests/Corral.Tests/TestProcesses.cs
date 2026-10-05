using System.Diagnostics;

namespace Corral.Tests;

/// <summary>
/// Lance des processus de test sous un nom unique (copie d'un exe système) et les ferme à la fin.
/// Les tests tournent en parallèle : un nom partagé comme « ping » ferait interférer les règles d'un test
/// avec les processus d'un autre.
/// </summary>
public sealed class TestProcesses : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "CorralTest_" + Guid.NewGuid().ToString("N"));
    readonly List<Process> started = new();

    /// <summary>Nom unique à ce test, à utiliser comme motif de règle.</summary>
    public string Name(string prefix) => $"{prefix}_{Path.GetFileName(dir)[11..19]}";

    /// <summary>Copie de ping.exe qui tourne une minute.</summary>
    public Process Ping(string name) => Start(name, "PING.EXE", "-n 60 127.0.0.1");

    /// <summary>Copie de cmd.exe qui attend sans lancer d'autre processus (≈ 7 Mo de mémoire privée).</summary>
    public Process Cmd(string name) => Start(name, "cmd.exe", "/k");

    Process Start(string name, string source, string args)
    {
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, name + ".exe");
        if (!File.Exists(exe))
            File.Copy(Path.Combine(Environment.SystemDirectory, source), exe);
        var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardInput = source == "cmd.exe", // cmd /k attend sur l'entrée standard
        })!;
        // Priorité héritée du lanceur (dotnet peut avoir été abaissé par un Corral installé) : on repart de Normale
        try { p.PriorityClass = ProcessPriorityClass.Normal; } catch { }
        started.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var p in started)
        {
            try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(2000); } } catch { }
            p.Dispose();
        }
        try { Directory.Delete(dir, true); } catch { }
    }
}
