using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AirPlayTray;

// Small on-screen display shown when a media key is pressed: artwork, what happened (next,
// previous, paused, playing) and the track, bottom right above the taskbar. It never takes the
// focus, ignores the mouse, and fades out by itself.
sealed class Osd : Form
{
    const int ShowMs = 2500, FadeStepMs = 15;
    const double FadeStep = 0.08;

    string caption = "", title = "", artist = "";
    Image art;
    readonly System.Windows.Forms.Timer hold = new() { Interval = ShowMs };
    readonly System.Windows.Forms.Timer fade = new() { Interval = FadeStepMs };
    bool dark;

    public Osd()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        Font = SystemFonts.MessageBoxFont;
        hold.Tick += (_, _) => { hold.Stop(); fade.Start(); };
        fade.Tick += (_, _) =>
        {
            if (Opacity > FadeStep) Opacity -= FadeStep;
            else { fade.Stop(); Hide(); }
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_TRANSPARENT (click-through) | WS_EX_NOACTIVATE
            cp.ExStyle |= 0x80 | 0x8 | 0x20 | 0x08000000;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }

    // (Re)shows the OSD with this content and restarts the timer.
    public void Present(string caption, string title, string artist, Image art)
    {
        this.caption = caption; this.title = title; this.artist = artist; this.art = art;
        dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is int v && v == 0;
        BackColor = dark ? Color.FromArgb(0x2B, 0x2B, 0x2B) : Color.FromArgb(0xF9, 0xF9, 0xF9);

        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        int s(int px) => DeviceDpi * px / 96;
        int artSize = s(64), pad = s(14), textW = s(260);
        Size = new Size(pad + (art != null ? artSize + pad : 0) + textW + pad, artSize + 2 * pad);
        // bottom right, above the taskbar's notification area, like Windows' own flyouts
        Location = new Point(screen.Right - Width - s(12), screen.Bottom - Height - s(12));

        fade.Stop();
        Opacity = 0.97;
        if (!Visible) Show();
        Invalidate();
        hold.Stop();
        hold.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int s(int px) => DeviceDpi * px / 96;
        int artSize = s(64), pad = s(14), x = pad;
        if (art != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var clip = Rounded(new Rectangle(x, pad, artSize, artSize), s(6));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.SetClip(clip);
            g.DrawImage(art, new Rectangle(x, pad, artSize, artSize));
            g.ResetClip();
            x += artSize + pad;
        }
        var fg = dark ? Color.White : Color.FromArgb(0x1B, 0x1B, 0x1B);
        var dim = dark ? Color.FromArgb(0xB0, 0xB0, 0xB0) : Color.FromArgb(0x5C, 0x5C, 0x5C);
        int w = Width - x - pad;
        const TextFormatFlags f = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        using var small = new Font(Font.FontFamily, Font.Size * 0.9f);
        using var big = new Font(Font.FontFamily, Font.Size * 1.25f, FontStyle.Bold);
        int h1 = TextRenderer.MeasureText(g, "Ag", small, Size.Empty, f).Height;
        int h2 = TextRenderer.MeasureText(g, "Ag", big, Size.Empty, f).Height;
        int h3 = artist.Length > 0 ? TextRenderer.MeasureText(g, "Ag", Font, Size.Empty, f).Height : 0;
        int y = (Height - h1 - h2 - h3 - s(4)) / 2;
        TextRenderer.DrawText(g, caption, small, new Rectangle(x, y, w, h1), dim, f);
        y += h1 + s(2);
        TextRenderer.DrawText(g, title, big, new Rectangle(x, y, w, h2), fg, f);
        y += h2 + s(2);
        if (artist.Length > 0)
            TextRenderer.DrawText(g, artist, Font, new Rectangle(x, y, w, h3), dim, f);
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
