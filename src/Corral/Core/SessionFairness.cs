using System.Runtime.InteropServices;
using Corral.Models;

namespace Corral.Core;

/// <summary>Charge d'une session utilisateur (serveur RDS, VDI multi-session…), pour l'interface.</summary>
public sealed record SessionUsage(int Id, string User, double Cpu, bool Limited);

/// <summary>
/// Équité entre sessions : quand le processeur est saturé et que plusieurs sessions sont ouvertes, une session qui
/// prend nettement plus que sa part (plus de <see cref="GreedyFactor"/> fois la part équitable) voit ses programmes
/// actifs abaissés, jusqu'à ce qu'elle repasse sous sa part ou que le système se calme. Logique pure, testable.
/// Les seuils et durées sont ceux de ProBalance.
/// </summary>
public sealed class SessionFairnessLogic
{
    public const double GreedyFactor = 1.5;

    sealed class State
    {
        public DateTime? AboveSince;
        public DateTime? CalmSince;
        public bool Limited;
    }

    readonly Dictionary<int, State> states = new();
    DateTime? busySince;

    public bool IsLimited(int session) => states.TryGetValue(session, out var s) && s.Limited;

    public void Reset()
    {
        states.Clear();
        busySince = null;
    }

    /// <param name="sessionCpu">CPU (% du total) de chaque session utilisateur (la session 0 des services est exclue).</param>
    public void Evaluate(DateTime now, double systemCpu, IReadOnlyDictionary<int, double> sessionCpu, ProBalanceSettings cfg)
    {
        var trigger = TimeSpan.FromSeconds(cfg.TriggerSeconds);
        var restoreAfter = TimeSpan.FromSeconds(cfg.RestoreSeconds);
        bool high = systemCpu >= cfg.SystemThreshold;
        busySince = high ? busySince ?? now : null;
        bool busy = busySince is { } since && now - since >= trigger;

        // Seules les sessions qui travaillent comptent pour la part équitable
        int active = sessionCpu.Count(kv => kv.Value >= 1);
        double fair = active >= 2 ? systemCpu / active : double.MaxValue;

        foreach (var gone in states.Keys.Where(id => !sessionCpu.ContainsKey(id)).ToList())
            states.Remove(gone);
        foreach (var (id, cpu) in sessionCpu)
        {
            var st = states.TryGetValue(id, out var existing) ? existing : states[id] = new State();
            if (st.Limited)
            {
                bool calm = cpu <= fair || !high;
                if (!calm)
                {
                    st.CalmSince = null;
                    continue;
                }
                st.CalmSince ??= now;
                if (now - st.CalmSince.Value >= restoreAfter)
                    (st.Limited, st.CalmSince, st.AboveSince) = (false, null, null);
            }
            else if (cpu > fair * GreedyFactor && cpu >= cfg.ProcessThreshold)
            {
                st.AboveSince ??= now;
                if (busy && now - st.AboveSince.Value >= trigger)
                    (st.Limited, st.CalmSince) = (true, null);
            }
            else
            {
                st.AboveSince = null;
            }
        }
    }
}

/// <summary>Nom d'utilisateur des sessions Windows (Services Bureau à distance).</summary>
public static class Sessions
{
    const int WTSUserName = 5, WTSDomainName = 7;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);

    static string? Query(int session, int infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out var buffer, out _))
            return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    /// <summary>« DOMAINE\utilisateur », ou « Session n » si personne n'y est connecté.</summary>
    public static string UserName(int session)
    {
        var user = Query(session, WTSUserName);
        if (string.IsNullOrEmpty(user))
            return Tr($"Session {session}", $"Session {session}");
        var domain = Query(session, WTSDomainName);
        return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
    }
}
