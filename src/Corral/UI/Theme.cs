using System.Runtime.InteropServices;
using Corral.Models;
using Microsoft.Win32;

namespace Corral.UI;

public sealed record Palette(Color Back, Color Surface, Color Surface2, Color Hover, Color Fore, Color Muted, Color Border, Color Accent, Color Warning);

/// <summary>
/// Thème clair / sombre appliqué à la main (WinForms .NET 8 n'a pas de mode sombre).
/// Les couleurs du formulaire se propagent aux enfants qui ne les fixent pas ;
/// seuls les contrôles « de saisie » et les barres d'outils reçoivent des couleurs explicites.
/// </summary>
public static class Theme
{
    /// <summary>À mettre dans Tag des libellés secondaires (texte grisé).</summary>
    public const string HintTag = "hint";

    public static readonly Palette Light = new(
        Back: Color.FromArgb(243, 243, 243), Surface: Color.White, Surface2: Color.FromArgb(236, 236, 236),
        Hover: Color.FromArgb(226, 226, 226), Fore: Color.FromArgb(26, 26, 26), Muted: Color.FromArgb(104, 104, 104),
        Border: Color.FromArgb(222, 222, 222), Accent: Color.FromArgb(24, 128, 106), Warning: Color.FromArgb(166, 98, 0));

    public static readonly Palette Dark = new(
        Back: Color.FromArgb(32, 32, 32), Surface: Color.FromArgb(43, 43, 43), Surface2: Color.FromArgb(55, 55, 55),
        Hover: Color.FromArgb(64, 64, 64), Fore: Color.FromArgb(235, 235, 235), Muted: Color.FromArgb(160, 160, 160),
        Border: Color.FromArgb(60, 60, 60), Accent: Color.FromArgb(46, 170, 144), Warning: Color.FromArgb(240, 180, 70));

    public static bool IsDark { get; private set; }
    public static Palette Current => IsDark ? Dark : Light;

    /// <summary>Levé quand le thème effectif change.</summary>
    public static event Action? Changed;

    public static void Set(ThemeMode mode)
    {
        bool dark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemPrefersDark());
        if (dark == IsDark)
            return;
        IsDark = dark;
        Changed?.Invoke();
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Apply(Form form)
    {
        var p = Current;
        form.BackColor = p.Back;
        form.ForeColor = p.Fore;
        ApplyTitleBar(form);
        ApplyTo(form, p);
    }

    /// <summary>Barre de titre sombre (Windows 10 20H1+ / 11). À rappeler dans OnHandleCreated.</summary>
    public static void ApplyTitleBar(Form form)
    {
        if (!form.IsHandleCreated)
            return;
        try
        {
            int value = IsDark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch { }
    }

    public static void ApplyTo(ToolStrip strip)
    {
        var p = Current;
        strip.Renderer = new ThemedRenderer(p);
        strip.BackColor = strip is ToolStripDropDown ? p.Surface : p.Back;
        strip.ForeColor = p.Fore;
    }


    static void ApplyTo(Control parent, Palette p)
    {
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case ToolStrip strip:
                    ApplyTo(strip);
                    break;
                case Card card:
                    card.BackColor = p.Surface; // hérité par le contenu de la carte
                    card.Invalidate();
                    break;
                case CardStack stack:
                    WhenHandle(stack, () => SetWindowTheme(stack.Handle, IsDark ? "DarkMode_Explorer" : "Explorer", null));
                    break;
                case NavBar or ModernButton or ToggleSwitch:
                    c.Invalidate();
                    break;
                case Label label when Equals(label.Tag, HintTag):
                    label.ForeColor = p.Muted;
                    break;
                case Button b:
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = p.Surface2;
                    b.ForeColor = p.Fore;
                    b.FlatAppearance.BorderColor = p.Border;
                    b.FlatAppearance.MouseOverBackColor = p.Hover;
                    break;
                case CheckBox cb:
                    cb.FlatStyle = IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                    break;
                case ComboBox combo:
                    combo.FlatStyle = IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                    combo.BackColor = InputBack(p);
                    combo.ForeColor = p.Fore;
                    break;
                case ListView lv:
                    lv.BackColor = p.Surface;
                    lv.ForeColor = p.Fore;
                    lv.BorderStyle = BorderStyle.None; // la carte qui l'entoure fait office de bordure
                    WhenHandle(lv, () =>
                    {
                        SetWindowTheme(lv.Handle, IsDark ? "DarkMode_Explorer" : "Explorer", null);
                        var header = SendMessage(lv.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
                        if (header != IntPtr.Zero)
                            SetWindowTheme(header, IsDark ? "DarkMode_ItemsView" : "ItemsView", null);
                        lv.Invalidate(true);
                    });
                    break;
                case TextBoxBase or NumericUpDown or ListBox:
                    bool flat = Equals(c.Tag, FlatTag);
                    c.BackColor = flat ? p.Surface : InputBack(p);
                    c.ForeColor = p.Fore;
                    var border = flat ? BorderStyle.None : IsDark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
                    switch (c)
                    {
                        case TextBoxBase t: t.BorderStyle = border; break;
                        case NumericUpDown n: n.BorderStyle = border; break;
                        case ListBox l: l.BorderStyle = border; break;
                    }
                    if (c is TextBoxBase or ListBox)
                        WhenHandle(c, () => SetWindowTheme(c.Handle, IsDark ? "DarkMode_Explorer" : "Explorer", null));
                    break;
            }

            if (c.ContextMenuStrip != null)
                ApplyTo(c.ContextMenuStrip);
            if (c is not (ListView or ComboBox or NumericUpDown or TextBoxBase))
                ApplyTo(c, p);
        }
    }

    /// <summary>À mettre dans Tag d'une zone de texte sans bordure, posée dans une carte.</summary>
    public const string FlatTag = "flat";

    /// <summary>Champs de saisie : un cran plus clair que la carte en sombre, pour rester visibles.</summary>
    static Color InputBack(Palette p) => IsDark ? p.Surface2 : p.Surface;

    public sealed record CellStyle(string? Text = null, Color? Fore = null, Color? Back = null, Image? Icon = null);

    /// <summary>Infobulle dessinée aux couleurs du thème courant (lu au moment de l'affichage).</summary>
    public static ToolTip CreateToolTip()
    {
        var tip = new ToolTip { OwnerDraw = true, InitialDelay = 400, AutoPopDelay = 15_000, ReshowDelay = 100 };
        tip.Popup += (_, e) =>
        {
            var size = TextRenderer.MeasureText(tip.GetToolTip(e.AssociatedControl!), Ui.Base, new Size(420, 0), TextFormatFlags.WordBreak);
            e.ToolTipSize = new Size(Math.Min(size.Width, 420) + 20, size.Height + 14);
        };
        tip.Draw += (_, e) =>
        {
            var p = Current;
            using (var back = new SolidBrush(p.Surface2))
                e.Graphics.FillRectangle(back, e.Bounds);
            using (var border = new Pen(p.Border))
                e.Graphics.DrawRectangle(border, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, Ui.Base, Rectangle.Inflate(e.Bounds, -10, -7), p.Fore, TextFormatFlags.WordBreak);
        };
        return tip;
    }

    /// <summary>
    /// Liste entièrement dessinée (les deux thèmes) : lignes de 32 px, en-tête discret, sélection teintée d'accent.
    /// <paramref name="cell"/> personnalise une cellule (texte, couleurs) ; <paramref name="sort"/> indique la colonne triée.
    /// La dernière colonne occupe la largeur restante (le contrôle natif laisserait une zone d'en-tête non dessinée).
    /// </summary>
    public static void StyleList(ListView lv, Func<ListViewItem, int, CellStyle?>? cell = null, Func<int, SortOrder>? sort = null)
    {
        // Les largeurs initiales servent de proportions : les colonnes remplissent toujours la largeur
        // disponible (pas de défilement horizontal, pas de zone d'en-tête vide). On ne recalcule que si
        // la largeur change, pour respecter une colonne redimensionnée à la main.
        int[]? weights = null;
        int fittedWidth = -1;
        bool fitting = false;
        void Fit()
        {
            if (fitting || lv.Columns.Count == 0 || !lv.IsHandleCreated)
                return;
            int client = lv.ClientSize.Width;
            if (client <= 0 || client == fittedWidth)
                return;
            fitting = true;
            try
            {
                weights ??= lv.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray();
                int total = weights.Sum(), used = 0;
                for (int i = 0; i < lv.Columns.Count; i++)
                {
                    int w = i == lv.Columns.Count - 1 ? client - used : Math.Max(50, client * weights[i] / total);
                    lv.Columns[i].Width = Math.Max(50, w);
                    used += lv.Columns[i].Width;
                }
                fittedWidth = client;
            }
            finally
            {
                fitting = false;
            }
        }
        lv.Resize += (_, _) => Fit();
        lv.ClientSizeChanged += (_, _) => Fit();
        lv.HandleCreated += (_, _) => Fit();
        lv.Layout += (_, _) => Fit();
        lv.Tag = (Action)Fit;

        lv.SmallImageList = new ImageList { ImageSize = new Size(1, 32) }; // hauteur des lignes
        lv.OwnerDraw = true;
        lv.DrawItem += (_, _) => { }; // tout est dessiné cellule par cellule
        lv.DrawSubItem += (_, e) =>
        {
            if (e.Item == null || e.SubItem == null)
                return;
            var p = Current;
            // Pour la 1re colonne, e.Bounds couvre toute la ligne : on la ramène à sa largeur.
            var bounds = e.ColumnIndex == 0 ? new Rectangle(e.Bounds.X, e.Bounds.Y, lv.Columns[0].Width, e.Bounds.Height) : e.Bounds;
            var style = cell?.Invoke(e.Item, e.ColumnIndex);
            var back = e.Item.Selected ? Ui.Blend(p.Accent, p.Surface, IsDark ? 0.30 : 0.16) : style?.Back ?? p.Surface;
            using (var brush = new SolidBrush(back))
                e.Graphics.FillRectangle(brush, bounds);
            var textBounds = Rectangle.Inflate(bounds, -10, 0);
            if (style?.Icon is { } icon)
            {
                e.Graphics.DrawImage(icon, textBounds.Left, bounds.Top + (bounds.Height - 16) / 2, 16, 16);
                textBounds = new Rectangle(textBounds.Left + 24, textBounds.Top, Math.Max(0, textBounds.Width - 24), textBounds.Height);
            }
            var align = lv.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
            TextRenderer.DrawText(e.Graphics, style?.Text ?? e.SubItem.Text, lv.Font, textBounds, style?.Fore ?? p.Fore,
                align | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        lv.DrawColumnHeader += (_, e) =>
        {
            var p = Current;
            using (var back = new SolidBrush(p.Surface))
                e.Graphics.FillRectangle(back, e.Bounds);
            using (var line = new Pen(p.Border))
                e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var text = e.Header?.Text ?? "";
            var order = sort?.Invoke(e.ColumnIndex) ?? SortOrder.None;
            if (order != SortOrder.None)
                text += order == SortOrder.Ascending ? "  ▲" : "  ▼";
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                        (e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, text, Ui.Strong, Rectangle.Inflate(e.Bounds, -10, 0), p.Muted, flags);
        };
    }

    /// <summary>Redessine l'en-tête (fenêtre native distincte de la liste), par exemple après un changement de tri.</summary>
    public static void InvalidateHeader(ListView lv)
    {
        if (!lv.IsHandleCreated)
            return;
        var header = SendMessage(lv.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        if (header != IntPtr.Zero)
            InvalidateRect(header, IntPtr.Zero, true);
    }

    [DllImport("user32.dll")]
    static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    /// <summary>Réajuste la dernière colonne (à appeler après un ajout d'éléments, la barre de défilement a pu apparaître).</summary>
    public static void FitColumns(ListView lv)
    {
        if (lv.Tag is Action fit)
            fit();
    }

    static void WhenHandle(Control c, Action action)
    {
        if (c.IsHandleCreated)
        {
            action();
            return;
        }
        void Handler(object? s, EventArgs e)
        {
            c.HandleCreated -= Handler;
            action();
        }
        c.HandleCreated += Handler;
    }

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int LVM_GETHEADER = 0x101F;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>Rendu des barres d'outils, menus et barre d'état aux couleurs du thème.</summary>
sealed class ThemedRenderer : ToolStripProfessionalRenderer
{
    readonly Palette p;

    public ThemedRenderer(Palette palette) : base(new ThemedColors(palette))
    {
        p = palette;
        RoundedEdges = false;
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? p.Fore : p.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = p.Fore;
        base.OnRenderArrow(e);
    }

    /// <summary>Boutons de navigation : fond discret au survol, barre d'accent sous la page active.</summary>
    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not ToolStripButton b)
        {
            base.OnRenderButtonBackground(e);
            return;
        }
        var r = new Rectangle(Point.Empty, e.Item.Size);
        if (b.Checked || b.Selected || b.Pressed)
        {
            using var back = new SolidBrush(b.Checked ? p.Surface2 : p.Hover);
            e.Graphics.FillRectangle(back, r);
        }
        if (b.Checked)
        {
            using var accent = new SolidBrush(p.Accent);
            e.Graphics.FillRectangle(accent, r.Left, r.Bottom - 3, r.Width, 3);
        }
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            using var pen = new Pen(p.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }
        else
        {
            using var pen = new Pen(p.Border);
            e.Graphics.DrawLine(pen, 0, e.AffectedBounds.Bottom - 1, e.AffectedBounds.Right, e.AffectedBounds.Bottom - 1);
        }
    }
}

sealed class ThemedColors : ProfessionalColorTable
{
    readonly Palette p;

    public ThemedColors(Palette palette)
    {
        p = palette;
        UseSystemColors = false;
    }

    public override Color ToolStripGradientBegin => p.Back;
    public override Color ToolStripGradientMiddle => p.Back;
    public override Color ToolStripGradientEnd => p.Back;
    public override Color ToolStripBorder => p.Border;
    public override Color StatusStripGradientBegin => p.Back;
    public override Color StatusStripGradientEnd => p.Back;
    public override Color MenuStripGradientBegin => p.Back;
    public override Color MenuStripGradientEnd => p.Back;
    public override Color ToolStripDropDownBackground => p.Surface;
    public override Color ImageMarginGradientBegin => p.Surface;
    public override Color ImageMarginGradientMiddle => p.Surface;
    public override Color ImageMarginGradientEnd => p.Surface;
    public override Color MenuBorder => p.Border;
    public override Color MenuItemBorder => p.Hover;
    public override Color MenuItemSelected => p.Hover;
    public override Color MenuItemSelectedGradientBegin => p.Hover;
    public override Color MenuItemSelectedGradientEnd => p.Hover;
    public override Color MenuItemPressedGradientBegin => p.Surface2;
    public override Color MenuItemPressedGradientMiddle => p.Surface2;
    public override Color MenuItemPressedGradientEnd => p.Surface2;
    public override Color CheckBackground => p.Accent;
    public override Color CheckSelectedBackground => p.Accent;
    public override Color CheckPressedBackground => p.Accent;
    public override Color SeparatorDark => p.Border;
    public override Color SeparatorLight => p.Surface;
    public override Color GripDark => p.Border;
    public override Color GripLight => p.Back;
}
