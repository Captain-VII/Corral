using Corral.Core;
using Corral.Models;

namespace Corral.Tests;

public class SessionFairnessTests
{
    static readonly ProBalanceSettings Cfg = new() { SystemThreshold = 75, ProcessThreshold = 5, TriggerSeconds = 3, RestoreSeconds = 5 };
    static readonly DateTime T0 = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);

    static void Run(SessionFairnessLogic logic, int fromSecond, int toSecond, double system, params (int Id, double Cpu)[] sessions)
    {
        var cpu = sessions.ToDictionary(s => s.Id, s => s.Cpu);
        for (int t = fromSecond; t <= toSecond; t++)
            logic.Evaluate(T0.AddSeconds(t), system, cpu, Cfg);
    }

    [Fact]
    public void GreedySessionIsLimitedAfterTriggerThenRestored()
    {
        var logic = new SessionFairnessLogic();
        // 90 % au total, part équitable 45 % : la session 1 en prend 70 (plus d'une fois et demie)
        Run(logic, 0, 2, 90, (1, 70), (2, 20));
        Assert.False(logic.IsLimited(1)); // pas avant la durée de déclenchement
        Run(logic, 3, 3, 90, (1, 70), (2, 20));
        Assert.True(logic.IsLimited(1));
        Assert.False(logic.IsLimited(2));

        // Revenue à sa part : restaurée après la durée de restauration
        Run(logic, 4, 8, 90, (1, 40), (2, 50));
        Assert.True(logic.IsLimited(1));
        Run(logic, 9, 9, 90, (1, 40), (2, 50));
        Assert.False(logic.IsLimited(1));
    }

    [Fact]
    public void CalmSystemRestoresAndSingleSessionIsNeverLimited()
    {
        var logic = new SessionFairnessLogic();
        Run(logic, 0, 3, 95, (1, 80), (2, 10));
        Assert.True(logic.IsLimited(1));
        Run(logic, 4, 9, 40, (1, 35), (2, 5)); // processeur calme
        Assert.False(logic.IsLimited(1));

        // Une seule session qui travaille (l'autre est inactive) : rien à partager
        var alone = new SessionFairnessLogic();
        Run(alone, 0, 10, 99, (1, 95), (2, 0.2));
        Assert.False(alone.IsLimited(1));
    }

    [Fact]
    public void ModestExcessOrClosedSessionIsIgnored()
    {
        var logic = new SessionFairnessLogic();
        // 50 contre 40 : au-dessus de la part (45) mais pas d'une fois et demie
        Run(logic, 0, 10, 90, (1, 50), (2, 40));
        Assert.False(logic.IsLimited(1));

        Run(logic, 11, 14, 90, (3, 80), (2, 10));
        Assert.True(logic.IsLimited(3));
        Run(logic, 15, 15, 90, (2, 10)); // session 3 fermée : oubliée
        Assert.False(logic.IsLimited(3));
    }

    [Fact]
    public void SettingSurvivesJson()
    {
        var s = new Settings();
        s.ProBalance.SessionFairness = true;
        Assert.True(RuleStore.Clone(s).ProBalance.SessionFairness);
        Assert.False(new Settings().ProBalance.SessionFairness); // désactivé par défaut
        Assert.False(string.IsNullOrEmpty(Sessions.UserName(System.Diagnostics.Process.GetCurrentProcess().SessionId)));
    }
}
