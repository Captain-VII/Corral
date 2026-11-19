namespace Corral.Core;

/// <summary>Historique glissant d'une mesure (CPU système, mémoire, débits…), alimenté par le moteur et lu par l'interface (thread-safe).</summary>
public sealed class CpuHistory
{
    readonly object sync = new();
    readonly Queue<(DateTime Time, double Value)> points = new();
    readonly TimeSpan keep;

    readonly double max;

    /// <param name="max">Plafond des valeurs (100 pour un pourcentage).</param>
    public CpuHistory(TimeSpan keep, double max = 100)
    {
        this.keep = keep;
        this.max = max;
    }

    public void Add(DateTime time, double value)
    {
        lock (sync)
        {
            points.Enqueue((time, Math.Clamp(value, 0, max)));
            while (time - points.Peek().Time > keep)
                points.Dequeue();
        }
    }

    public List<(DateTime Time, double Value)> Since(DateTime from)
    {
        lock (sync)
            return points.Where(p => p.Time >= from).OrderBy(p => p.Time).ToList(); // trié : la courbe ne doit jamais revenir en arrière
    }
}
