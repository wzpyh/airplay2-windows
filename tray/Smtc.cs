using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AirPlayTray;

// Windows' own media controls (the media card in Quick Settings, the volume flyout, the lock
// screen) for the AirPlay source: title, artist, artwork, play/pause/next/previous.
//
// Called through raw COM vtables instead of the C#/WinRT projection, which would add a 25 MB
// Microsoft.Windows.SDK.NET.dll for these few calls. Vtable slots follow windows.media.h /
// windows.storage.streams.h: 0-2 IUnknown, 3-5 IInspectable, then the interface's own methods in
// declaration order.
//
// If anything fails (old Windows, missing API) the tray works as before without it.
sealed unsafe class Smtc : IDisposable
{
    public enum Button { Play = 0, Pause = 1, Stop = 2, Next = 6, Previous = 7 }
    enum Status { Closed = 0, Changing = 1, Stopped = 2, Playing = 3, Paused = 4 }

    static readonly Guid IID_ISystemMediaTransportControlsInterop = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");
    static readonly Guid IID_ISystemMediaTransportControls = new("99fa3ff4-1742-42a6-902e-087d41f965ec");
    static readonly Guid IID_IMusicDisplayProperties2 = new("00368462-97d3-44b9-b00f-008afcefaf18");
    static readonly Guid IID_IRandomAccessStreamReferenceStatics = new("857309dc-3fbf-4e7d-986f-ef3b1a07a964");
    static readonly Guid IID_IRandomAccessStream = new("905a0fe1-bc53-11df-8c49-001e4fc686da");
    // TypedEventHandler<SystemMediaTransportControls, SystemMediaTransportControlsButtonPressedEventArgs>
    static readonly Guid IID_ButtonPressedHandler = new("0557e996-7b23-5bae-aa81-ea0d671143a4");
    static readonly Guid IID_IUnknown = new("00000000-0000-0000-c000-000000000046");
    static readonly Guid IID_IAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");

    IntPtr smtc, updater, music;
    long buttonToken;
    IntPtr handler;   // our COM delegate object
    GCHandle self;
    readonly Action<Button> onButton;

    Smtc(Action<Button> onButton) { this.onButton = onButton; }

    // hwnd: a top-level window of this process (it may be hidden)
    public static Smtc TryCreate(IntPtr hwnd, Action<Button> onButton)
    {
        var s = new Smtc(onButton);
        try
        {
            s.Init(hwnd);
            Log("init ok");
            return s;
        }
        catch (Exception e) when (e is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log("init at " + Step + ": " + e.Message + " " + e.StackTrace);
            s.Dispose();
            return null;
        }
    }

    internal static string Step = "";

    void Init(IntPtr hwnd)
    {
        Step = "factory";
        IntPtr interop = Factory("Windows.Media.SystemMediaTransportControls", IID_ISystemMediaTransportControlsInterop);
        try
        {
            var iid = IID_ISystemMediaTransportControls;
            // ISystemMediaTransportControlsInterop::GetForWindow(HWND, REFIID, void**)
            Step = "GetForWindow";
            IntPtr c;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Slot(interop, 6))(interop, hwnd, &iid, &c));
            smtc = c;
        }
        finally { Marshal.Release(interop); }

        // get_DisplayUpdater (8); put_Type(MediaPlaybackType.Music = 1) (7) must come before
        // get_MusicProperties (12), which is refused with ERROR_NOT_SUPPORTED while the type is Unknown
        Step = "DisplayUpdater";
        IntPtr u;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(smtc, 8))(smtc, &u));
        updater = u;
        Step = "put_Type";
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(updater, 7))(updater, 1));
        Step = "MusicProperties";
        IntPtr m;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(updater, 12))(updater, &m));
        music = m;

        Step = "enable buttons";
        foreach (int slot in new[] { 13, 17, 25, 27 }) // put_IsPlayEnabled, put_IsPauseEnabled, put_IsPreviousEnabled, put_IsNextEnabled
            Check(((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Slot(smtc, slot))(smtc, 1));

        Step = "add_ButtonPressed";
        self = GCHandle.Alloc(this);
        handler = Handler.Create(self);
        long token;
        // add_ButtonPressed (32)
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(smtc, 32))(smtc, handler, &token));
        buttonToken = token;
    }

    // Shows (or hides) the session and its now-playing data.
    public void Update(bool session, bool playing, string title, string artist, string album, byte[] artwork)
    {
        if (smtc == IntPtr.Zero) return;
        try
        {
            // put_IsEnabled (11)
            Check(((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Slot(smtc, 11))(smtc, session ? (byte)1 : (byte)0));
            if (!session) return;
            // put_PlaybackStatus (7)
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(smtc, 7))(smtc, (int)(playing ? Status.Playing : Status.Paused)));

            // IMusicDisplayProperties: put_Title (7), put_Artist (11); IMusicDisplayProperties2: put_AlbumTitle (7)
            PutString(music, 7, title);
            PutString(music, 11, artist);
            var iid2 = IID_IMusicDisplayProperties2;
            if (Marshal.QueryInterface(music, in iid2, out IntPtr music2) == 0)
            {
                try { PutString(music2, 7, album); } finally { Marshal.Release(music2); }
            }
            SetThumbnail(artwork);
            // Update (17)
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(updater, 17))(updater));
        }
        catch (COMException e) { Log("update: " + e.Message + " " + e.StackTrace); }
    }

    internal static void Log(string m)
    {
        if (Environment.GetEnvironmentVariable("AIRPLAY_TRAY_DEBUG") == null) return;
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "airplay2-tray.log"), DateTime.Now.ToString("T") + " " + m + Environment.NewLine); } catch (IOException) { }
    }

    void SetThumbnail(byte[] art)
    {
        IntPtr reference = IntPtr.Zero;
        if (art != null && art.Length > 0)
        {
            IntPtr istream = IntPtr.Zero, ras = IntPtr.Zero, statics = IntPtr.Zero;
            try
            {
                fixed (byte* p = art) istream = SHCreateMemStream(p, (uint)art.Length);
                if (istream == IntPtr.Zero) return;
                var iid = IID_IRandomAccessStream;
                Check(CreateRandomAccessStreamOverStream(istream, 0, &iid, &ras));
                statics = Factory("Windows.Storage.Streams.RandomAccessStreamReference", IID_IRandomAccessStreamReferenceStatics);
                // CreateFromStream (8)
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(statics, 8))(statics, ras, &reference));
            }
            finally
            {
                if (statics != IntPtr.Zero) Marshal.Release(statics);
                if (ras != IntPtr.Zero) Marshal.Release(ras);
                if (istream != IntPtr.Zero) Marshal.Release(istream);
            }
        }
        try
        {
            // put_Thumbnail (11); null clears it
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(updater, 11))(updater, reference));
        }
        finally { if (reference != IntPtr.Zero) Marshal.Release(reference); }
    }

    static void PutString(IntPtr obj, int slot, string value)
    {
        IntPtr h = IntPtr.Zero;
        value ??= "";
        fixed (char* p = value)
            Check(WindowsCreateString(p, (uint)value.Length, &h));
        try { Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(obj, slot))(obj, h)); }
        finally { WindowsDeleteString(h); }
    }

    public void Dispose()
    {
        if (smtc != IntPtr.Zero)
        {
            try
            {
                if (buttonToken != 0)
                    ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(smtc, 33))(smtc, buttonToken); // remove_ButtonPressed
                ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Slot(smtc, 11))(smtc, 0); // put_IsEnabled(false)
            }
            catch (COMException) { }
        }
        foreach (var p in new[] { music, updater, smtc })
            if (p != IntPtr.Zero) Marshal.Release(p);
        music = updater = smtc = IntPtr.Zero;
        if (handler != IntPtr.Zero) { Marshal.Release(handler); handler = IntPtr.Zero; }
        // self is freed by the handler's last Release
    }

    void Pressed(int button)
    {
        if (Enum.IsDefined(typeof(Button), button)) onButton((Button)button);
    }

    // ---- helpers --------------------------------------------------------------------------------

    static IntPtr Slot(IntPtr obj, int i) => (*(IntPtr**)obj)[i];

    static void Check(int hr)
    {
        if (hr < 0) throw new COMException($"SMTC call failed 0x{hr:X8}", hr);
    }

    static IntPtr Factory(string runtimeClass, Guid iid)
    {
        IntPtr h = IntPtr.Zero, f = IntPtr.Zero;
        fixed (char* p = runtimeClass)
            Check(WindowsCreateString(p, (uint)runtimeClass.Length, &h));
        try { Check(RoGetActivationFactory(h, &iid, &f)); }
        finally { WindowsDeleteString(h); }
        return f;
    }

    // A minimal COM object implementing the ButtonPressed delegate: IUnknown + Invoke.
    static class Handler
    {
        struct Obj
        {
            public IntPtr Vtbl;
            public int Refs;
            public IntPtr Target; // GCHandle to the Smtc
        }

        static readonly IntPtr Vtbl = MakeVtbl();

        static IntPtr MakeVtbl()
        {
            var v = (IntPtr*)NativeMemory.Alloc((nuint)(4 * sizeof(IntPtr)));
            v[0] = (IntPtr)(delegate* unmanaged[Stdcall]<Obj*, Guid*, IntPtr*, int>)&QueryInterface;
            v[1] = (IntPtr)(delegate* unmanaged[Stdcall]<Obj*, uint>)&AddRef;
            v[2] = (IntPtr)(delegate* unmanaged[Stdcall]<Obj*, uint>)&Release;
            v[3] = (IntPtr)(delegate* unmanaged[Stdcall]<Obj*, IntPtr, IntPtr, int>)&Invoke;
            return (IntPtr)v;
        }

        public static IntPtr Create(GCHandle target)
        {
            var o = (Obj*)NativeMemory.Alloc((nuint)sizeof(Obj));
            o->Vtbl = Vtbl;
            o->Refs = 1;
            o->Target = GCHandle.ToIntPtr(target);
            return (IntPtr)o;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        static int QueryInterface(Obj* self, Guid* iid, IntPtr* ppv)
        {
            if (*iid == IID_IUnknown || *iid == IID_IAgileObject || *iid == IID_ButtonPressedHandler)
            {
                Interlocked.Increment(ref self->Refs);
                *ppv = (IntPtr)self;
                return 0;
            }
            *ppv = IntPtr.Zero;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        static uint AddRef(Obj* self) => (uint)Interlocked.Increment(ref self->Refs);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        static uint Release(Obj* self)
        {
            int n = Interlocked.Decrement(ref self->Refs);
            if (n == 0)
            {
                GCHandle.FromIntPtr(self->Target).Free();
                NativeMemory.Free(self);
            }
            return (uint)n;
        }

        // Invoke(sender, ISystemMediaTransportControlsButtonPressedEventArgs*) -- get_Button is slot 6
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        static int Invoke(Obj* self, IntPtr sender, IntPtr args)
        {
            try
            {
                int button;
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(args, 6))(args, &button);
                if (hr >= 0 && GCHandle.FromIntPtr(self->Target).Target is Smtc s) s.Pressed(button);
            }
            catch (Exception) { } // never let an exception cross into native code
            return 0;
        }
    }

    [DllImport("combase.dll")] static extern int WindowsCreateString(char* s, uint len, IntPtr* h);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr h);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, Guid* iid, IntPtr* factory);
    [DllImport("shlwapi.dll")] static extern IntPtr SHCreateMemStream(byte* p, uint len);
    [DllImport("shcore.dll")] static extern int CreateRandomAccessStreamOverStream(IntPtr stream, int options, Guid* riid, IntPtr* ppv);
}
