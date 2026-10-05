using System.Drawing.Drawing2D;
using Corral.Core;

namespace Corral.UI;

/// <summary>
/// Courbe du CPU système sur une fenêtre glissante, dessinée en GDI+ aux couleurs du thème.
/// Une seule série (le titre la nomme, pas de légende), axe 0-100 %, seuil ProBalance en pointillés,
/// info-bulle au survol.
/// </summary>
public sealed class CpuChart : Control
{
    const int PadLeft = 52, PadRight = 18, PadTop = 82, PadBottom = 30;
    static readonly Font HeroFont = new("Segoe UI Semibold", 20f);
    static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(5); // au-delà, la courbe est coupée

    readonly CpuHistory history;
    Point? mouse;

    public CpuChart(CpuHistory history)
    {
        this.history = history;
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    public TimeSpan Range { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Seuil affiché en pointillés (null = aucun), avec son libellé.</summary>
    public double? Threshold { get; set; }
    public string ThresholdLabel { get; set; } = Tr("Seuil ProBalance", "ProBalance threshold");

    public string Title { get; set; } = Tr("Processeur", "CPU");

    /// <summary>Ligne d'information sous la moyenne (ex. « 11,8 Go sur 31,9 Go »).</summary>
    public string? Caption { get; set; }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        mouse = e.Location;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        mouse = null;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(p.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var now = DateTime.UtcNow;
        var from = now - Range;
        var data = history.Since(from);
        var plot = new Rectangle(PadLeft, PadTop, Math.Max(10, Width - PadLeft - PadRight), Math.Max(10, Height - PadTop - PadBottom));

        float X(DateTime t) => plot.Left + (float)((t - from).TotalMilliseconds / Range.TotalMilliseconds) * plot.Width;
        float Y(double v) => plot.Bottom - (float)(v / 100.0) * plot.Height;

        // En-tête : titre, valeur actuelle en grand, moyenne et pic sur la période, légende éventuelle
        int hx = PadLeft - 4;
        TextRenderer.DrawText(g, Title, Ui.Section, new Point(hx, 6), p.Fore);
        var current = data.Count > 0 ? $"{data[^1].Value:0} %" : "—";
        TextRenderer.DrawText(g, current, HeroFont, new Point(hx - 2, 28), p.Fore);
        int sx = hx + TextRenderer.MeasureText(g, current, HeroFont).Width + 12;
        var summary = data.Count > 0 ? Tr($"Moyenne {data.Average(d => d.Value):0} %   ·   Pic {data.Max(d => d.Value):0} %", $"Average {data.Average(d => d.Value):0} %   ·   Peak {data.Max(d => d.Value):0} %") : "";
        TextRenderer.DrawText(g, summary, Font, new Point(sx, 32), p.Muted);
        if (Caption != null)
            TextRenderer.DrawText(g, Caption, Font, new Point(sx, 50), p.Muted);

        // Grille horizontale discrète et graduations
        using var gridPen = new Pen(Color.FromArgb(IsDarkSurface(p) ? 45 : 60, p.Muted));
        foreach (var v in new[] { 0, 25, 50, 75, 100 })
        {
            float y = Y(v);
            g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            TextRenderer.DrawText(g, $"{v} %", Font, new Rectangle(0, (int)y - 8, PadLeft - 8, 16), p.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        // Graduations de temps relatives (« -4 min », « -30 s »…)
        // Sur un graphique étroit, une étiquette qui chevaucherait sa voisine est omise
        // (« maintenant », à droite, est toujours affichée).
        int nowWidth = TextRenderer.MeasureText(g, Now, Font).Width;
        int nowLeft = plot.Right - nowWidth;
        int lastRight = int.MinValue;
        for (int i = 0; i <= 5; i++)
        {
            var offset = TimeSpan.FromTicks(Range.Ticks * (5 - i) / 5);
            int x = (int)(plot.Left + plot.Width * i / 5f);
            var label = i == 5 ? Now : Format(offset);
            int w = i == 5 ? nowWidth : TextRenderer.MeasureText(g, label, Font).Width;
            int left = i == 0 ? x : i == 5 ? nowLeft : x - w / 2;
            if (i < 5 && (left < lastRight + 10 || left + w > nowLeft - 10))
                continue;
            TextRenderer.DrawText(g, label, Font, new Point(left, plot.Bottom + 6), p.Muted);
            lastRight = left + w;
        }

        // Seuil ProBalance
        if (Threshold is { } th)
        {
            float y = Y(th);
            using var dash = new Pen(p.Muted, 1) { DashStyle = DashStyle.Dash };
            g.DrawLine(dash, plot.Left, y, plot.Right, y);
            TextRenderer.DrawText(g, $"{ThresholdLabel} {th:0} %", Font, new Rectangle(plot.Left, (int)y - 18, plot.Width - 4, 16), p.Muted,
                TextFormatFlags.Right | TextFormatFlags.Bottom);
        }

        // Courbe : segments continus (coupés s'il manque des mesures), aire légère dessous
        g.SetClip(plot);
        foreach (var segment in Segments(data))
        {
            if (segment.Count < 2)
                continue;
            var pts = segment.Select(d => new PointF(X(d.Time), Y(d.Value))).ToArray();
            using (var area = new GraphicsPath())
            {
                area.AddLine(pts[0].X, plot.Bottom, pts[0].X, pts[0].Y);
                area.AddLines(pts);
                area.AddLine(pts[^1].X, pts[^1].Y, pts[^1].X, plot.Bottom);
                area.CloseFigure();
                using var fill = new SolidBrush(Color.FromArgb(IsDarkSurface(p) ? 55 : 40, p.Accent));
                g.FillPath(fill, area);
            }
            using var line = new Pen(p.Accent, 2) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(line, pts);
        }
        g.ResetClip();

        if (data.Count == 0)
            TextRenderer.DrawText(g, Tr("Collecte des mesures…", "Collecting data…"), Font, plot, p.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        DrawHover(g, p, data, plot, X, Y, now);
    }

    void DrawHover(Graphics g, Palette p, List<(DateTime Time, double Value)> data, Rectangle plot,
        Func<DateTime, float> X, Func<double, float> Y, DateTime now)
    {
        if (mouse is not { } m || data.Count == 0 || m.X < plot.Left || m.X > plot.Right || m.Y < plot.Top - 20 || m.Y > plot.Bottom + 20)
            return;

        var nearest = data.MinBy(d => Math.Abs(X(d.Time) - m.X));
        float x = X(nearest.Time), y = Y(nearest.Value);

        using (var cross = new Pen(Color.FromArgb(140, p.Muted)))
            g.DrawLine(cross, x, plot.Top, x, plot.Bottom);
        // Point de 8 px avec anneau de la couleur du fond
        using (var ring = new SolidBrush(p.Surface))
            g.FillEllipse(ring, x - 6, y - 6, 12, 12);
        using (var dot = new SolidBrush(p.Accent))
            g.FillEllipse(dot, x - 4, y - 4, 8, 8);

        var ago = now - nearest.Time;
        var text = $"{nearest.Value:0.0} %   ·   {(ago.TotalSeconds < 1.5 ? Now : Tr("il y a " + Format(ago, precise: true), Format(ago, precise: true) + " ago"))}";
        var size = TextRenderer.MeasureText(g, text, Font);
        var box = new Rectangle((int)x + 12, (int)y - size.Height - 16, size.Width + 16, size.Height + 10);
        if (box.Right > Width - 4) box.X = (int)x - box.Width - 12;
        if (box.Top < 4) box.Y = (int)y + 12;
        using (var back = new SolidBrush(p.Surface2))
            g.FillRectangle(back, box);
        using (var border = new Pen(p.Border))
            g.DrawRectangle(border, box);
        TextRenderer.DrawText(g, text, Font, box, p.Fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    static IEnumerable<List<(DateTime Time, double Value)>> Segments(List<(DateTime Time, double Value)> data)
    {
        var current = new List<(DateTime Time, double Value)>();
        foreach (var d in data)
        {
            if (current.Count > 0 && d.Time - current[^1].Time > MaxGap)
            {
                yield return current;
                current = new();
            }
            current.Add(d);
        }
        if (current.Count > 0)
            yield return current;
    }

    static string Format(TimeSpan t, bool precise = false)
    {
        if (t.TotalSeconds < 60)
            return $"{t.TotalSeconds:0} s";
        if (precise && t.Seconds != 0)
            return $"{(int)t.TotalMinutes} min {t.Seconds} s";
        return $"{t.TotalMinutes:0} min";
    }

    static bool IsDarkSurface(Palette p) => p.Surface.GetBrightness() < 0.5f;

    static string Now => Tr("maintenant", "now");
}

/// <summary>Petite courbe pour la fiche d'un processus : titre, valeur actuelle, pic, échelle fixe (%) ou automatique.</summary>
public sealed class MiniChart : Control
{
    static readonly Font ValueFont = new("Segoe UI Semibold", 12f);
    List<(DateTime Time, double Value)> data = new();

    public MiniChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(290, 92);
    }

    public string Title { get; set; } = "";
    public Func<double, string> Format { get; set; } = v => $"{v:0} %";

    /// <summary>Haut de l'échelle (100 pour un pourcentage) ; null = adapté au pic.</summary>
    public double? Max { get; set; } = 100;
    public TimeSpan Range { get; set; } = TimeSpan.FromMinutes(5);

    public void SetData(List<(DateTime Time, double Value)> points)
    {
        data = points;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(p.Surface);
        using (var border = new Pen(p.Border))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        TextRenderer.DrawText(g, Title, Font, new Point(10, 8), p.Muted);
        var current = data.Count > 0 ? Format(data[^1].Value) : "—";
        TextRenderer.DrawText(g, current, ValueFont, new Rectangle(0, 4, Width - 10, 24), p.Fore, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        var plot = new Rectangle(10, 34, Width - 20, Height - 56);
        double peak = data.Count > 0 ? data.Max(d => d.Value) : 0;
        double top = Max ?? Math.Max(peak * 1.15, 1);
        TextRenderer.DrawText(g, data.Count > 0 ? Tr($"Pic {Format(peak)}", $"Peak {Format(peak)}") : Tr("Collecte des mesures…", "Collecting data…"), Font,
            new Point(10, plot.Bottom + 4), p.Muted);

        var now = DateTime.UtcNow;
        var from = now - Range;
        var pts = data.Where(d => d.Time >= from)
            .Select(d => new PointF(plot.Left + (float)((d.Time - from).TotalMilliseconds / Range.TotalMilliseconds) * plot.Width,
                plot.Bottom - (float)Math.Min(1, d.Value / top) * plot.Height))
            .ToArray();
        if (pts.Length < 2)
            return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var area = new GraphicsPath())
        {
            area.AddLine(pts[0].X, plot.Bottom, pts[0].X, pts[0].Y);
            area.AddLines(pts);
            area.AddLine(pts[^1].X, pts[^1].Y, pts[^1].X, plot.Bottom);
            area.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(p.Surface.GetBrightness() < 0.5f ? 55 : 40, p.Accent));
            g.FillPath(fill, area);
        }
        using var line = new Pen(p.Accent, 1.6f) { LineJoin = LineJoin.Round };
        g.DrawLines(line, pts);
    }
}

public static class Units
{
    /// <summary>Débit lisible : « 0 Ko/s », « 850 Ko/s », « 12,4 Mo/s ».</summary>
    public static string Rate(double bytesPerSec) =>
        bytesPerSec >= 1 << 20 ? $"{bytesPerSec / (1 << 20):0.0} {MB}/s" : $"{bytesPerSec / 1024:0} {KB}/s";

    public static string KB => Tr("Ko", "KB");
    public static string MB => Tr("Mo", "MB");
    public static string GB => Tr("Go", "GB");
}
