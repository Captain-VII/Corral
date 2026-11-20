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

    public string? Title
    {
        get => title;
        set
        {
            title = value;
            Invalidate();
        }
    }

    string? title;

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

/// <summary>Cartes sur toute la largeur, en autant de colonnes que la place le permet (chacune dans la plus courte), avec défilement.
/// Les cartes de <see cref="Wide"/> occupent toute la largeur ; <see cref="Grow"/> (large, contenu « fill ») part de sa
/// hauteur minimale et s'étire pour remplir la hauteur restante.</summary>
public sealed class CardStack : Panel
{
    const int Gap = 14, ColumnWidth = 600;

    public CardStack()
    {
        AutoScroll = true;
        Padding = new Padding(0, 0, 8, 0);
    }

    public HashSet<Control> Wide { get; } = new();
    public Control? Grow { get; set; }

    // Page cachée : les cartes paraissent toutes invisibles ; on les place quand elle s'affiche
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
            PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        var cards = Controls.Cast<Control>().Where(c => c.Visible).ToList();
        if (Visible && cards.Count > 0)
        {
            int width = ClientSize.Width - Padding.Horizontal - 2;
            int cols = Math.Clamp((width + Gap) / (ColumnWidth + Gap), 1, Math.Max(1, cards.Count(c => !Wide.Contains(c))));
            int colWidth = (width - Gap * (cols - 1)) / cols;
            var bottoms = new int[cols];
            var origin = AutoScrollPosition;
            int Height(Control c, int w) => c == Grow ? c.MinimumSize.Height : c.GetPreferredSize(new Size(w, 0)).Height;
            foreach (var c in cards)
            {
                if (Wide.Contains(c))
                {
                    int top = bottoms.Max();
                    c.Bounds = new Rectangle(origin.X, origin.Y + top, width, Height(c, width));
                    Array.Fill(bottoms, top + c.Height + Gap);
                }
                else
                {
                    int col = Array.IndexOf(bottoms, bottoms.Min());
                    c.Bounds = new Rectangle(origin.X + col * (colWidth + Gap), origin.Y + bottoms[col], colWidth, Height(c, colWidth));
                    bottoms[col] += c.Height + Gap;
                }
            }
            int extra = ClientSize.Height - (bottoms.Max() - Gap);
            if (Grow is { Visible: true } grow && extra > 0)
            {
                foreach (var c in cards.Where(c => c.Top > grow.Top))
                    c.Top += extra;
                grow.Height += extra;
            }
        }
        base.OnLayout(levent);
    }
}

/// <summary>Barres des 24 dernières heures (la plus récente à droite), repères toutes les 6 heures ; un clic choisit une heure.</summary>
public sealed class HourBars : Control
{
    const int LabelHeight = 18;
    int[] values = new int[24];

    public HourBars()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 96;
        AccessibleRole = AccessibleRole.Chart;
    }

    /// <summary>Texte affiché quand toutes les heures sont à zéro.</summary>
    public string EmptyText { get; set; } = "";

    /// <summary>Heure mise en avant (0 = il y a 23 h … 23 = l'heure en cours), ou -1.</summary>
    public int Highlight
    {
        get => highlight;
        set
        {
            highlight = value;
            Invalidate();
        }
    }

    int highlight = -1;

    /// <summary>Clic sur une barre : son rang (0 à 23).</summary>
    public event Action<int>? BarClicked;

    public void SetValues(int[] v)
    {
        values = v;
        AccessibleDescription = string.Join(" ; ", v.Select((n, i) => $"{DateTime.Now.AddHours(i - 23):HH} h : {n}"));
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposed) => new(proposed.Width, Height);

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (values.Length > 0 && BarClicked != null)
            BarClicked(Math.Clamp(e.X * values.Length / Math.Max(1, Width), 0, values.Length - 1));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(p.Surface);
        int n = values.Length, max = Math.Max(1, values.DefaultIfEmpty().Max());
        float w = (float)Width / Math.Max(1, n), h = Height - LabelHeight;
        using var track = new SolidBrush(p.Surface2);
        using var bar = new SolidBrush(p.Accent);
        using var marked = new SolidBrush(p.Warning);
        var now = DateTime.Now;
        for (int i = 0; i < n; i++)
        {
            var slot = new RectangleF(i * w + 1, 0, Math.Max(1, w - 2), h);
            g.FillRectangle(track, slot);
            if (values[i] > 0)
            {
                float bh = Math.Max(3, h * values[i] / max);
                g.FillRectangle(i == highlight ? marked : bar, slot.X, h - bh, slot.Width, bh);
            }
            int hour = now.AddHours(i - n + 1).Hour;
            if (i < n - 3 && hour % 6 == 0)
                TextRenderer.DrawText(g, $"{hour} h", Font, new Point((int)(i * w), (int)h + 2), p.Muted, TextFormatFlags.NoPrefix);
        }
        TextRenderer.DrawText(g, Tr("maintenant", "now"), Font, new Rectangle(0, (int)h + 2, Width, LabelHeight), p.Muted, TextFormatFlags.Right | TextFormatFlags.NoPrefix);
        if (values.All(v => v == 0) && EmptyText.Length > 0)
            TextRenderer.DrawText(g, EmptyText, Font, new Rectangle(0, 0, Width, (int)h), p.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
    }
}

/// <summary>Navigation latérale : logo, pages avec icône, état et bouton Pause en bas.</summary>
public sealed class NavBar : Control
{
    const int ItemHeight = 38, ItemTop = 76, Side = 10;
    readonly List<(string Glyph, string Text)> items = new();
    readonly ModernButton pause = new(Tr("Mettre en pause", "Pause"));
    readonly ModernButton game = new(Tr("Mode Jeu", "Game Mode"));
    int hover = -1;
    int selected;
    string cpuText = "—", processText = "";
    bool paused;
    bool gameOn;
    string? gameText;
    /// <summary>Chargé une fois : la barre est redessinée à chaque instantané (chaque seconde).</summary>
    readonly Icon logo = AppIcon.Load(new Size(32, 32));

    public NavBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Width = 230;
        Dock = DockStyle.Left;
        TabStop = true;
        pause.Click += (_, _) => PauseRequested?.Invoke(!paused);
        Controls.Add(pause);
        game.Click += (_, _) => GameRequested?.Invoke();
        Controls.Add(game);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            logo.Dispose();
        base.Dispose(disposing);
    }

    public event Action<int>? SelectedChanged;
    public event Action<bool>? PauseRequested;

    /// <summary>Clic sur « Mode Jeu » : la fenêtre décide quoi faire (activer, désactiver, expliquer).</summary>
    public event Action? GameRequested;

    public Control GameButton => game;

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
            if (Focused && IsHandleCreated)
            {
                // Lecteurs d'écran : annonce la page choisie au clavier
                AccessibilityNotifyClients(AccessibleEvents.Selection, value);
                AccessibilityNotifyClients(AccessibleEvents.Focus, value);
            }
        }
    }

    public void SetStatus(double cpu, int processes, bool isPaused, bool gameActive = false, string? gameTrigger = null)
    {
        cpuText = $"{cpu:0} %";
        processText = Tr($"{processes} processus", $"{processes} processes");
        if (paused != isPaused)
        {
            paused = isPaused;
            pause.Text = paused ? Tr("Reprendre", "Resume") : Tr("Mettre en pause", "Pause");
            pause.Primary = paused;
        }
        if (gameOn != gameActive)
        {
            gameOn = gameActive;
            game.Primary = gameActive;
            game.Text = gameActive ? Tr("Mode Jeu actif", "Game Mode on") : Tr("Mode Jeu", "Game Mode");
        }
        gameText = gameActive ? (gameTrigger != null ? Tr($"Mode Jeu : {gameTrigger}", $"Game Mode: {gameTrigger}") : Tr("Mode Jeu activé", "Game Mode on")) : null;
        Invalidate(new Rectangle(0, Height - 200, Width, 110));
        pause.Invalidate();
        game.Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        pause.Bounds = new Rectangle(Side + 6, Height - 52, Width - 2 * Side - 12, 34);
        game.Bounds = new Rectangle(Side + 6, Height - 94, Width - 2 * Side - 12, 34);
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

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
        if (IsHandleCreated && items.Count > 0)
            AccessibilityNotifyClients(AccessibleEvents.Focus, selected);
    }

    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override AccessibleObject CreateAccessibilityInstance() => new NavAccessible(this);

    /// <summary>Pour les lecteurs d'écran : une liste d'onglets (les pages), suivie des boutons Pause et Mode Jeu.</summary>
    sealed class NavAccessible : ControlAccessibleObject
    {
        readonly NavBar nav;

        public NavAccessible(NavBar nav) : base(nav) => this.nav = nav;

        public override AccessibleRole Role => AccessibleRole.PageTabList;
        public override string? Name => Tr("Pages de Corral", "Corral pages");
        public override int GetChildCount() => nav.items.Count + nav.Controls.Count;

        public override AccessibleObject? GetChild(int index) =>
            index < 0 ? null
            : index < nav.items.Count ? new ItemAccessible(nav, this, index)
            : index - nav.items.Count < nav.Controls.Count ? nav.Controls[index - nav.items.Count].AccessibilityObject
            : null;

        public override AccessibleObject? GetFocused() => nav.Focused && nav.items.Count > 0 ? GetChild(nav.selected) : base.GetFocused();
        public override AccessibleObject? GetSelected() => nav.items.Count > 0 ? GetChild(nav.selected) : null;
    }

    sealed class ItemAccessible : AccessibleObject
    {
        readonly NavBar nav;
        readonly AccessibleObject parent;
        readonly int index;

        public ItemAccessible(NavBar nav, AccessibleObject parent, int index)
        {
            this.nav = nav;
            this.parent = parent;
            this.index = index;
        }

        public override string? Name => nav.items[index].Text;
        public override AccessibleRole Role => AccessibleRole.PageTab;
        public override AccessibleObject Parent => parent;
        public override Rectangle Bounds => nav.RectangleToScreen(nav.ItemBounds(index));
        public override string? DefaultAction => Tr("Ouvrir", "Open");
        public override void DoDefaultAction() => nav.Selected = index;

        public override AccessibleStates State
        {
            get
            {
                var state = AccessibleStates.Selectable | AccessibleStates.Focusable;
                if (index == nav.selected)
                    state |= AccessibleStates.Selected | (nav.Focused ? AccessibleStates.Focused : 0);
                return state;
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(p.Back);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Logo + nom
        g.DrawIcon(logo, new Rectangle(Side + 10, 22, 28, 28));
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
        int y = Height - 178;
        using (var sep = new Pen(p.Border))
            g.DrawLine(sep, Side + 6, y, Width - Side - 6, y);
        TextRenderer.DrawText(g, Tr("Processeur", "CPU"), Ui.Base, new Point(Side + 6, y + 12), p.Muted);
        TextRenderer.DrawText(g, cpuText, Ui.Big, new Point(Side + 4, y + 30), p.Fore);
        TextRenderer.DrawText(g, paused ? Tr("En pause · règles suspendues", "Paused · rules suspended") : gameText ?? processText, Ui.Base, new Point(Side + 6, y + 62),
            paused ? p.Warning : gameText != null ? p.Accent : p.Muted);
    }
}

/// <summary>Bandeau « action faite — Annuler » qui disparaît seul après quelques secondes.</summary>
public sealed class UndoBanner : Panel
{
    readonly Label text = new() { AutoSize = true, Margin = new Padding(0, 8, 12, 0) };
    readonly ModernButton undo = new(Tr("Annuler", "Undo")) { Height = 30 };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 8000 };
    Action? onUndo;

    public UndoBanner()
    {
        Dock = DockStyle.Bottom;
        Height = 46;
        Visible = false;
        Padding = new Padding(0, 8, 0, 0);
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        row.Controls.Add(text);
        row.Controls.Add(undo);
        Controls.Add(row);
        undo.Click += (_, _) =>
        {
            var action = onUndo;
            Hide();
            action?.Invoke();
        };
        timer.Tick += (_, _) => Hide();
    }

    /// <summary>Sans action d'annulation, le bandeau sert de simple confirmation.</summary>
    public void Show(string message, Action? undoAction = null)
    {
        text.Text = message;
        onUndo = undoAction;
        undo.Visible = undoAction != null;
        Visible = true;
        timer.Stop();
        timer.Start();
    }

    public new void Hide()
    {
        timer.Stop();
        onUndo = null;
        Visible = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            timer.Dispose();
        base.Dispose(disposing);
    }
}

public sealed class BufferedListView : ListView
{
    public BufferedListView() => DoubleBuffered = true;
}

/// <summary>Classement compact : nom, valeur et barre proportionnelle au premier (le plus gourmand).</summary>
/// <summary>Tuiles « libellé / valeur » (avec barre facultative, 0-1) qui passent à la ligne selon la largeur.</summary>
public sealed class StatGrid : Control
{
    const int Gap = 8;
    IReadOnlyList<(string Label, string Value, double? Fraction)> items = Array.Empty<(string, string, double?)>();

    public StatGrid()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.Table;
    }

    /// <summary>Largeur minimale d'une tuile.</summary>
    public int TileWidth { get; set; } = 150;

    int TileHeight => items.Any(i => i.Fraction != null) ? 60 : 52;

    public void SetItems(IReadOnlyList<(string Label, string Value, double? Fraction)> list)
    {
        items = list;
        AccessibleDescription = string.Join(" ; ", list.Select(i => $"{i.Label} {i.Value}"));
        Invalidate();
    }

    // Lignes équilibrées (2×2 plutôt que 3+1), tuiles élargies pour remplir la ligne
    int Columns(int width)
    {
        int n = Math.Max(1, items.Count), max = Math.Clamp((width + Gap) / (TileWidth + Gap), 1, n), rows = (n + max - 1) / max;
        return (n + rows - 1) / rows;
    }

    public override Size GetPreferredSize(Size proposed)
    {
        int rows = (items.Count + Columns(proposed.Width) - 1) / Columns(proposed.Width);
        return new(proposed.Width, Math.Max(0, rows * (TileHeight + Gap) - Gap));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(p.Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        int cols = Columns(Width), h = TileHeight;
        float w = (Width - Gap * (cols - 1f)) / cols;
        using var back = new SolidBrush(p.Surface2);
        using var bar = new SolidBrush(p.Accent);
        for (int i = 0; i < items.Count; i++)
        {
            var (label, value, fraction) = items[i];
            var tile = new RectangleF(i % cols * (w + Gap), i / cols * (h + Gap), w, h);
            using (var path = Ui.Rounded(tile, 6))
                g.FillPath(back, path);
            var inner = Rectangle.Round(new RectangleF(tile.X + 10, tile.Y + 6, tile.Width - 20, 18));
            TextRenderer.DrawText(g, label, Font, inner, p.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            inner.Offset(0, 20);
            TextRenderer.DrawText(g, value, Ui.Strong, inner, p.Fore, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (fraction is { } f)
            {
                var track = new RectangleF(tile.X + 10, tile.Bottom - 12, tile.Width - 20, 4);
                using (var tp = Ui.Rounded(track, 2))
                using (var tb = new SolidBrush(p.Back))
                    g.FillPath(tb, tp);
                using var fp = Ui.Rounded(new RectangleF(track.X, track.Y, Math.Max(4, track.Width * (float)Math.Clamp(f, 0, 1)), track.Height), 2);
                g.FillPath(bar, fp);
            }
        }
    }
}

public sealed class TopList : Control
{
    IReadOnlyList<(string Name, double Value, string Text)> items = Array.Empty<(string, double, string)>();
    const int RowHeight = 46;

    public TopList()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.List;
    }

    public string EmptyText { get; set; } = Tr("Collecte des mesures…", "Collecting data…");

    /// <summary>Valeur d'une barre pleine (ex. 100 pour des %) ; null = la première ligne.</summary>
    public double? FullValue { get; set; }

    public void SetItems(IReadOnlyList<(string Name, double Value, string Text)> list)
    {
        items = list;
        // Lecteurs d'écran : le classement est lu comme une phrase
        AccessibleDescription = list.Count == 0 ? EmptyText : string.Join(" ; ", list.Select(i => $"{i.Name} {i.Text}"));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(p.Surface);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (items.Count == 0)
        {
            TextRenderer.DrawText(g, EmptyText, Font, ClientRectangle, p.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
            return;
        }
        double max = Math.Max(FullValue ?? items[0].Value, 1e-9);
        for (int i = 0; i < items.Count; i++)
        {
            int y = i * RowHeight;
            var (name, value, text) = items[i];
            TextRenderer.DrawText(g, name, Ui.Base, new Rectangle(0, y, Width - 90, 20), p.Fore, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, text, Ui.Strong, new Rectangle(Width - 90, y, 90, 20), p.Fore, TextFormatFlags.Right);
            var track = new RectangleF(0, y + 24, Width - 1, 6);
            using (var trackPath = Ui.Rounded(track, 3))
            using (var back = new SolidBrush(p.Surface2))
                g.FillPath(back, trackPath);
            float w = (float)Math.Clamp(track.Width * value / max, 6, track.Width);
            using var barPath = Ui.Rounded(new RectangleF(track.X, track.Y, w, track.Height), 3);
            using var bar = new SolidBrush(p.Accent);
            g.FillPath(bar, barPath);
        }
    }

    public override Size GetPreferredSize(Size proposed) => new(proposed.Width, Math.Max(RowHeight, items.Count * RowHeight));
}

/// <summary>Petite fenêtre de saisie d'un texte (nom de profil…).</summary>
public static class InputBox
{
    /// <summary>Renvoie le texte saisi, ou null si l'utilisateur annule.</summary>
    public static string? Ask(IWin32Window owner, string title, string prompt, string initial)
    {
        using var form = new Form
        {
            Text = title,
            Font = Ui.Base,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        var box = new TextBox { Text = initial, Width = 340, Margin = new Padding(0, 6, 0, 0) };
        var ok = new ModernButton("OK", primary: true) { DialogResult = DialogResult.OK };
        var cancel = new ModernButton(Tr("Annuler", "Cancel")) { DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0) };
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        var grid = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(20, 16, 20, 14) };
        grid.Controls.Add(new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(340, 0) });
        grid.Controls.Add(box);
        grid.Controls.Add(buttons);
        form.Controls.Add(grid);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.HandleCreated += (_, _) => Theme.ApplyTitleBar(form);
        Theme.Apply(form);
        return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}

/// <summary>Noms pour les lecteurs d'écran (Narrateur, NVDA) des contrôles qui n'affichent pas leur propre libellé.</summary>
public static class A11y
{
    /// <summary>Nomme les champs de saisie de <paramref name="control"/> d'après le libellé de leur ligne de réglage.</summary>
    public static void Label(Control control, string name, string? description = null)
    {
        foreach (var input in Inputs(control))
        {
            if (string.IsNullOrEmpty(input.AccessibleName))
                input.AccessibleName = name;
            if (description != null && string.IsNullOrEmpty(input.AccessibleDescription))
                input.AccessibleDescription = description;
        }
    }

    static IEnumerable<Control> Inputs(Control c) =>
        c is ComboBox or ToggleSwitch or NumericUpDown or TextBoxBase or TrackBar
            ? new[] { c }
            : c.Controls.Cast<Control>().SelectMany(Inputs);

    /// <summary>Boutons qui n'affichent qu'une icône : leur infobulle leur sert de nom (sans le raccourci entre parenthèses).</summary>
    public static void NameIconButtons(Control root, ToolTip tips)
    {
        foreach (Control c in root.Controls)
        {
            if (c is ButtonBase b && string.IsNullOrEmpty(b.AccessibleName) && IsIconOnly(b.Text) && tips.GetToolTip(b) is { Length: > 0 } tip)
                b.AccessibleName = tip.Split(" (")[0].Split('\n')[0].Trim();
            NameIconButtons(c, tips);
        }
    }

    public static bool IsIconOnly(string text)
    {
        var t = text.Trim();
        return t.Length is > 0 and <= 2 && t.All(ch => ch is >= '' and <= '' or '⋯' or '…' or '+' or '×');
    }
}
