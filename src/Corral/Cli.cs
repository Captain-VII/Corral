using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Corral.Core;
using Corral.Models;
using Corral.Service;

namespace Corral;

/// <summary>
/// Ligne de commande (« Corral.exe status », « Corral.exe rules add … ») : pilote le service de l'installation MSI.
/// Codes de sortie : 0 succès, 1 erreur, 2 commande invalide, 3 service injoignable.
/// Le module PowerShell (packaging/powershell) s'appuie sur la sortie --json.
/// </summary>
public static class Cli
{
    public const int Ok = 0, Failed = 1, Usage = 2, NoService = 3;

    static readonly string[] Commands = { "help", "status", "processes", "rules", "pause", "resume", "game", "clean", "priority", "fleet" };

    /// <summary>Les arguments sont-ils une commande (et non une option de l'interface comme --minimized) ?</summary>
    public static bool IsCommand(string[] args) =>
        args.Length > 0 && (Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase) || args[0] is "-h" or "--help" or "/?");

    static readonly JsonSerializerOptions Json = new(RuleStore.Options);

    sealed class CliException(string message, int code = Failed) : Exception(message)
    {
        public int Code { get; } = code;
    }

    /// <summary>Point d'entrée depuis Program.Main : écrit dans la console qui a lancé Corral (ou dans la sortie redirigée).</summary>
    public static int Execute(string[] args)
    {
        TextWriter output, error;
        if (Console.IsOutputRedirected)
        {
            // Module PowerShell, « | more »… : UTF-8, sans toucher à la page de code de la console
            var utf8 = new UTF8Encoding(false);
            output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true };
            error = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };
        }
        else
        {
            if (AttachConsole(ATTACH_PARENT_PROCESS))
                Console.Out.WriteLine(); // l'invite de cmd est déjà affichée : on passe à la ligne
            (output, error) = (Console.Out, Console.Error);
        }
        int code = Run(args, output, error);
        output.Flush();
        error.Flush();
        return code;
    }

    public static int Run(string[] args, TextWriter output, TextWriter error, string pipeName = Protocol.PipeName, bool requireService = true)
    {
        try
        {
            var command = args[0].ToLowerInvariant();
            if (command is "help" or "-h" or "--help" or "/?")
            {
                output.Write(Help);
                return Ok;
            }
            bool json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);
            var rest = args.Skip(1).Where(a => !a.Equals("--json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (command == "fleet")
                return FleetCommand(rest, output); // outils de l'équipe informatique : pas besoin du service
            if (requireService && !RemoteEngine.ServiceInstalled)
                throw new CliException(Tr("La ligne de commande nécessite l'installation de Corral (MSI) : son service n'est pas installé.",
                    "The command line requires the Corral installation (MSI): its service is not installed."), NoService);
            using var remote = RemoteEngine.Connect(TimeSpan.FromSeconds(10), pipeName, requireService, mirrorLog: false)
                ?? throw new CliException(Tr("Le service Corral ne répond pas.", "The Corral service is not responding."), NoService);
            remote.Listen();
            return command switch
            {
                "status" => Status(remote, output, json),
                "processes" => Processes(remote, output, json),
                "rules" => Rules(remote, rest, output, json),
                "pause" => Simple(() => remote.SetPaused(true), output, Tr("Corral en pause.", "Corral paused.")),
                "resume" => Simple(() => remote.SetPaused(false), output, Tr("Corral a repris.", "Corral resumed.")),
                "game" => Game(remote, rest, output),
                "clean" => Simple(remote.CleanMemoryNow, output, Tr("Nettoyage de la mémoire demandé.", "Memory cleanup requested.")),
                "priority" => Priority(remote, rest, output),
                _ => throw new CliException(Tr($"Commande inconnue : {command}", $"Unknown command: {command}"), Usage),
            };
        }
        catch (CliException ex)
        {
            error.WriteLine(ex.Message);
            if (ex.Code == Usage)
                error.WriteLine(Tr("Aide : Corral.exe help", "Help: Corral.exe help"));
            return ex.Code;
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.Message);
            return Failed;
        }
    }

    static string Help => Tr("""
        Corral - ligne de commande (installation MSI)

          status [--json]                     État : pause, Mode Jeu, CPU, mémoire, règles
          processes [--json]                  Processus suivis
          rules [--json]                      Règles du profil actif
          rules add <nom> [options]           Ajoute ou remplace une règle :
              --priority <Idle|BelowNormal|Normal|AboveNormal|High>
              --affinity <masque, ex. 0xFF>   --cpu-limit <1-99>   --efficiency <on|off>   --game
          rules remove <nom>                  Supprime une règle
          rules enable|disable <nom>          Active ou désactive une règle
          rules export <fichier>              Exporte les règles
          rules import <fichier> [--replace]  Importe des règles (ajoutées, ou remplaçant les actuelles)
          pause | resume                      Suspend ou reprend Corral
          game on|off                         Mode Jeu manuel
          clean                               Nettoie la mémoire maintenant
          priority <pid> <priorité>           Change la priorité d'un processus une fois

        Configuration de parc (équipe informatique) :
          fleet keygen <clé.pem>              Crée la clé de signature et affiche la clé publique
          fleet sign <config.json> <clé.pem>  Signe la configuration (écrit config.json.sig)
          fleet check <config.json> <clé publique>  Vérifie la signature et résume le contenu

        Codes de sortie : 0 succès, 1 erreur, 2 commande invalide, 3 service injoignable.

        """, """
        Corral - command line (MSI installation)

          status [--json]                     Status: pause, Game Mode, CPU, memory, rules
          processes [--json]                  Tracked processes
          rules [--json]                      Rules of the active profile
          rules add <name> [options]          Adds or replaces a rule:
              --priority <Idle|BelowNormal|Normal|AboveNormal|High>
              --affinity <mask, e.g. 0xFF>    --cpu-limit <1-99>   --efficiency <on|off>   --game
          rules remove <name>                 Deletes a rule
          rules enable|disable <name>         Enables or disables a rule
          rules export <file>                 Exports the rules
          rules import <file> [--replace]     Imports rules (added, or replacing the current ones)
          pause | resume                      Suspends or resumes Corral
          game on|off                         Manual Game Mode
          clean                               Cleans memory now
          priority <pid> <priority>           Changes a process priority once

        Fleet configuration (IT team):
          fleet keygen <key.pem>              Creates the signing key and shows the public key
          fleet sign <config.json> <key.pem>  Signs the configuration (writes config.json.sig)
          fleet check <config.json> <public key>  Checks the signature and summarizes the content

        Exit codes: 0 success, 1 error, 2 invalid command, 3 service unreachable.

        """);

    static int Simple(Action action, TextWriter output, string done)
    {
        action();
        output.WriteLine(done);
        return Ok;
    }

    static EngineSnapshot WaitSnapshot(RemoteEngine remote)
    {
        var tcs = new TaskCompletionSource<EngineSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.SnapshotReady += s => tcs.TrySetResult(s);
        return tcs.Task.Wait(TimeSpan.FromSeconds(10)) ? tcs.Task.Result
            : throw new CliException(Tr("Le service n'envoie pas d'état.", "The service sends no status."), NoService);
    }

    static int Status(RemoteEngine remote, TextWriter output, bool json)
    {
        var snap = WaitSnapshot(remote);
        var s = remote.InitialSettings;
        var status = new
        {
            Version = remote.ServiceVersion ?? Updater.CurrentVersion.ToString(3),
            snap.Paused,
            snap.GameMode,
            snap.GameTrigger,
            Cpu = Math.Round(snap.SystemCpu, 1),
            Memory = Math.Round(snap.MemoryPercent, 1),
            ProBalance = s.ProBalance.Enabled,
            Profile = s.ActiveProfile,
            Rules = s.Rules.Count,
            Locked = Policies.Current.LockSettings,
        };
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(status, Json));
            return Ok;
        }
        string YesNo(bool b) => b ? Tr("oui", "yes") : Tr("non", "no");
        output.WriteLine($"Corral {status.Version}");
        output.WriteLine($"  {Tr("En pause", "Paused"),-14}: {YesNo(snap.Paused)}");
        output.WriteLine($"  {Tr("Mode Jeu", "Game Mode"),-14}: {YesNo(snap.GameMode)}{(snap.GameTrigger is { } t ? $" ({t})" : "")}");
        output.WriteLine($"  {"ProBalance",-14}: {YesNo(status.ProBalance)}");
        output.WriteLine($"  {"CPU",-14}: {status.Cpu:0.0} %");
        output.WriteLine($"  {Tr("Mémoire", "Memory"),-14}: {status.Memory:0.0} %");
        output.WriteLine($"  {Tr("Profil", "Profile"),-14}: {status.Profile} ({status.Rules} {Tr("règles", "rules")})");
        if (status.Locked)
            output.WriteLine("  " + Tr("Réglages verrouillés par votre organisation.", "Settings locked by your organization."));
        return Ok;
    }

    static int Processes(RemoteEngine remote, TextWriter output, bool json)
    {
        var rows = WaitSnapshot(remote).Rows.OrderByDescending(r => r.Cpu).ToList();
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(rows.Select(r => new { r.Pid, r.Name, Cpu = Math.Round(r.Cpu, 1), r.MemoryBytes, r.Rule, r.Restrained, r.Path }), Json));
            return Ok;
        }
        output.WriteLine($"{"PID",7}  {"CPU %",6}  {Tr("Mémoire", "Memory"),10}  {Tr("Nom", "Name")}");
        foreach (var r in rows)
            output.WriteLine($"{r.Pid,7}  {r.Cpu,6:0.0}  {r.MemoryBytes / (1024 * 1024),7} Mo  {r.Name}{(r.Rule != null ? $"  [{r.Rule}]" : "")}{(r.Restrained ? "  (ProBalance)" : "")}");
        return Ok;
    }

    static int Rules(RemoteEngine remote, string[] args, TextWriter output, bool json)
    {
        var settings = remote.InitialSettings;
        if (args.Length == 0)
        {
            if (json)
                output.WriteLine(JsonSerializer.Serialize(settings.Rules, Json));
            else
                foreach (var r in settings.Rules)
                    output.WriteLine($"{(r.Enabled ? "[x]" : "[ ]")} {r.Pattern}{Describe(r)}");
            return Ok;
        }
        var action = args[0].ToLowerInvariant();
        string Arg(int i) => args.Length > i ? args[i]
            : throw new CliException(Tr($"Argument manquant pour « rules {action} ».", $"Missing argument for “rules {action}”."), Usage);
        Rule Find(string pattern) => settings.Rules.FirstOrDefault(r => r.Pattern.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliException(Tr($"Aucune règle « {pattern} ».", $"No rule “{pattern}”."));
        if (action != "export" && settings.Managed?.Contains("Règles") == true)
            throw new CliException(Tr("Règles imposées par la configuration de parc de votre organisation.", "Rules enforced by your organization's fleet configuration."));
        string done;
        switch (action)
        {
            case "export":
                RuleStore.ExportRules(settings.Rules, Arg(1));
                output.WriteLine(Tr($"{settings.Rules.Count} règles exportées.", $"{settings.Rules.Count} rules exported."));
                return Ok;
            case "import":
                var imported = RuleStore.ImportRules(Arg(1));
                if (args.Contains("--replace", StringComparer.OrdinalIgnoreCase))
                    settings.Rules = imported;
                else
                    foreach (var r in imported)
                        Upsert(settings, r);
                done = Tr($"{imported.Count} règles importées.", $"{imported.Count} rules imported.");
                break;
            case "add":
                var rule = ParseRule(Arg(1), args.Skip(2).ToArray());
                Upsert(settings, rule);
                done = Tr($"Règle « {rule.Pattern} » enregistrée.", $"Rule “{rule.Pattern}” saved.");
                break;
            case "remove":
                settings.Rules.Remove(Find(Arg(1)));
                done = Tr($"Règle « {Arg(1)} » supprimée.", $"Rule “{Arg(1)}” deleted.");
                break;
            case "enable":
            case "disable":
                Find(Arg(1)).Enabled = action == "enable";
                done = action == "enable" ? Tr($"Règle « {Arg(1)} » activée.", $"Rule “{Arg(1)}” enabled.")
                    : Tr($"Règle « {Arg(1)} » désactivée.", $"Rule “{Arg(1)}” disabled.");
                break;
            default:
                throw new CliException(Tr($"Action inconnue : rules {action}", $"Unknown action: rules {action}"), Usage);
        }
        if (remote.ApplyAsync(settings).GetAwaiter().GetResult() is { } failure)
            throw new CliException(failure);
        output.WriteLine(done);
        return Ok;
    }

    /// <summary>Remplace la règle de même nom, ou l'ajoute à la fin.</summary>
    static void Upsert(Settings settings, Rule rule)
    {
        int i = settings.Rules.FindIndex(r => r.Pattern.Equals(rule.Pattern, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
            settings.Rules[i] = rule;
        else
            settings.Rules.Add(rule);
    }

    static string Describe(Rule r)
    {
        var parts = new List<string>();
        if (r.Priority is { } p) parts.Add(p.ToString());
        if (r.AffinityMask is { } m) parts.Add($"0x{m:X}");
        if (r.CpuLimitPercent is { } c) parts.Add($"CPU ≤ {c} %");
        if (r.EfficiencyMode is { } e) parts.Add(e ? "Eco" : "No eco");
        if (r.IsGame) parts.Add(Tr("jeu", "game"));
        if (r.When?.Describe() is { Length: > 0 } when) parts.Add(when);
        return parts.Count > 0 ? "  (" + string.Join(", ", parts) + ")" : "";
    }

    public static Rule ParseRule(string pattern, string[] options)
    {
        var rule = new Rule { Pattern = pattern.Trim() };
        if (rule.Pattern.Length == 0)
            throw new CliException(Tr("Nom de processus vide.", "Empty process name."), Usage);
        for (int i = 0; i < options.Length; i++)
        {
            string Value() => i + 1 < options.Length ? options[++i]
                : throw new CliException(Tr($"Valeur manquante pour {options[i]}.", $"Missing value for {options[i]}."), Usage);
            switch (options[i].ToLowerInvariant())
            {
                case "--priority":
                    rule.Priority = ParsePriority(Value());
                    break;
                case "--affinity":
                    var text = Value();
                    long mask;
                    bool ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? long.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out mask)
                        : long.TryParse(text, out mask);
                    if (!ok || !RuleMatcher.IsValidAffinity(mask, Environment.ProcessorCount))
                        throw new CliException(Tr($"Masque d'affinité invalide : {text}", $"Invalid affinity mask: {text}"), Usage);
                    rule.AffinityMask = mask;
                    break;
                case "--cpu-limit":
                    rule.CpuLimitPercent = int.TryParse(Value(), out var cpu) && cpu is >= 1 and <= 99 ? cpu
                        : throw new CliException(Tr("--cpu-limit attend un nombre de 1 à 99.", "--cpu-limit expects a number from 1 to 99."), Usage);
                    break;
                case "--efficiency":
                    rule.EfficiencyMode = Value().ToLowerInvariant() switch
                    {
                        "on" or "true" or "1" => true,
                        "off" or "false" or "0" => false,
                        _ => throw new CliException(Tr("--efficiency attend on ou off.", "--efficiency expects on or off."), Usage),
                    };
                    break;
                case "--game":
                    rule.IsGame = true;
                    break;
                default:
                    throw new CliException(Tr($"Option inconnue : {options[i]}", $"Unknown option: {options[i]}"), Usage);
            }
        }
        return rule;
    }

    static ProcessPriorityClass ParsePriority(string text) =>
        Enum.TryParse<ProcessPriorityClass>(text, ignoreCase: true, out var p) && Enum.IsDefined(p) && p != ProcessPriorityClass.RealTime
            ? p
            : throw new CliException(Tr($"Priorité invalide : {text} (Idle, BelowNormal, Normal, AboveNormal, High)",
                $"Invalid priority: {text} (Idle, BelowNormal, Normal, AboveNormal, High)"), Usage);

    static int FleetCommand(string[] args, TextWriter output)
    {
        switch (args)
        {
            case ["keygen", var pem]:
                var publicKey = Fleet.KeyGen(pem);
                output.WriteLine(Tr($"Clé privée : {pem} (à garder secrète et sauvegardée)", $"Private key: {pem} (keep it secret and backed up)"));
                output.WriteLine(Tr("Clé publique (stratégie « Configuration de parc ») :", "Public key (“Fleet configuration” policy):"));
                output.WriteLine(publicKey);
                return Ok;
            case ["sign", var config, var pem]:
                var key = Fleet.Sign(config, pem);
                output.WriteLine(Tr($"Signé : {config}.sig", $"Signed: {config}.sig"));
                output.WriteLine(Tr("Clé publique : ", "Public key: ") + key);
                return Ok;
            case ["check", var config, var publicKey2]:
                var sigFile = config + ".sig";
                if (!File.Exists(sigFile))
                    throw new CliException(Tr($"Signature absente : {sigFile}", $"Missing signature: {sigFile}"));
                var c = Fleet.Verify(File.ReadAllBytes(config), File.ReadAllText(sigFile), publicKey2);
                output.WriteLine(Tr($"Signature valide. Configuration n° {c.Serial}", $"Valid signature. Configuration #{c.Serial}"));
                if (c.Rules != null)
                    output.WriteLine(Tr($"  Règles : {c.Rules.Count}", $"  Rules: {c.Rules.Count}"));
                if (c.ProBalance != null)
                    output.WriteLine("  ProBalance");
                if (c.GameMode != null)
                    output.WriteLine(Tr("  Mode Jeu", "  Game Mode"));
                return Ok;
            default:
                throw new CliException(Tr("Utilisation : fleet keygen|sign|check …", "Usage: fleet keygen|sign|check …"), Usage);
        }
    }

    static int Game(RemoteEngine remote, string[] args, TextWriter output)
    {
        bool on = args.FirstOrDefault()?.ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            _ => throw new CliException(Tr("Utilisation : game on|off", "Usage: game on|off"), Usage),
        };
        remote.SetGameMode(on);
        output.WriteLine(on ? Tr("Mode Jeu activé.", "Game Mode enabled.") : Tr("Mode Jeu désactivé.", "Game Mode disabled."));
        return Ok;
    }

    static int Priority(RemoteEngine remote, string[] args, TextWriter output)
    {
        if (args.Length < 2 || !int.TryParse(args[0], out var pid))
            throw new CliException(Tr("Utilisation : priority <pid> <priorité>", "Usage: priority <pid> <priority>"), Usage);
        var priority = ParsePriority(args[1]);
        // Nom tel que le service le connaît : il vérifie que le pid désigne toujours le même programme
        var name = WaitSnapshot(remote).Rows.FirstOrDefault(r => r.Pid == pid)?.Name
            ?? throw new CliException(Tr($"Aucun processus {pid}.", $"No process {pid}."));
        if (remote.SetPriorityOnce(pid, name, priority) is { } failure)
            throw new CliException(failure);
        output.WriteLine(Tr($"{name} ({pid}) : priorité {priority}.", $"{name} ({pid}): priority {priority}."));
        return Ok;
    }

    const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
