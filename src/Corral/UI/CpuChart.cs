using System.Drawing.Drawing2D;
using Corral.Core;

namespace Corral.UI;

/// <summary>
/// Courbe d'une mesure de tout le PC sur une fenêtre glissante, dessinée en GDI+ aux couleurs du thème.
/// En-tête : titre, valeur actuelle, moyenne et pic de <see cref="History"/>. Le tracé montre <see cref="History"/>,
/// ou les séries de <see cref="Series"/> (ex. lecture et écriture) avec une légende. Échelle fixe (0-100 %) ou adaptée au pic,
/// seuil ProBalance en pointillés, info-bulle au survol.
/// </summary>
public sealed class CpuChart : Control
{
    const int PadRight = 18, PadTop = 82, PadBottom = 30;
    static readonly Font HeroFont = new("Segoe UI Semibold", 20f);
    static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(5); // au-delà, la courbe est coupée

    Point? mouse;

    public CpuChart(CpuHistory history)
    {
        History = history;
        DoubleBuffered = true;
        ResizeRedraw = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AccessibleRole = AccessibleRole.Chart;
    }

    /// <summary>Mesure de l'en-tête (et du tracé, sans <see cref="Series"/>).</summary>
    public CpuHistory History { get; set; }

    /// <summary>Séries tracées à la place de <see cref="History"/>, avec leur nom pour la légende (null = <see cref="History"/> seule).</summary>
    public IReadOnlyList<(CpuHistory History, string Name)>? Series { get; set; }

    public TimeSpan Range { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Haut de l'échelle (100 pour un pourcentage) ; null = adapté au pic, arrondi.</summary>
    public double? Max { get; set; } = 100;

    public Func<double, string> Format { get; set; } = v => $"{v:0} %";

    /// <summary>Seuil affiché en pointillés (null = aucun), avec son libellé.</summary>
    public double? Threshold { get; set; }
    public string ThresholdLabel { get; set; } = Tr("Seuil ProBalance", "ProBalance threshold");

    public string Title { get; set; } = Tr("Processeur", "CPU");

    /// <summary>Ligne d'information sous la moyenne (ex. le nom du processeur).</summary>
    public string? Caption { get; set; }

    /// <summary>Garde sa hauteur dans une pile de cartes (qui demande la hauteur préférée du contenu).</summary>
    public override Size GetPreferredSize(Size proposed) => new(proposed.Width, Height);

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

    /// <summary>Haut d'échelle « rond » (1, 2, 4 ou 8 × 10^n : quarts lisibles) au-dessus de <paramref name="peak"/>.</summary>
    internal static double NiceTop(double peak)
    {
        if (peak <= 0)
            return 1;
        double pow = Math.Pow(10, Math.Floor(Math.Log10(peak)));
        foreach (var m in new[] { 1, 2, 4, 8, 10 })
            if (m * pow >= peak)
                return m * pow;
        return 10 * pow;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(p.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var now = DateTime.UtcNow;
        var from = now - Range;
        var data = History.Since(from);
        var lines = (Series ?? new[] { (History, Title) }).Select((s, i) => (Data: i == 0 && Series == null ? data : s.History.Since(from), s.Name, Color: i == 0 ? p.Accent : p.Warning)).ToList();
        double peak = lines.SelectMany(l => l.Data).Select(d => d.Value).DefaultIfEmpty(0).Max() * 1.1;
        // shortcut: échelle automatique pensée pour des débits (octets/s), arrondie en Ko ou Mo ; à revoir pour une autre unité
        double unit = peak >= 1 << 20 ? 1 << 20 : peak >= 1 << 10 ? 1 << 10 : 1;
        double top = Max ?? NiceTop(peak / unit) * unit;
        var ticks = new[] { 0, 0.25, 0.5, 0.75, 1 }.Select(f => (Value: f * top, Label: Format(f * top))).ToList();
        int padLeft = Math.Max(52, ticks.Max(t => TextRenderer.MeasureText(g, t.Label, Font).Width) + 14);
        var plot = new Rectangle(padLeft, PadTop, Math.Max(10, Width - padLeft - PadRight), Math.Max(10, Height - PadTop - PadBottom));

        float X(DateTime t) => plot.Left + (float)((t - from).TotalMilliseconds / Range.TotalMilliseconds) * plot.Width;
        float Y(double v) => plot.Bottom - (float)(Math.Min(v, top) / top) * plot.Height;

        // En-tête : titre, valeur actuelle en grand, moyenne et pic sur la période, détail éventuel ; légende à droite
        int hx = padLeft - 4;
        TextRenderer.DrawText(g, Title, Ui.Section, new Point(hx, 6), p.Fore);
        var current = data.Count > 0 ? Format(data[^1].Value) : "—";
        TextRenderer.DrawText(g, current, HeroFont, new Point(hx - 2, 28), p.Fore);
        int sx = hx + TextRenderer.MeasureText(g, current, HeroFont).Width + 12;
        var summary = data.Count > 0 ? Tr($"Moyenne {Format(data.Average(d => d.Value))}   ·   Pic {Format(data.Max(d => d.Value))}", $"Average {Format(data.Average(d => d.Value))}   ·   Peak {Format(data.Max(d => d.Value))}") : "";
        TextRenderer.DrawText(g, summary, Font, new Point(sx, 32), p.Muted);
        if (Caption != null)
            TextRenderer.DrawText(g, Caption, Font, new Rectangle(sx, 50, Math.Max(0, Width - sx - PadRight), 18), p.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Series != null)
        {
            int lx = Width - PadRight;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                int w = TextRenderer.MeasureText(g, lines[i].Name, Font).Width;
                lx -= w;
                TextRenderer.DrawText(g, lines[i].Name, Font, new Point(lx, 8), p.Muted);
                using var swatch = new Pen(lines[i].Color, 3);
                g.DrawLine(swatch, lx - 18, 16, lx - 6, 16);
                lx -= 34;
            }
        }

        // Grille horizontale discrète et graduations
        using var gridPen = new Pen(Color.FromArgb(IsDarkSurface(p) ? 45 : 60, p.Muted));
        foreach (var (v, label) in ticks)
        {
            float y = Y(v);
            g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            TextRenderer.DrawText(g, label, Font, new Rectangle(0, (int)y - 8, padLeft - 8, 16), p.Muted,
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
            var label = i == 5 ? Now : Ago(offset);
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

        // Courbes : segments continus (coupés s'il manque des mesures), aire légère sous la première
        g.SetClip(plot);
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            foreach (var segment in Segments(lines[i].Data))
            {
                if (segment.Count < 2)
                    continue;
                var pts = segment.Select(d => new PointF(X(d.Time), Y(d.Value))).ToArray();
                if (i == 0)
                {
                    using var area = new GraphicsPath();
                    area.AddLine(pts[0].X, plot.Bottom, pts[0].X, pts[0].Y);
                    area.AddLines(pts);
                    area.AddLine(pts[^1].X, pts[^1].Y, pts[^1].X, plot.Bottom);
                    area.CloseFigure();
                    using var fill = new SolidBrush(Color.FromArgb(IsDarkSurface(p) ? 55 : 40, p.Accent));
                    g.FillPath(fill, area);
                }
                using var line = new Pen(lines[i].Color, 2) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLines(line, pts);
            }
        }
        g.ResetClip();

        if (lines[0].Data.Count == 0)
            TextRenderer.DrawText(g, Tr("Collecte des mesures…", "Collecting data…"), Font, plot, p.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        DrawHover(g, p, lines, plot, X, Y, now);
    }

    void DrawHover(Graphics g, Palette p, List<(List<(DateTime Time, double Value)> Data, string Name, Color Color)> lines, Rectangle plot,
        Func<DateTime, float> X, Func<double, float> Y, DateTime now)
    {
        var first = lines[0].Data;
        if (mouse is not { } m || first.Count == 0 || m.X < plot.Left || m.X > plot.Right || m.Y < plot.Top - 20 || m.Y > plot.Bottom + 20)
            return;

        var nearest = first.MinBy(d => Math.Abs(X(d.Time) - m.X));
        float x = X(nearest.Time), y = Y(nearest.Value);

        using (var cross = new Pen(Color.FromArgb(140, p.Muted)))
            g.DrawLine(cross, x, plot.Top, x, plot.Bottom);
        var parts = new List<string>();
        foreach (var (data, name, color) in lines)
        {
            if (data.Count == 0)
                continue;
            var d = data.MinBy(d => Math.Abs((d.Time - nearest.Time).Ticks));
            float dy = Y(d.Value);
            // Point de 8 px avec anneau de la couleur du fond
            using (var ring = new SolidBrush(p.Surface))
                g.FillEllipse(ring, x - 6, dy - 6, 12, 12);
            using (var dot = new SolidBrush(color))
                g.FillEllipse(dot, x - 4, dy - 4, 8, 8);
            parts.Add(Series == null ? Format(d.Value) : $"{name} {Format(d.Value)}");
        }

        var ago = now - nearest.Time;
        parts.Add(ago.TotalSeconds < 1.5 ? Now : Tr("il y a " + Ago(ago, precise: true), Ago(ago, precise: true) + " ago"));
        var text = string.Join("   ·   ", parts);
        var size = TextRenderer.MeasureText(g, text, Font);
        var box = new Rectangle((int)x + 12, (int)y - size.Height - 16, size.Width + 16, size.Height + 10);
        if (box.Right > Width - 4) box.X = (int)x - box.Width - 12;
        if (box.Left < 4) box.X = 4;
        if (box.Top < 4) box.Y = (int)y + 12;
        using (var back = new SolidBrush(p.Surface2))
            g.FillRectangle(back, box);
        using (var border = new Pen(p.Border))
            g.DrawRectangle(border, box);
        TextRenderer.DrawText(g, text, Font, box, p.Fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    internal static IEnumerable<List<(DateTime Time, double Value)>> Segments(List<(DateTime Time, double Value)> data)
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

    static string Ago(TimeSpan t, bool precise = false)
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
        AccessibleRole = AccessibleRole.Chart;
    }

    public string Title
    {
        get => title;
        set
        {
            title = value;
            AccessibleName = value;
        }
    }

    string title = "";
    public Func<double, string> Format { get; set; } = v => $"{v:0} %";

    /// <summary>Haut de l'échelle (100 pour un pourcentage) ; null = adapté au pic.</summary>
    public double? Max { get; set; } = 100;
    public TimeSpan Range { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Détail affiché en bas à droite (ex. « ↓ 1,2 Mo/s  ↑ 40 Ko/s »).</summary>
    public string? Caption { get; set; }

    /// <summary>Élément choisi d'une liste (bordure d'accent) ; <see cref="Selectable"/> le rend cliquable et focalisable.</summary>
    public bool Selected
    {
        get => selected;
        set
        {
            selected = value;
            AccessibleRole = AccessibleRole.RadioButton;
            Invalidate();
        }
    }

    bool selected;

    public bool Selectable
    {
        get => GetStyle(ControlStyles.Selectable);
        set
        {
            SetStyle(ControlStyles.Selectable, value);
            TabStop = value;
            Cursor = value ? Cursors.Hand : Cursors.Default;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Selectable && e.KeyCode is Keys.Enter or Keys.Space)
            OnClick(EventArgs.Empty);
        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Selectable)
            Focus();
        base.OnMouseDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

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
        using (var border = new Pen(selected ? p.Accent : p.Border, selected ? 2 : 1))
            g.DrawRectangle(border, selected ? 1 : 0, selected ? 1 : 0, Width - (selected ? 2 : 1), Height - (selected ? 2 : 1));
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, new Rectangle(3, 3, Width - 6, Height - 6));

        TextRenderer.DrawText(g, Title, Font, new Point(10, 8), p.Muted);
        var current = data.Count > 0 ? Format(data[^1].Value) : "—";
        TextRenderer.DrawText(g, current, ValueFont, new Rectangle(0, 4, Width - 10, 24), p.Fore, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        var plot = new Rectangle(10, 34, Width - 20, Height - 56);
        double peak = data.Count > 0 ? data.Max(d => d.Value) : 0;
        double top = Max ?? Math.Max(peak * 1.15, 1);
        var footer = data.Count > 0 ? Tr($"Pic {Format(peak)}", $"Peak {Format(peak)}") : Tr("Collecte des mesures…", "Collecting data…");
        TextRenderer.DrawText(g, footer, Font, new Point(10, plot.Bottom + 4), p.Muted);
        // Détail omis s'il chevaucherait le pic (fenêtre étroite)
        if (Caption != null && 10 + TextRenderer.MeasureText(g, footer, Font).Width + 16 + TextRenderer.MeasureText(g, Caption, Font).Width <= Width - 10)
            TextRenderer.DrawText(g, Caption, Font, new Rectangle(0, plot.Bottom + 4, Width - 10, 18), p.Muted, TextFormatFlags.Right);

        var now = DateTime.UtcNow;
        var from = now - Range;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(Color.FromArgb(p.Surface.GetBrightness() < 0.5f ? 55 : 40, p.Accent));
        using var line = new Pen(p.Accent, 1.6f) { LineJoin = LineJoin.Round };
        foreach (var segment in CpuChart.Segments(data.Where(d => d.Time >= from).ToList())) // coupée s'il manque des mesures
        {
            if (segment.Count < 2)
                continue;
            var pts = segment.Select(d => new PointF(plot.Left + (float)((d.Time - from).TotalMilliseconds / Range.TotalMilliseconds) * plot.Width,
                plot.Bottom - (float)Math.Min(1, d.Value / top) * plot.Height)).ToArray();
            using (var area = new GraphicsPath())
            {
                area.AddLine(pts[0].X, plot.Bottom, pts[0].X, pts[0].Y);
                area.AddLines(pts);
                area.AddLine(pts[^1].X, pts[^1].Y, pts[^1].X, plot.Bottom);
                area.CloseFigure();
                g.FillPath(fill, area);
            }
            g.DrawLines(line, pts);
        }
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
    public static string TB => Tr("To", "TB");
}
