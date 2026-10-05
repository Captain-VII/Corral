using System.Runtime.InteropServices;
using Corral.Models;
using Microsoft.Win32;

namespace Corral.UI;

public sealed record Palette(Color Back, Color Surface, Color Surface2, Color Hover, Color Fore, Color Muted, Color Border, Color Accent);

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
        Back: Color.FromArgb(243, 243, 243), Surface: Color.White, Surface2: Color.FromArgb(230, 230, 230),
        Hover: Color.FromArgb(220, 220, 220), Fore: Color.FromArgb(28, 28, 28), Muted: Color.FromArgb(110, 110, 110),
        Border: Color.FromArgb(200, 200, 200), Accent: Color.FromArgb(28, 128, 108));

    public static readonly Palette Dark = new(
        Back: Color.FromArgb(32, 32, 32), Surface: Color.FromArgb(43, 43, 43), Surface2: Color.FromArgb(52, 52, 52),
        Hover: Color.FromArgb(62, 62, 62), Fore: Color.FromArgb(232, 232, 232), Muted: Color.FromArgb(155, 155, 155),
        Border: Color.FromArgb(75, 75, 75), Accent: Color.FromArgb(38, 160, 136));

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
                case Label label when Equals(label.Tag, HintTag):
                    label.ForeColor = p.Muted;
                    break;
                case Button b:
                    if (IsDark)
                    {
                        b.FlatStyle = FlatStyle.Flat;
                        b.BackColor = p.Surface2;
                        b.ForeColor = p.Fore;
                        b.FlatAppearance.BorderColor = p.Border;
                        b.FlatAppearance.MouseOverBackColor = p.Hover;
                        b.FlatAppearance.MouseDownBackColor = p.Border;
                    }
                    else
                    {
                        b.FlatStyle = FlatStyle.Standard;
                        b.BackColor = SystemColors.Control;
                        b.ForeColor = SystemColors.ControlText;
                        b.UseVisualStyleBackColor = true;
                    }
                    break;
                case CheckBox cb:
                    cb.FlatStyle = IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                    break;
                case ComboBox combo:
                    combo.FlatStyle = IsDark ? FlatStyle.Flat : FlatStyle.Standard;
                    combo.BackColor = p.Surface;
                    combo.ForeColor = p.Fore;
                    break;
                case ListView lv:
                    lv.BackColor = p.Surface;
                    lv.ForeColor = p.Fore;
                    lv.BorderStyle = IsDark ? BorderStyle.None : BorderStyle.Fixed3D; // le relief 3D reste clair
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
                    c.BackColor = p.Surface;
                    c.ForeColor = p.Fore;
                    var border = IsDark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
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

    /// <summary>
    /// En-têtes de ListView dessinés à la main en sombre (le contrôle natif reste clair).
    /// En clair, dessin natif.
    /// </summary>
    public static void EnableThemedHeaders(ListView lv)
    {
        // La dernière colonne occupe la largeur restante : la zone vide de l'en-tête,
        // que le contrôle natif laisse claire en sombre, n'existe plus.
        bool fitting = false;
        void Fit()
        {
            if (fitting || lv.Columns.Count == 0 || !lv.IsHandleCreated)
                return;
            fitting = true;
            try
            {
                var last = lv.Columns[^1];
                int others = 0;
                for (int i = 0; i < lv.Columns.Count - 1; i++)
                    others += lv.Columns[i].Width;
                int width = Math.Max(60, lv.ClientSize.Width - others);
                if (last.Width != width)
                    last.Width = width;
            }
            finally
            {
                fitting = false;
            }
        }
        lv.Resize += (_, _) => Fit();
        lv.ClientSizeChanged += (_, _) => Fit();
        lv.HandleCreated += (_, _) => Fit();
        lv.ColumnWidthChanged += (_, _) => Fit();
        lv.Layout += (_, _) => Fit();
        lv.Tag = (Action)Fit;

        lv.OwnerDraw = true;
        lv.DrawItem += (_, e) => e.DrawDefault = true;
        lv.DrawSubItem += (_, e) => e.DrawDefault = true;
        lv.DrawColumnHeader += (_, e) =>
        {
            if (!IsDark)
            {
                e.DrawDefault = true;
                return;
            }
            var p = Current;
            using (var back = new SolidBrush(p.Surface2))
                e.Graphics.FillRectangle(back, e.Bounds);
            using (var line = new Pen(p.Border))
            {
                e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
                e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            }
            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
                        (e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            var text = Rectangle.Inflate(e.Bounds, -6, 0);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, e.Font, text, p.Fore, flags);
        };
    }

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
