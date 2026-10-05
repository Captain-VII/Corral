using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Corral.UI;

/// <summary>Icône embarquée (tools/make-icon.ps1 génère corral.ico), à la bonne taille pour chaque usage.</summary>
public static class AppIcon
{
    public static Icon Load(Size size)
    {
        try
        {
            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Corral.corral.ico");
            if (stream != null)
                return new Icon(stream, size);
        }
        catch { }
        return SystemIcons.Application;
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// Icône de notification qui affiche la charge du processeur : le nombre sur une pastille
    /// verte, orange au-delà de 60 %, rouge au-delà de 85 %. À libérer avec <see cref="Free"/>.
    /// </summary>
    public static Icon CpuIcon(double cpu, Size size)
    {
        int pct = (int)Math.Round(Math.Clamp(cpu, 0, 100));
        using var bmp = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var path = Rounded(new RectangleF(0, 0, size.Width - 0.5f, size.Height - 0.5f), size.Width / 4f))
            using (var brush = new SolidBrush(LoadColor(pct)))
                g.FillPath(brush, path);
            var text = pct.ToString();
            // Police la plus grande qui tient dans l'icône
            float em = size.Height * 0.8f;
            Font font;
            while (true)
            {
                font = new Font("Segoe UI", em, FontStyle.Bold, GraphicsUnit.Pixel);
                if (g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width <= size.Width - 1 || em < 6)
                    break;
                font.Dispose();
                em -= 0.5f;
            }
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
            using (font)
                g.DrawString(text, font, Brushes.White, new RectangleF(0, 0.5f, size.Width, size.Height), format);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    /// <summary>Vert, orange au-delà de 60 %, rouge au-delà de 85 %.</summary>
    public static Color LoadColor(double pct) =>
        pct >= 85 ? Color.FromArgb(214, 64, 64) : pct >= 60 ? Color.FromArgb(222, 140, 30) : Color.FromArgb(28, 128, 102);

    /// <summary>Libère une icône créée par <see cref="CpuIcon"/>.</summary>
    public static void Free(Icon icon)
    {
        var handle = icon.Handle;
        icon.Dispose();
        DestroyIcon(handle);
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
