using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Corral.UI;

/// <summary>Polices et formes communes.</summary>
static class Ui
{
    public static readonly Font Base = new("Segoe UI", 9.75f);
    public static readonly Font Title = new("Segoe UI Semibold", 18f);
    public static readonly Font Section = new("Segoe UI Semibold", 11f);
    public static readonly Font Strong = new("Segoe UI Semibold", 9.75f);
    public static readonly Font Brand = new("Segoe UI Semibold", 13f);
    public static readonly Font Big = new("Segoe UI Semibold", 16f);
    static Font? icons;
    public static Font Icons => icons ??= new Font(IconFamily, 12f); // paresseux : IconFamily est initialisé plus bas

    /// <summary>Police d'icônes de Windows 11 (Segoe Fluent Icons), sinon celle de Windows 10.</summary>
    public static readonly string IconFamily =
        new InstalledFontCollection().Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color Blend(Color a, Color b, double amountOfA) =>
        Color.FromArgb(
            (int)(a.R * amountOfA + b.R * (1 - amountOfA)),
            (int)(a.G * amountOfA + b.G * (1 - amountOfA)),
            (int)(a.B * amountOfA + b.B * (1 - amountOfA)));

    public static Color ParentBack(Control c) => c.Parent?.BackColor ?? Theme.Current.Back;
}

/// <summary>Bouton arrondi : principal (couleur d'accent) ou secondaire. <see cref="Toggled"/> pour les choix segmentés.</summary>
public sealed class ModernButton : Button
{
    bool hover, down;

    public ModernButton(string text, bool primary = false)
    {
        Text = text;
        Primary = primary;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Ui.Base;
        Height = 32;
        Margin = new Padding(4, 0, 4, 0);
        Cursor = Cursors.Hand;
        UpdateWidth();
    }

    public bool Primary { get; set; }

    bool toggled;
    public bool Toggled
    {
        get => toggled;
        set { toggled = value; Invalidate(); }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        UpdateWidth();
    }

    void UpdateWidth() => Width = TextRenderer.MeasureText(Text, Font).Width + 32;

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(Ui.ParentBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);

        Color back, fore, border;
        if (!Enabled)
            (back, fore, border) = (p.Surface2, p.Muted, p.Surface2);
        else if (Primary)
        {
            back = down ? Ui.Blend(p.Accent, Color.Black, 0.8) : hover ? Ui.Blend(p.Accent, Color.White, 0.88) : p.Accent;
            (fore, border) = (Color.White, back);
        }
        else if (Toggled)
            (back, fore, border) = (Ui.Blend(p.Accent, p.Surface, 0.18), p.Fore, p.Accent);
        else
        {
            back = down ? p.Border : hover ? p.Hover : p.Surface2;
            (fore, border) = (p.Fore, p.Border);
        }

        using (var path = Ui.Rounded(r, 5))
        {
            using var brush = new SolidBrush(back);
            g.FillPath(brush, path);
            using var pen = new Pen(border);
            g.DrawPath(pen, path);
        }
        if (Focused && ShowFocusCues)
        {
            using var focus = Ui.Rounded(new RectangleF(2.5f, 2.5f, Width - 5.5f, Height - 5.5f), 4);
            using var pen = new Pen(Primary ? Color.White : p.Accent) { DashStyle = DashStyle.Dot };
            g.DrawPath(pen, focus);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
    }
}

/// <summary>Interrupteur (case à cocher dessinée façon Windows 11). Le libellé est porté par la ligne qui le contient.</summary>
public sealed class ToggleSwitch : CheckBox
{
    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        Size = new Size(44, 24);
        Cursor = Cursors.Hand;
        Margin = new Padding(3, 4, 3, 4);
    }

    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(Ui.ParentBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(1, 2, Width - 3, Height - 5);
        using (var path = Ui.Rounded(track, track.Height / 2))
        {
            if (Checked)
            {
                using var fill = new SolidBrush(Enabled ? p.Accent : p.Border);
                g.FillPath(fill, path);
            }
            else
            {
                using var pen = new Pen(p.Muted, 1.2f);
                g.DrawPath(pen, path);
            }
        }
        float knob = track.Height - 8;
        float x = Checked ? track.Right - knob - 4 : track.Left + 4;
        using (var brush = new SolidBrush(Checked ? Color.White : p.Muted))
            g.FillEllipse(brush, x, track.Top + 4, knob, knob);
        if (Focused && ShowFocusCues)
        {
            using var pen = new Pen(p.Accent) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}

/// <summary>Carte : surface arrondie avec titre optionnel, contenant un seul contrôle.</summary>
public sealed class Card : Panel
{
    const int Pad = 18, TitleHeight = 32;
    readonly Control content;

    public Card(Control content, string? title = null, bool fill = false)
    {
        this.content = content;
        Title = title;
        Fill = fill;
        DoubleBuffered = true;
        ResizeRedraw = true;
        Margin = new Padding(0, 0, 0, 14);
        Padding = fill ? new Padding(6) : new Padding(Pad, Pad + (title == null ? 0 : TitleHeight), Pad, Pad);
        Controls.Add(content);
    }

    public string? Title { get; }

    /// <summary>Le contenu remplit la carte (liste, graphique) au lieu de dicter sa hauteur.</summary>
    public bool Fill { get; }

    public override Size GetPreferredSize(Size proposed)
    {
        int width = Math.Max(proposed.Width, 200);
        var inner = content.GetPreferredSize(new Size(width - Padding.Horizontal, 0));
        return new Size(width, inner.Height + Padding.Vertical);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        content.Bounds = new Rectangle(Padding.Left, Padding.Top,
            Math.Max(0, ClientSize.Width - Padding.Horizontal),
            Fill ? Math.Max(0, ClientSize.Height - Padding.Vertical) : content.GetPreferredSize(new Size(ClientSize.Width - Padding.Horizontal, 0)).Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(Ui.ParentBack(this));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Ui.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 8))
        {
            using var fill = new SolidBrush(p.Surface);
            g.FillPath(fill, path);
            using var pen = new Pen(p.Border);
            g.DrawPath(pen, path);
        }
        if (Title != null)
            TextRenderer.DrawText(g, Title, Ui.Section, new Point(Pad - 2, Pad - 4), p.Fore);
    }
}

/// <summary>Pile verticale de cartes qui prennent toute la largeur (max 900 px), avec défilement.</summary>
public sealed class CardStack : FlowLayoutPanel
{
    public CardStack()
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoScroll = true;
        Padding = new Padding(0, 0, 8, 0);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        int width = Math.Min(900, ClientSize.Width - Padding.Horizontal - 2);
        foreach (Control c in Controls)
        {
            c.Width = width;
            if (c is Card card)
                card.Height = card.GetPreferredSize(new Size(width, 0)).Height;
        }
        base.OnLayout(levent);
    }
}

/// <summary>Navigation latérale : logo, pages avec icône, état et bouton Pause en bas.</summary>
public sealed class NavBar : Control
{
    const int ItemHeight = 40, ItemTop = 76, Side = 10;
    readonly List<(string Glyph, string Text)> items = new();
    readonly ModernButton pause = new("Mettre en pause");
    int hover = -1;
    int selected;
    string cpuText = "—", processText = "";
    bool paused;

    public NavBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Width = 230;
        Dock = DockStyle.Left;
        TabStop = true;
        pause.Click += (_, _) => PauseRequested?.Invoke(!paused);
        Controls.Add(pause);
    }

    public event Action<int>? SelectedChanged;
    public event Action<bool>? PauseRequested;

    public void Add(string glyph, string text)
    {
        items.Add((glyph, text));
        Invalidate();
    }

    public int Selected
    {
        get => selected;
        set
        {
            if (value < 0 || value >= items.Count)
                return;
            selected = value;
            Invalidate();
            SelectedChanged?.Invoke(value);
        }
    }

    public void SetStatus(double cpu, int processes, bool isPaused)
    {
        cpuText = $"{cpu:0} %";
        processText = $"{processes} processus";
        if (paused != isPaused)
        {
            paused = isPaused;
            pause.Text = paused ? "Reprendre" : "Mettre en pause";
            pause.Primary = paused;
        }
        Invalidate(new Rectangle(0, Height - 150, Width, 110));
        pause.Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        pause.Bounds = new Rectangle(Side + 6, Height - 52, Width - 2 * Side - 12, 34);
    }

    Rectangle ItemBounds(int i) => new(Side, ItemTop + i * (ItemHeight + 2), Width - 2 * Side, ItemHeight);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = Enumerable.Range(0, items.Count).FirstOrDefault(i => ItemBounds(i).Contains(e.Location), -1);
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
        if (h != hover) { hover = h; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        if (hover >= 0)
            Selected = hover;
    }

    protected override bool IsInputKey(Keys key) => key is Keys.Up or Keys.Down || base.IsInputKey(key);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Up) Selected = Math.Max(0, selected - 1);
        else if (e.KeyCode == Keys.Down) Selected = Math.Min(items.Count - 1, selected + 1);
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(p.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Logo + nom
        using (var icon = AppIcon.Load(new Size(32, 32)))
            g.DrawIcon(icon, new Rectangle(Side + 10, 22, 28, 28));
        TextRenderer.DrawText(g, "Corral", Ui.Brand, new Point(Side + 46, 23), p.Fore);

        for (int i = 0; i < items.Count; i++)
        {
            var r = ItemBounds(i);
            if (i == selected || i == hover)
            {
                using var path = Ui.Rounded(r, 6);
                using var back = new SolidBrush(i == selected ? p.Surface2 : Color.FromArgb(120, p.Hover));
                g.FillPath(back, path);
            }
            if (i == selected)
            {
                using var pill = Ui.Rounded(new RectangleF(r.Left + 2, r.Top + 12, 3, r.Height - 24), 1.5f);
                using var accent = new SolidBrush(p.Accent);
                g.FillPath(accent, pill);
            }
            var fore = i == selected ? p.Fore : Ui.Blend(p.Fore, p.Back, 0.85);
            TextRenderer.DrawText(g, items[i].Glyph, Ui.Icons, new Rectangle(r.Left + 14, r.Top, 24, r.Height), fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, items[i].Text, i == selected ? Ui.Strong : Ui.Base, new Rectangle(r.Left + 48, r.Top, r.Width - 52, r.Height), fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (i == selected && Focused && ShowFocusCues)
            {
                using var pen = new Pen(p.Accent) { DashStyle = DashStyle.Dot };
                g.DrawRectangle(pen, r.Left + 1, r.Top + 1, r.Width - 3, r.Height - 3);
            }
        }

        // État en bas : CPU, nombre de processus, pause
        int y = Height - 136;
        using (var sep = new Pen(p.Border))
            g.DrawLine(sep, Side + 6, y, Width - Side - 6, y);
        TextRenderer.DrawText(g, "Processeur", Ui.Base, new Point(Side + 6, y + 12), p.Muted);
        TextRenderer.DrawText(g, cpuText, Ui.Big, new Point(Side + 4, y + 30), p.Fore);
        TextRenderer.DrawText(g, paused ? "En pause · règles suspendues" : processText, Ui.Base, new Point(Side + 6, y + 62),
            paused ? p.Warning : p.Muted);
    }
}

public sealed class BufferedListView : ListView
{
    public BufferedListView() => DoubleBuffered = true;
}
