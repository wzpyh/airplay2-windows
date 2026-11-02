using System.Drawing.Drawing2D;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AirPlayTray;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var single = new Mutex(true, @"Local\airplay2-tray", out bool first);
        if (!first) return;
        AppIdentity.Register();
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

// What the AirPlay source is playing, merged from the updateMRNowPlayingInfo messages.
sealed class NowPlaying
{
    public bool Session;          // a source is connected and streaming
    public bool Known;            // false until the first event: the tray may start mid-session
    public bool Usable => Session || !Known;
    public string Title = "", Artist = "", Album = "";
    public bool Playing;
    public Image Artwork;
    public byte[] ArtworkData;   // the original image, for the Windows media controls

    public void Clear()
    {
        Session = false; Playing = false;
        Title = Artist = Album = "";
        Artwork?.Dispose(); Artwork = null; ArtworkData = null;
    }
}

sealed class TrayContext : ApplicationContext
{
    const int MetadataPort = 5555;   // metadata.socket_port in shairport-sync.conf
    const int RemotePort = 5557;     // SPS_REMOTE_UDP_PORT, see patches/shairport-sync-remote.patch

    static readonly bool Zh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
    static string T(string en, string zh) => Zh ? zh : en;

    readonly NotifyIcon icon = new();
    readonly ContextMenuStrip menu = new();
    readonly NowPlayingItem header = new();
    readonly ToolStripMenuItem prevItem, playItem, nextItem, mediaKeysItem;
    readonly HiddenWindow window;
    readonly Smtc smtc;   // null if the Windows media controls are not available
    readonly NowPlaying np = new();
    System.Threading.Timer statusTimer;
    readonly System.Windows.Forms.Timer autoClose = new() { Interval = 15000 };
    readonly UdpClient sender = new(AddressFamily.InterNetwork);
    readonly Dictionary<(string, string), SortedDictionary<uint, byte[]>> chunks = new();
    Icon activeIcon, idleIcon;
    string listenError;

    public TrayContext()
    {
        window = new HiddenWindow(OnHotKey);
        smtc = Smtc.TryCreate(window.Handle, b => window.BeginInvoke(() => OnSmtcButton(b)));

        prevItem = new ToolStripMenuItem(T("Previous", "上一首"), null, (_, _) => Send("prev"));
        playItem = new ToolStripMenuItem(T("Play / Pause", "播放 / 暂停"), null, (_, _) => Send("toggle"));
        nextItem = new ToolStripMenuItem(T("Next", "下一首"), null, (_, _) => Send("next"));
        mediaKeysItem = new ToolStripMenuItem(T("Media keys always control AirPlay", "媒体键只控制 AirPlay"), null, (_, _) =>
        {
            mediaKeysItem.Checked = !mediaKeysItem.Checked;
            Settings.MediaKeys = mediaKeysItem.Checked;
            UpdateHotKeys();
        }) { Checked = Settings.MediaKeys };
        menu.Items.AddRange(new ToolStripItem[]
        {
            header, new ToolStripSeparator(), prevItem, playItem, nextItem, new ToolStripSeparator(), mediaKeysItem,
            new ToolStripMenuItem(T("Exit", "退出"), null, (_, _) => ExitThread()),
        });

        // no image column (the artwork is drawn by the header item), but keep the check column
        // for "media keys"
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = true;
        MakeIcons();
        SystemEvents.UserPreferenceChanged += OnThemeChanged;
        icon.MouseUp += (_, e) =>
        {
            // left or right click: the menu. Shown by hand: the menu must belong to the foreground
            // window, or it can open behind the taskbar and not close on a click elsewhere.
            if (e.Button is MouseButtons.Left or MouseButtons.Right)
            {
                HiddenWindow.SetForegroundWindow(window.Handle);
                menu.Show(Cursor.Position, ToolStripDropDownDirection.AboveLeft);
            }
            // middle click: next track without opening the menu
            else if (e.Button == MouseButtons.Middle && np.Usable)
                Send("next");
        };
        // the transport items keep the menu open, so skipping several tracks is several clicks in
        // place while the header follows along; Esc or a click elsewhere closes it
        menu.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked &&
                (prevItem.Selected || playItem.Selected || nextItem.Selected))
            {
                e.Cancel = true;
                // ...but only for a while: 15 s after the last click it closes by itself
                autoClose.Stop();
                autoClose.Start();
            }
        };
        menu.Closed += (_, _) => autoClose.Stop();
        autoClose.Tick += (_, _) => { autoClose.Stop(); menu.Close(); };
        icon.Visible = true;

        var listener = new Thread(Listen) { IsBackground = true, Name = "metadata" };
        listener.Start();
        Refresh();
    }

    // ---- metadata from shairport-sync -------------------------------------------------------

    void Listen()
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = true };
            // artwork arrives as ~60 KB pieces back to back; the 64 KB default buffer drops some
            udp.Client.ReceiveBufferSize = 1 << 20;
            udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, MetadataPort));
            // don't let an ICMP "port unreachable" (receiver not running) fail later receives
            const int SIO_UDP_CONNRESET = -1744830452;
            udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
        }
        catch (SocketException e)
        {
            listenError = $"UDP {MetadataPort}: {e.Message}";
            window.BeginInvoke(Refresh);
            return;
        }
        // catch up with a session that is already playing: "status" makes shairport-sync reply to
        // this socket with pbeg + the latest now-playing info, or pend. The receiver may still be starting (at logon, or right after an install), so ask
        // every 3 s until something has been heard, for up to a minute.
        var status = System.Text.Encoding.ASCII.GetBytes("status");
        int asked = 0;
        statusTimer = new System.Threading.Timer(_ =>
        {
            if (np.Known || ++asked > 20) { statusTimer.Dispose(); return; }
            try { udp.Send(status, status.Length, new IPEndPoint(IPAddress.Loopback, RemotePort)); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }, null, 0, 3000);
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            byte[] pkt;
            try { pkt = udp.Receive(ref from); }
            catch (SocketException) { continue; }
            if (pkt.Length < 8) continue;
            string type = Ascii(pkt, 0), code = Ascii(pkt, 4);
            if (type == "ssnc" && code == "chnk")
            {
                // ssnc chnk <u32 index> <u32 total> <type> <code> <payload>: one large item in pieces
                if (pkt.Length < 24) continue;
                uint ix = BE(pkt, 8), total = BE(pkt, 12);
                var key = (Ascii(pkt, 16), Ascii(pkt, 20));
                if (!chunks.TryGetValue(key, out var parts) || ix == 0) chunks[key] = parts = new();
                parts[ix] = pkt[24..];
                if (parts.Count == total)
                {
                    chunks.Remove(key);
                    Handle(key.Item1, key.Item2, parts.Values.SelectMany(p => p).ToArray());
                }
            }
            else Handle(type, code, pkt[8..]);
        }
    }

    static string Ascii(byte[] b, int at) => System.Text.Encoding.ASCII.GetString(b, at, 4);
    static uint BE(byte[] b, int at) => (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);

    // runs on the listener thread; state changes are posted to the UI thread
    void Handle(string type, string code, byte[] data)
    {
        if (type != "ssnc") return;
        switch (code)
        {
            case "pbeg": // play session begins
            case "prsm": // resumes after a pause or flush
                window.BeginInvoke(() => { np.Session = np.Known = true; Refresh(); });
                break;
            case "pend": // play session ends
            case "disc": // source disconnected
                window.BeginInvoke(() => { np.Clear(); np.Known = true; Refresh(); });
                break;
            case "copl": // AirPlay 2 command from the source, as a binary plist
                Dictionary<string, object> msg;
                try { msg = BinaryPlist.Parse(data) as Dictionary<string, object>; }
                catch (FormatException) { return; }
                if (msg != null && msg.TryGetValue("type", out var t) && (t as string) == "updateMRNowPlayingInfo")
                {
                    // { type: updateMRNowPlayingInfo,
                    //   params: { type: npi-text, mergePolicy: replace|update, params: { kMRMediaRemoteNowPlayingInfo... } } }
                    var outer = msg.TryGetValue("params", out var p) ? p as Dictionary<string, object> : null;
                    var info = outer != null && outer.TryGetValue("params", out var pp) ? pp as Dictionary<string, object> : null;
                    bool replace = outer != null && outer.TryGetValue("mergePolicy", out var mp) && (mp as string) == "replace";
                    if (info != null) window.BeginInvoke(() => Merge(info, replace));
                }
                break;
        }
    }

    void Merge(Dictionary<string, object> info, bool replace)
    {
        const string k = "kMRMediaRemoteNowPlayingInfo";
        if (replace)
        {
            np.Title = np.Artist = np.Album = "";
            np.Artwork?.Dispose(); np.Artwork = null; np.ArtworkData = null;
        }
        np.Session = np.Known = true;
        if (info.TryGetValue(k + "Title", out var v) && v is string title) np.Title = title;
        if (info.TryGetValue(k + "Artist", out v) && v is string artist) np.Artist = artist;
        if (info.TryGetValue(k + "Album", out v) && v is string album) np.Album = album;
        if (info.TryGetValue(k + "PlaybackRate", out v)) np.Playing = Convert.ToDouble(v, CultureInfo.InvariantCulture) > 0;
        if (info.TryGetValue(k + "ArtworkData", out v) && v is byte[] art && art.Length > 0)
        {
            try
            {
                using var ms = new MemoryStream(art);
                using var img = Image.FromStream(ms);
                np.Artwork?.Dispose();
                np.Artwork = Thumbnail(img, 128);
                np.ArtworkData = art;
            }
            catch (ArgumentException) { } // not an image GDI+ can read
        }
        Refresh();
    }

    static Bitmap Thumbnail(Image img, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(img, 0, 0, size, size);
        return bmp;
    }

    // ---- UI -----------------------------------------------------------------------------------

    void Refresh()
    {
        string line1, line2 = "";
        if (listenError != null) line1 = T("Cannot read metadata: ", "无法读取曲目信息：") + listenError;
        else if (!np.Known) line1 = "AirPlay";
        else if (!np.Session) line1 = T("Nothing is playing over AirPlay", "没有 AirPlay 在播放");
        else if (np.Title.Length == 0) line1 = T("AirPlay connected", "AirPlay 已连接");
        else
        {
            line1 = np.Title;
            line2 = string.Join(" — ", new[] { np.Artist, np.Album }.Where(s => s.Length > 0));
        }

        header.Set(line1, line2, np.Session ? np.Artwork : null);
        prevItem.Enabled = playItem.Enabled = nextItem.Enabled = np.Usable;
        playItem.Text = !np.Known ? T("Play / Pause", "播放 / 暂停") : np.Playing ? T("Pause", "暂停") : T("Play", "播放");

        string tip = np.Session && np.Title.Length > 0
            ? (np.Playing ? "" : "⏸ ") + np.Title + (np.Artist.Length > 0 ? " — " + np.Artist : "")
            : line1;
        icon.Text = tip.Length > 127 ? tip[..126] + "…" : tip;
        icon.Icon = np.Session ? activeIcon : idleIcon;
        UpdateHotKeys();
        smtc?.Update(np.Session, np.Playing, np.Title, np.Artist, np.Album, np.ArtworkData);
        UpdateOsd();
    }

    // Windows media controls: media keys (when not grabbed by the hotkeys) and the buttons on the
    // Quick Settings / volume flyout media card
    void OnSmtcButton(Smtc.Button b)
    {
        switch (b)
        {
            case Smtc.Button.Play: Send("play"); ShowOsd(OsdAction.Toggle); break;
            case Smtc.Button.Pause:
            case Smtc.Button.Stop: Send("pause"); ShowOsd(OsdAction.Toggle); break;
            case Smtc.Button.Next: Send("next"); ShowOsd(OsdAction.Next); break;
            case Smtc.Button.Previous: Send("prev"); ShowOsd(OsdAction.Previous); break;
        }
    }

    void Send(string word)
    {
        var b = System.Text.Encoding.ASCII.GetBytes(word);
        try { sender.Send(b, b.Length, new IPEndPoint(IPAddress.Loopback, RemotePort)); }
        catch (SocketException) { }
    }

    // Media keys belong to AirPlay only while a source is connected, so local players get them
    // back afterwards.
    void UpdateHotKeys() => window.SetMediaKeys(np.Usable && mediaKeysItem.Checked);

    void OnHotKey(int vk)
    {
        switch (vk)
        {
            case HiddenWindow.VK_MEDIA_NEXT_TRACK: Send("next"); ShowOsd(OsdAction.Next); break;
            case HiddenWindow.VK_MEDIA_PREV_TRACK: Send("prev"); ShowOsd(OsdAction.Previous); break;
            case HiddenWindow.VK_MEDIA_PLAY_PAUSE: Send("toggle"); ShowOsd(OsdAction.Toggle); break;
        }
    }

    // ---- on-screen display for media keys ------------------------------------------------------

    enum OsdAction { Next, Previous, Toggle }
    readonly Osd osd = new();
    OsdAction osdAction;
    DateTime osdUntil;      // updates from the source refresh the OSD until then
    string osdTrack;        // the track shown when the key was pressed

    void ShowOsd(OsdAction a)
    {
        osdAction = a;
        osdUntil = DateTime.UtcNow.AddSeconds(5);
        osdTrack = np.Title;
        if (a == OsdAction.Toggle)
            // predict the new state; the source's update confirms it a moment later
            osd.Present(np.Playing ? T("⏸  Paused", "⏸  已暂停") : T("▶  Playing", "▶  播放中"), np.Title, np.Artist, np.Artwork);
        else
            osd.Present(OsdCaption(), "…", "", null);
    }

    string OsdCaption() => osdAction switch
    {
        OsdAction.Next => T("⏭  Next", "⏭  下一首"),
        OsdAction.Previous => T("⏮  Previous", "⏮  上一首"),
        _ => np.Playing ? T("▶  Playing", "▶  播放中") : T("⏸  Paused", "⏸  已暂停"),
    };

    // called on every now-playing change
    void UpdateOsd()
    {
        if (DateTime.UtcNow > osdUntil || !np.Session || np.Title.Length == 0) return;
        // for next/previous, wait for the new track; "previous" may just restart the same song,
        // so after a second that one is shown too
        if (osdAction == OsdAction.Next && np.Title == osdTrack) return;
        if (osdAction == OsdAction.Previous && np.Title == osdTrack && DateTime.UtcNow < osdUntil.AddSeconds(-4)) return;
        osd.Present(OsdCaption(), np.Title, np.Artist, np.Artwork);
    }

    void OnThemeChanged(object s, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        MakeIcons();
        icon.Icon = np.Session ? activeIcon : idleIcon;
    }

    // The AirPlay audio glyph (arcs over a triangle), drawn for the taskbar's light or dark theme.
    void MakeIcons()
    {
        bool light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is int i && i == 1;
        var fg = light ? Color.FromArgb(32, 32, 32) : Color.White;
        var old = (activeIcon, idleIcon);
        activeIcon = Glyph(fg);
        idleIcon = Glyph(Color.FromArgb(110, fg));
        old.activeIcon?.Dispose(); old.idleIcon?.Dispose();
    }

    static Icon Glyph(Color c)
    {
        int s = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp)) DrawGlyph(g, 0, 0, s, c);
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        HiddenWindow.DestroyIcon(h);
        return icon;
    }

    // AirPlay audio glyph in a size x size box at (x, y)
    public static void DrawGlyph(Graphics g, float x, float y, float size, Color c)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = size / 16f;
        using var pen = new Pen(c, 1.4f * w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(pen, x + 1.5f * w, y + 1f * w, 13f * w, 13f * w, 140, 260);
        g.DrawArc(pen, x + 4.5f * w, y + 4f * w, 7f * w, 7f * w, 140, 260);
        using var brush = new SolidBrush(c);
        g.FillPolygon(brush, new[] { new PointF(x + 8 * w, y + 9.5f * w), new PointF(x + 3.5f * w, y + 15.5f * w), new PointF(x + 12.5f * w, y + 15.5f * w) });
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.UserPreferenceChanged -= OnThemeChanged;
        window.SetMediaKeys(false);
        smtc?.Dispose();
        icon.Visible = false;
        icon.Dispose();
        window.DestroyHandle();
        base.ExitThreadCore();
    }
}

// Menu header: artwork on the left, title (bold) and "artist — album" (dimmed) on the right.
sealed class NowPlayingItem : ToolStripItem
{
    string line1 = "", line2 = "";
    Image art;

    public NowPlayingItem() { Enabled = true; }

    public void Set(string l1, string l2, Image a)
    {
        line1 = l1; line2 = l2; art = a;
        if (Owner is ToolStripDropDownMenu m)
        {
            // the drop-down sizes itself from its menu items only; reserve room for this one
            int need = GetPreferredSize(Size.Empty).Width + m.Padding.Horizontal + 2;
            m.MinimumSize = new Size(need, 0);
            m.PerformLayout();
        }
        Invalidate();
    }

    int Scale(int px) => (Owner?.DeviceDpi ?? 96) * px / 96;
    Font Bold => new(Font, FontStyle.Bold);

    // long titles wrap onto more lines instead of widening the menu
    const TextFormatFlags Flags = TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
    int MaxTextWidth => Scale(300);

    (Size s1, Size s2) Measure()
    {
        using var bold = Bold;
        var limit = new Size(MaxTextWidth, int.MaxValue);
        var s1 = TextRenderer.MeasureText(line1, bold, limit, Flags);
        var s2 = line2.Length > 0 ? TextRenderer.MeasureText(line2, Font, limit, Flags) : Size.Empty;
        return (s1, s2);
    }

    public override Size GetPreferredSize(Size constrainingSize)
    {
        int pad = Scale(10), artSize = art != null ? Scale(48) : 0;
        var (s1, s2) = Measure();
        int textW = Math.Min(Math.Max(s1.Width, s2.Width), MaxTextWidth);
        int textH = s1.Height + (line2.Length > 0 ? Scale(2) : 0) + s2.Height;
        return new Size(pad + (art != null ? artSize + pad : 0) + textW + pad, Math.Max(artSize, textH) + 2 * Scale(8));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        int pad = Scale(10), artSize = Scale(48), x = pad;
        var g = e.Graphics;
        if (art != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(art, new Rectangle(x, (Height - artSize) / 2, artSize, artSize));
            x += artSize + pad;
        }
        using var bold = Bold;
        var (s1, s2) = Measure();
        int gap = line2.Length > 0 ? Scale(2) : 0;
        int y = (Height - s1.Height - gap - s2.Height) / 2, w = Math.Max(s1.Width, s2.Width);
        TextRenderer.DrawText(g, line1, bold, new Rectangle(x, y, w, s1.Height), SystemColors.MenuText, Flags);
        if (line2.Length > 0)
            TextRenderer.DrawText(g, line2, Font, new Rectangle(x, y + s1.Height + gap, w, s2.Height), SystemColors.GrayText, Flags);
    }
}

// Hidden top-level window: receives WM_HOTKEY, lets the listener thread post work to the UI
// thread, and owns the Windows media controls (GetForWindow does not accept message-only windows).
sealed class HiddenWindow : NativeWindow
{
    public const int VK_MEDIA_NEXT_TRACK = 0xB0, VK_MEDIA_PREV_TRACK = 0xB1, VK_MEDIA_PLAY_PAUSE = 0xB3;
    const int WM_HOTKEY = 0x0312, WM_APP_INVOKE = 0x8001;
    static readonly int[] Keys = { VK_MEDIA_NEXT_TRACK, VK_MEDIA_PREV_TRACK, VK_MEDIA_PLAY_PAUSE };

    readonly Action<int> onHotKey;
    readonly System.Collections.Concurrent.ConcurrentQueue<Action> queue = new();
    bool registered;

    public HiddenWindow(Action<int> onHotKey)
    {
        this.onHotKey = onHotKey;
        // a plain top-level window, never shown (SMTC refuses tool windows: ERROR_NOT_SUPPORTED)
        CreateHandle(new CreateParams { Caption = "AirPlay" });
    }

    public void BeginInvoke(Action a)
    {
        queue.Enqueue(a);
        PostMessage(Handle, WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);
    }

    public void SetMediaKeys(bool on)
    {
        if (on == registered || Handle == IntPtr.Zero) return;
        registered = on;
        foreach (var vk in Keys)
        {
            // another program may own a key already; then that key simply stays with it
            if (on) RegisterHotKey(Handle, vk, 0x4000 /* MOD_NOREPEAT */, (uint)vk);
            else UnregisterHotKey(Handle, vk);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY) onHotKey((int)m.WParam);
        else if (m.Msg == WM_APP_INVOKE)
        {
            while (queue.TryDequeue(out var a)) a();
        }
        else base.WndProc(ref m);
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}

// Gives this process an AppUserModelID that Windows can resolve to a name and icon, so its media
// controls show "AirPlay" rather than "Unknown app". For an unpackaged app that takes a Start menu
// shortcut carrying the same ID (System.AppUserModel.ID); the tray keeps one in the user's Start
// menu, pointing at this exe.
static class AppIdentity
{
    public const string Id = "airplay2-windows.tray";

    public static void Register()
    {
        try { EnsureShortcut(); }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException) { }
        SetCurrentProcessExplicitAppUserModelID(Id);
    }

    static void EnsureShortcut()
    {
        string exe = Environment.ProcessPath;
        string lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AirPlay.lnk");
        var link = (IShellLinkW)new ShellLink();
        if (File.Exists(lnk))
        {
            // already ours and pointing here: nothing to do
            ((IPersistFile)link).Load(lnk, 0);
            var target = new char[1024];
            link.GetPath(target, target.Length, IntPtr.Zero, 0);
            if (string.Equals(new string(target).TrimEnd((char)0), exe, StringComparison.OrdinalIgnoreCase)) return;
            link = (IShellLinkW)new ShellLink();
        }
        link.SetPath(exe);
        link.SetWorkingDirectory(Path.GetDirectoryName(exe));
        link.SetDescription("AirPlay receiver: now playing and remote control");
        var key = new PropertyKey { Fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Pid = 5 }; // System.AppUserModel.ID
        var value = new PropVariant { Vt = 31 /* VT_LPWSTR */, Ptr = Marshal.StringToCoTaskMemUni(Id) };
        try
        {
            var store = (IPropertyStore)link;
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally { Marshal.FreeCoTaskMem(value.Ptr); }
        ((IPersistFile)link).Save(lnk, true);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string id);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    interface IPersistFile
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid Fmtid; public uint Pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public IntPtr Ptr;
    }
}

static class Settings
{
    const string Key = @"HKEY_CURRENT_USER\Software\airplay2-windows";
    public static bool MediaKeys
    {
        get => Registry.GetValue(Key, "TrayMediaKeys", 1) is not int v || v != 0;
        set => Registry.SetValue(Key, "TrayMediaKeys", value ? 1 : 0, RegistryValueKind.DWord);
    }
}
