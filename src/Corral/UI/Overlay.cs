using System.Runtime.InteropServices;
using Corral.Core;
using Corral.Models;

namespace Corral.UI;

/// <summary>
/// Mini-fenêtre toujours au premier plan : processeur et mémoire en barres, état (pause, Mode Jeu).
/// Elle ne prend jamais le focus et se déplace à la souris ; sa position est mémorisée.
/// </summary>
public sealed class OverlayWindow : Form
{
    const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2;

    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    readonly OverlaySettings settings;
    EngineSnapshot? snap;

    /// <summary>Double-clic ou « Ouvrir Corral ».</summary>
    public event Action? OpenRequested;

    /// <summary>« Masquer » dans le menu : l'appelant désactive l'option.</summary>
    public event Action? HideRequested;

    /// <summary>La fenêtre a été déplacée (position à enregistrer).</summary>
    public event Action? Moved;

    public OverlayWindow(OverlaySettings settings)
    {
        this.settings = settings;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Font = Ui.Base;
        Size = new Size(210, 62);
        Opacity = 0.93;
        DoubleBuffered = true;
        Text = Tr("Corral — mini-fenêtre", "Corral — mini window");

        var menu = new ContextMenuStrip();
        menu.Items.Add(Tr("Ouvrir Corral", "Open Corral"), null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add(Tr("Masquer la mini-fenêtre", "Hide the mini window"), null, (_, _) => HideRequested?.Invoke());
        Theme.ApplyTo(menu);
        ContextMenuStrip = menu;

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var fallback = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
        var pos = settings.X is { } x && settings.Y is { } y ? new Point(x, y) : fallback;
        // Position mémorisée seulement si elle est encore visible (écran débranché…)
        if (!Screen.AllScreens.Any(s => s.WorkingArea.Contains(new Rectangle(pos, Size))))
            pos = fallback;
        Location = pos;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void SetSnapshot(EngineSnapshot s)
    {
        snap = s;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || e.Clicks > 1)
            return;
        // Déplacement : Windows traite le clic comme sur une barre de titre (retour au relâchement)
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
        if (settings.X != Left || settings.Y != Top)
        {
            settings.X = Left;
            settings.Y = Top;
            Moved?.Invoke();
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        OpenRequested?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(p.Surface);
        using (var border = new Pen(p.Border))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        Bar(g, p, 11, "CPU", snap?.SystemCpu ?? 0);
        Bar(g, p, 35, "RAM", snap?.MemoryPercent ?? 0);

        // Liseré d'état à gauche : accent en Mode Jeu, orange en pause
        if (snap is { } s && (s.Paused || s.GameMode))
        {
            using var mark = new SolidBrush(s.Paused ? p.Warning : p.Accent);
            g.FillRectangle(mark, 0, 0, 4, Height);
        }
    }

    void Bar(Graphics g, Palette p, int y, string label, double value)
    {
        TextRenderer.DrawText(g, label, Ui.Strong, new Point(12, y - 2), p.Muted);
        TextRenderer.DrawText(g, $"{value:0} %", Ui.Strong, new Rectangle(0, y - 2, Width - 12, 18), p.Fore, TextFormatFlags.Right);
        var track = new Rectangle(52, y + 4, Width - 52 - 58, 8);
        using (var back = new SolidBrush(p.Surface2))
            g.FillRectangle(back, track);
        using var fill = new SolidBrush(AppIcon.LoadColor(value));
        g.FillRectangle(fill, track.X, track.Y, (int)(track.Width * Math.Clamp(value, 0, 100) / 100), track.Height);
    }
}
