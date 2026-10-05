using Corral.Models;

namespace Corral.Core;

/// <summary>
/// Décide quels processus abaisser ou restaurer. Logique pure (pas d'appel système) pour être testable.
/// Abaissement : CPU système ≥ seuil ET CPU du processus ≥ seuil, tous deux pendant TriggerSeconds.
/// Restauration : CPU du processus &lt; seuil de restauration OU CPU système redescendu sous son seuil,
/// pendant RestoreSeconds ; ou immédiatement si le processus n'est plus éligible (passé au premier plan…).
/// </summary>
public sealed class ProBalanceLogic
{
    public readonly record struct Sample(ProcKey Key, double Cpu, bool Eligible);

    sealed class State
    {
        public DateTime? AboveSince;
        public DateTime? CalmSince;
        public bool Restrained;
    }

    readonly Dictionary<ProcKey, State> states = new();
    DateTime? systemHighSince;

    public bool IsRestrained(ProcKey key) => states.TryGetValue(key, out var s) && s.Restrained;

    public void Reset()
    {
        states.Clear();
        systemHighSince = null;
    }

    public void Evaluate(DateTime now, double systemCpu, IReadOnlyList<Sample> samples, ProBalanceSettings cfg,
        List<ProcKey> toRestrain, List<ProcKey> toRestore)
    {
        var trigger = TimeSpan.FromSeconds(cfg.TriggerSeconds);
        var restoreAfter = TimeSpan.FromSeconds(cfg.RestoreSeconds);
        bool systemHigh = systemCpu >= cfg.SystemThreshold;
        systemHighSince = systemHigh ? systemHighSince ?? now : null;
        bool systemBusy = systemHighSince is { } since && now - since >= trigger;

        var seen = new HashSet<ProcKey>();
        foreach (var s in samples)
        {
            seen.Add(s.Key);
            bool hot = s.Eligible && s.Cpu >= cfg.ProcessThreshold;
            if (!states.TryGetValue(s.Key, out var st))
            {
                if (!hot)
                    continue;
                st = new State();
                states[s.Key] = st;
            }

            if (st.Restrained)
            {
                if (!s.Eligible)
                {
                    toRestore.Add(s.Key);
                    states.Remove(s.Key);
                    continue;
                }
                bool calm = s.Cpu < cfg.RestoreThreshold || !systemHigh;
                if (!calm)
                {
                    st.CalmSince = null;
                    continue;
                }
                st.CalmSince ??= now;
                if (now - st.CalmSince.Value >= restoreAfter)
                {
                    toRestore.Add(s.Key);
                    states.Remove(s.Key);
                }
            }
            else if (hot)
            {
                st.AboveSince ??= now;
                if (systemBusy && now - st.AboveSince.Value >= trigger)
                {
                    st.Restrained = true;
                    st.CalmSince = null;
                    toRestrain.Add(s.Key);
                }
            }
            else
            {
                states.Remove(s.Key);
            }
        }

        foreach (var gone in states.Keys.Where(k => !seen.Contains(k)).ToList())
            states.Remove(gone);
    }
}
