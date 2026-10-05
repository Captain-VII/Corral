using System.Diagnostics;
using System.Security;
using System.Text;

namespace Corral.Core;

/// <summary>
/// Démarrage à l'ouverture de session via une tâche planifiée (la clé Run ne peut pas lancer une appli admin).
/// On passe par un XML pour lever les pièges par défaut de schtasks : arrêt après 72 h, pas de démarrage
/// sur batterie, priorité « inférieure à la normale ».
/// </summary>
public static class AutoStart
{
    const string TaskName = "Corral";

    public static bool IsEnabled() => Run($"/query /tn {TaskName}", out _) == 0;

    /// <summary>Si la tâche existe mais lance un autre exe (Corral déplacé), la recrée vers <paramref name="exePath"/>.</summary>
    public static void RepairPath(string exePath)
    {
        try
        {
            if (Run($"/query /tn {TaskName} /xml", out var xml) != 0)
                return;
            var m = System.Text.RegularExpressions.Regex.Match(xml, "<Command>(.*?)</Command>");
            if (!m.Success)
                return;
            var current = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim('"');
            if (string.Equals(current, exePath, StringComparison.OrdinalIgnoreCase))
                return;
            Enable(exePath);
            Log.Info($"Démarrage automatique mis à jour vers {exePath}");
        }
        catch (Exception ex)
        {
            Log.Error("Mise à jour du démarrage automatique", ex);
        }
    }

    public static void Enable(string exePath)
    {
        var user = SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Corral - gestion des processus</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT10S</Delay></LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{SecurityElement.Escape(exePath)}</Command>
                  <Arguments>--minimized</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
        var tmp = Path.Combine(Path.GetTempPath(), $"corral_task_{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            if (Run($"/create /tn {TaskName} /xml \"{tmp}\" /f", out var output) != 0)
                throw new InvalidOperationException(output.Trim());
            Log.Info("Démarrage automatique activé");
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    public static void Disable()
    {
        if (Run($"/delete /tn {TaskName} /f", out var output) != 0)
            throw new InvalidOperationException(output.Trim());
        Log.Info("Démarrage automatique désactivé");
    }

    static int Run(string args, out string output)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(15_000))
        {
            try { p.Kill(); } catch { }
            output = "schtasks : délai dépassé";
            return -1;
        }
        output = stdout.Result + stderr.Result;
        return p.ExitCode;
    }
}
