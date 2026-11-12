using System.Diagnostics;
using Corral.Core;
using Corral.Models;

namespace Corral.Tests;

/// <summary>Tests lancés seuls : ils mesurent les handles du processus de test, que les autres tests feraient varier.</summary>
[CollectionDefinition("Endurance", DisableParallelization = true)]
public sealed class EnduranceCollection { }

/// <summary>
/// Le service tourne des semaines : une fuite d'un handle par passage (une par seconde) finit par le bloquer.
/// Les blocages de la 3.1.3 et de la 3.1.4 n'étaient visibles qu'à la longue.
/// </summary>
[Collection("Endurance")]
public class EnduranceTests
{
    sealed class NoPower : IPowerPlanApi
    {
        public Guid? GetActive() => null;
        public bool SetActive(Guid plan) => true;
    }

    static int Handles()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var self = Process.GetCurrentProcess();
        return self.HandleCount;
    }

    [Fact]
    public void EnginePassesDoNotLeakHandles()
    {
        var s = new Settings();
        s.ProBalance.Enabled = false;
        var engine = new Engine(s, new NoPower(), foregroundPid: () => -1);
        for (int i = 0; i < 20; i++) // mise en route : caches, premiers passages
            engine.TickOnce();
        int before = Handles();
        for (int i = 0; i < 300; i++)
            engine.TickOnce();
        int after = Handles();
        engine.Stop();
        // Une fuite par passage en donnerait au moins 300 ; on laisse de la marge aux processus du runner qui vont et viennent
        Assert.True(after - before < 100, $"handles : {before} avant, {after} après 300 passages");
    }
}
