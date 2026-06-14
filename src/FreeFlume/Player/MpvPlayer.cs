// FreeFlume — mpv video surface embedded in a WinUI 3 SwapChainPanel.
// Author: velkadyne
//
// Stage-2 spike target: prove libmpv renders video INTO the XAML tree (so transport
// controls/overlays can compose on top). Uses mpv's software render API to write each
// frame into a D3D11 dynamic texture, which is copied to a composition swapchain bound
// to this panel. The GL/ANGLE zero-copy path is a later perf optimization; the surface
// contract (SwapChainPanel) stays the same.

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;
using static FreeFlume.Player.LibMpv;

namespace FreeFlume.Player;

[ComImport]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISwapChainPanelNative
{
    [PreserveSig] int SetSwapChain(IntPtr swapChain);
}

public sealed partial class MpvPlayer : SwapChainPanel, IDisposable
{
    private IntPtr _mpv;
    private IntPtr _renderCtx;

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGISwapChain1 _swapChain = null!;
    private ID3D11Texture2D? _stage;
    private int _w, _h;

    // Reused native buffers for the SW render params (kept alive for the control's life).
    private IntPtr _pApiSw, _pFmt, _pSize, _pStride, _pBlockZero;
    private bool _graphicsReady;
    private bool _disposed;
    private string? _pendingUrl;
    private double _pendingStart;
    private double _resumeTo;   // seek here once the file is loaded
    private Thread? _eventThread;

    // Hardware-decode stall watchdog: some GPU decoders hang on a mid-stream change (video freezes
    // while audio plays on). We detect "audio advancing but no fresh video frame" and drop to
    // software decoding for that file.
    private long _lastFrameTick;
    private bool _hwFellBack;

    // ---- observable playback state (updated on the UI thread) ----
    public double Position { get; private set; }
    public double Duration { get; private set; }
    public bool Paused { get; private set; }
    public bool Muted { get; private set; }
    public double Volume { get; private set; } = 100;

    public event Action<double>? PositionChanged;
    public event Action<double>? DurationChanged;
    public event Action<bool>? PausedChanged;
    public event Action<bool>? MutedChanged;
    public event Action? EndReached;
    public event Action<System.Collections.Generic.List<FreeFlume.Models.Chapter>>? ChaptersChanged;
    public event Action<System.Collections.Generic.List<FreeFlume.Models.MediaTrack>>? TracksChanged;
    public event Action<string>? TitleChanged;   // mpv media-title once the file loads

    public MpvPlayer()
    {
        _pApiSw = Marshal.StringToHGlobalAnsi("sw");
        _pFmt = Marshal.StringToHGlobalAnsi("bgr0");          // matches DXGI B8G8R8A8
        _pSize = Marshal.AllocHGlobal(sizeof(int) * 2);
        _pStride = Marshal.AllocHGlobal(IntPtr.Size);          // size_t
        _pBlockZero = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(_pBlockZero, 0);                    // don't block UI thread on A/V timing

        Loaded += OnLoaded;
        // Note: we deliberately do NOT dispose on Unloaded — the player is reparented between
        // the full page and the mini overlay, which fires Unloaded/Loaded. Dispose is explicit.
        SizeChanged += (_, __) => { if (_graphicsReady) ResizeToPanel(); };
        CompositionScaleChanged += (_, __) => { if (_graphicsReady) ResizeToPanel(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_graphicsReady) return;
        try
        {
            InitMpv();
            SpikeLog.Write("mpv + SW render context created.");
            InitGraphics();
            SpikeLog.Write($"D3D11 swapchain bound to panel ({_w}x{_h}). Render loop starting.");
            _graphicsReady = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;

            if (_pendingUrl is not null) { var u = _pendingUrl; var s = _pendingStart; _pendingUrl = null; _pendingStart = 0; Play(u, s); }
        }
        catch (Exception ex)
        {
            SpikeLog.Write("INIT FAILED: " + ex);
        }
    }

    /// <summary>
    /// Reparent this panel into <paramref name="newParent"/>. A SwapChainPanel with a live
    /// composition swapchain throws E_UNEXPECTED on Children.Add, so we unbind the swapchain,
    /// move the panel, then rebind it to the freshly-homed composition visual.
    /// </summary>
    public void MoveTo(Panel newParent)
    {
        if (ReferenceEquals(Parent, newParent))
        {
            TryAddTo(newParent);
            return;
        }

        DetachSwapChain();
        if (Parent is Panel old) { try { old.Children.Remove(this); } catch { } }
        TryAddTo(newParent);
        ReattachSwapChain();
    }

    /// <summary>Add this panel to <paramref name="parent"/>, surviving the rare WinRT reparent hiccup
    /// (COMException 0x800F1000 "No installed components were detected") that otherwise crashes the app
    /// — e.g. navigating between the player and the mini-player while a video is loaded. We fully unbind
    /// the swapchain, detach from any current parent, and retry once; failing that we give up quietly
    /// (the surface rebinds on the next move) rather than tearing down the process.</summary>
    private void TryAddTo(Panel parent)
    {
        try
        {
            if (!parent.Children.Contains(this)) parent.Children.Add(this);
            return;
        }
        catch (Exception ex) { SpikeLog.Write("MoveTo add failed, retrying: " + ex.Message); }

        try { this.As<ISwapChainPanelNative>().SetSwapChain(IntPtr.Zero); } catch { }
        try { if (Parent is Panel p) p.Children.Remove(this); } catch { }
        try { if (!parent.Children.Contains(this)) parent.Children.Add(this); }
        catch (Exception ex) { SpikeLog.Write("MoveTo add retry failed: " + ex.Message); }
    }

    /// <summary>Unbind the swapchain so the panel can be safely reparented.</summary>
    private void DetachSwapChain()
    {
        if (!_graphicsReady) return;
        try { this.As<ISwapChainPanelNative>().SetSwapChain(IntPtr.Zero); } catch { }
    }

    /// <summary>Rebind the swapchain to this panel's (possibly new) composition visual.</summary>
    private void ReattachSwapChain()
    {
        if (!_graphicsReady || _swapChain is null) return;
        try
        {
            this.As<ISwapChainPanelNative>().SetSwapChain(_swapChain.NativePointer);
            ResizeToPanel();
        }
        catch { }
    }

    /// <summary>Stop playback and unload the file (keeps the mpv instance alive for reuse).</summary>
    public void Stop() { if (_mpv != IntPtr.Zero) mpv_command_string(_mpv, "stop"); }

    private bool _cursorVisible = true;

    /// <summary>Show or hide the mouse cursor over the video surface — used to hide it together with the
    /// controls in fullscreen. WinUI has no "no-cursor" shape, so hiding uses the known trick of
    /// assigning an InputCursor to this element's ProtectedCursor and disposing it, leaving nothing to
    /// draw. (ProtectedCursor is settable directly here because MpvPlayer is our own UIElement subclass.)</summary>
    public void SetCursorVisible(bool visible)
    {
        if (visible == _cursorVisible) return;
        _cursorVisible = visible;
        try
        {
            var c = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
            ProtectedCursor = c;
            if (!visible) c.Dispose();   // disposing the assigned cursor makes it invisible
        }
        catch { }
    }

    private void InitMpv()
    {
        // mpv must find our bundled yt-dlp.exe + deno.exe (sit next to the exe). Done once at startup,
        // but ensure it here too in case a player is created before App configured the environment.
        FreeFlume.Services.ToolEnv.Configure();

        _mpv = mpv_create();
        if (_mpv == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed");

        var settings = FreeFlume.Services.Settings.Shared;
        Volume = settings.Volume;

        mpv_set_option_string(_mpv, "vo", "libmpv");           // required for the render API
        mpv_set_option_string(_mpv, "hwdec", settings.HwDecodeMode);
        mpv_set_option_string(_mpv, "keepaspect", "yes");      // preserve video aspect (letterbox/pillarbox)
        mpv_set_option_string(_mpv, "osd-level", "0");         // no mpv OSD; we draw our own controls
        mpv_set_option_string(_mpv, "osd-bar", "no");          // no on-seek OSD bar
        mpv_set_option_string(_mpv, "ytdl", "yes");
        mpv_set_option_string(_mpv, "ytdl-format", settings.QualityFormat);
        mpv_set_option_string(_mpv, "script-opts", "ytdl_hook-ytdl_path=" + FreeFlume.Services.YtDlp.ExePath);
        mpv_set_option_string(_mpv, "keep-open", "yes");
        mpv_set_option_string(_mpv, "sub-auto", "no");         // captions show only when picked from the CC menu
        // Smooth high-bitrate (4K) streaming: a larger demuxer cache reduces stutter/rebuffering.
        mpv_set_option_string(_mpv, "cache", "yes");
        mpv_set_option_string(_mpv, "demuxer-max-bytes", "256MiB");
        mpv_set_option_string(_mpv, "demuxer-readahead-secs", "20");
        mpv_set_option_string(_mpv, "terminal", "yes");
        mpv_set_option_string(_mpv, "msg-level", "all=status");

        if (mpv_initialize(_mpv) < 0) throw new InvalidOperationException("mpv_initialize failed");

        mpv_set_property_string(_mpv, "volume", ((int)Volume).ToString(CultureInfo.InvariantCulture));
        ApplySubtitleStyle();
        mpv_observe_property(_mpv, 1, "time-pos", FORMAT_DOUBLE);
        mpv_observe_property(_mpv, 2, "duration", FORMAT_DOUBLE);
        mpv_observe_property(_mpv, 3, "pause", FORMAT_FLAG);
        mpv_observe_property(_mpv, 4, "mute", FORMAT_FLAG);
        mpv_observe_property(_mpv, 5, "volume", FORMAT_DOUBLE);
        mpv_observe_property(_mpv, 6, "eof-reached", FORMAT_FLAG);   // end-of-file -> autoplay next

        var cp = new[]
        {
            new RenderParam(PARAM_API_TYPE, _pApiSw),
            new RenderParam(PARAM_INVALID, IntPtr.Zero),
        };
        int rc = mpv_render_context_create(out _renderCtx, _mpv, cp);
        if (rc < 0) throw new InvalidOperationException($"mpv_render_context_create (sw) failed rc={rc}");

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _eventThread.Start();
    }

    private void EventLoop()
    {
        while (!_disposed)
        {
            IntPtr ev = mpv_wait_event(_mpv, 0.1);
            if (ev == IntPtr.Zero) continue;
            int id = Marshal.ReadInt32(ev);            // mpv_event.event_id @ 0
            if (id == 0) continue;
            if (id == EVENT_SHUTDOWN) break;
            if (id == EVENT_FILE_LOADED)
            {
                if (_resumeTo > 0)
                {
                    double r = _resumeTo; _resumeTo = 0;
                    DispatcherQueue?.TryEnqueue(() => SeekAbsolute(r));
                }
                var chapters = ReadChapters();
                DispatcherQueue?.TryEnqueue(() => ChaptersChanged?.Invoke(chapters));
                var tracks = ReadTracks();
                DispatcherQueue?.TryEnqueue(() => TracksChanged?.Invoke(tracks));
                string title = GetStrProp("media-title");
                DispatcherQueue?.TryEnqueue(() => TitleChanged?.Invoke(title));
                _hwFellBack = false;                          // new file: try hardware decode again
                _lastFrameTick = Environment.TickCount64;
                continue;
            }
            if (id != EVENT_PROPERTY_CHANGE) continue;

            IntPtr data = Marshal.ReadIntPtr(ev, 16);  // mpv_event.data @ 16
            if (data == IntPtr.Zero) continue;
            IntPtr namePtr = Marshal.ReadIntPtr(data, 0);
            int format = Marshal.ReadInt32(data, 8);
            IntPtr valPtr = Marshal.ReadIntPtr(data, 16);
            if (namePtr == IntPtr.Zero || valPtr == IntPtr.Zero) continue;

            string name = Marshal.PtrToStringAnsi(namePtr) ?? "";
            if (format == FORMAT_DOUBLE)
            {
                double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(valPtr));
                DispatcherQueue?.TryEnqueue(() => ApplyDouble(name, v));
            }
            else if (format == FORMAT_FLAG)
            {
                bool b = Marshal.ReadInt32(valPtr) != 0;
                DispatcherQueue?.TryEnqueue(() => ApplyFlag(name, b));
            }
        }
    }

    private void ApplyDouble(string name, double v)
    {
        switch (name)
        {
            case "time-pos": Position = v; PositionChanged?.Invoke(v); CheckVideoStall(v); break;
            case "duration": Duration = v; DurationChanged?.Invoke(v); break;
            case "volume": Volume = v; break;
        }
    }

    // Called as audio advances (time-pos). If no fresh video frame has arrived for a few seconds
    // while playing, the hardware decoder has hung — drop to software and reload from here.
    private void CheckVideoStall(double pos)
    {
        if (_hwFellBack || Paused || _lastFrameTick == 0) return;
        if (FreeFlume.Services.Settings.Shared.HwDecodeMode == "no") return;   // already software
        if (Environment.TickCount64 - _lastFrameTick < 4000) return;           // video still flowing

        _hwFellBack = true;
        mpv_set_property_string(_mpv, "hwdec", "no");
        SeekAbsolute(pos);                       // re-init the decoder (now software) from here
        _lastFrameTick = Environment.TickCount64;
        SpikeLog.Write($"HW decode stalled at {pos:0.0}s — fell back to software for this video.");
    }

    private void ApplyFlag(string name, bool b)
    {
        switch (name)
        {
            // Resuming playback: reset the frame clock so the watchdog doesn't fire on the pause gap.
            case "pause": Paused = b; PausedChanged?.Invoke(b); if (!b) _lastFrameTick = Environment.TickCount64; break;
            case "mute": Muted = b; MutedChanged?.Invoke(b); break;
            case "eof-reached": if (b) EndReached?.Invoke(); break;
        }
    }

    // ---- transport controls ----
    public void TogglePause() => SetPaused(!Paused);
    public void SetPaused(bool p) { if (_mpv != IntPtr.Zero) mpv_set_property_string(_mpv, "pause", p ? "yes" : "no"); }
    public void SeekAbsolute(double seconds) { if (_mpv != IntPtr.Zero) mpv_command_string(_mpv, $"seek {seconds.ToString(CultureInfo.InvariantCulture)} absolute"); }
    public void SeekRelative(double delta) { if (_mpv != IntPtr.Zero) mpv_command_string(_mpv, $"seek {delta.ToString(CultureInfo.InvariantCulture)} relative"); }
    public void SetVolume(double v) { if (_mpv != IntPtr.Zero) mpv_set_property_string(_mpv, "volume", ((int)v).ToString(CultureInfo.InvariantCulture)); }
    public void ToggleMute() => SetMuted(!Muted);
    public void SetMuted(bool m) { if (_mpv != IntPtr.Zero) mpv_set_property_string(_mpv, "mute", m ? "yes" : "no"); }
    public void CycleSubtitles() { if (_mpv != IntPtr.Zero) mpv_command_string(_mpv, "cycle sub"); }

    /// <summary>Push the saved subtitle styling onto mpv (applies live to the current sub).</summary>
    public void ApplySubtitleStyle()
    {
        if (_mpv == IntPtr.Zero) return;
        var s = FreeFlume.Services.Settings.Shared;
        void Set(string k, string v) => mpv_set_property_string(_mpv, k, v);
        string n(int i) => i.ToString(CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(s.SubtitleFont)) Set("sub-font", s.SubtitleFont);
        Set("sub-font-size", n(s.SubtitleFontSize));
        Set("sub-color", s.SubtitleColor);
        Set("sub-bold", s.SubtitleBold ? "yes" : "no");
        // Outline + shadow + back-color (old/new mpv option names; unknown ones error out harmlessly).
        Set("sub-outline-size", n(s.SubtitleOutline)); Set("sub-border-size", n(s.SubtitleOutline));
        Set("sub-shadow-offset", n(s.SubtitleShadowOffset));
        Set("sub-shadow-color", s.SubtitleShadowColor);
        Set("sub-back-color", s.SubtitleBackground ? "#80000000" : "#00000000");
    }

    /// <summary>Tell yt-dlp (via mpv) which caption tracks to fetch. Applies on the next load.
    /// We fetch ALL human-made subtitles (the user picks the language from the in-player CC menu);
    /// auto-generated captions are added only when the setting opts in.</summary>
    public void ApplySubtitleFetch()
    {
        if (_mpv == IntPtr.Zero) return;
        var opts = new System.Collections.Generic.List<string> { "sub-langs=all", "write-subs=" };
        if (FreeFlume.Services.Settings.Shared.SubtitleIncludeAuto) opts.Add("write-auto-subs=");
        mpv_set_property_string(_mpv, "ytdl-raw-options", string.Join(",", opts));
    }
    public void SetLoop(bool on) { if (_mpv != IntPtr.Zero) mpv_set_property_string(_mpv, "loop-file", on ? "inf" : "no"); }
    public void SetSpeed(double s) { if (_mpv != IntPtr.Zero) mpv_set_property_string(_mpv, "speed", s.ToString(CultureInfo.InvariantCulture)); }
    public void SetVideoEnabled(bool on) { if (_mpv != IntPtr.Zero) mpv_set_property_string(_mpv, "vid", on ? "auto" : "no"); }
    public void SetYtdlFormat(string fmt) { if (_mpv != IntPtr.Zero) mpv_set_property_string(_mpv, "ytdl-format", fmt); }
    public void SetSubTrack(string idOrNo) { if (_mpv != IntPtr.Zero) mpv_set_property_string(_mpv, "sid", idOrNo); }

    /// <summary>Save a native-resolution screenshot of the current frame, encoded as PNG or JPEG by
    /// the file extension. We render the current mpv frame into an offscreen CPU buffer sized at the
    /// video's dwidth×dheight via the SW render API — mpv's own <c>screenshot-to-file</c> is
    /// unreliable with the libmpv VO (this render path), so we never use it. Subtitles burned in are
    /// whatever mpv is compositing. Must run on the UI thread (shares the render context).</summary>
    public async Task<bool> ScreenshotNativeAsync(string path, int jpegQuality)
    {
        if (_mpv == IntPtr.Zero || _renderCtx == IntPtr.Zero) return false;
        int sw = (int)GetDoubleProp("dwidth"), sh = (int)GetDoubleProp("dheight");
        if (sw <= 0 || sh <= 0) { sw = _w; sh = _h; }   // fall back to the on-screen buffer size
        if (sw <= 0 || sh <= 0) return false;

        var pixels = new byte[(long)sw * sh * 4];
        IntPtr pSize = Marshal.AllocHGlobal(sizeof(int) * 2);
        IntPtr pStride = Marshal.AllocHGlobal(IntPtr.Size);
        var gch = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            Marshal.WriteInt32(pSize, 0, sw);
            Marshal.WriteInt32(pSize, 4, sh);
            Marshal.WriteIntPtr(pStride, (IntPtr)(sw * 4));
            var rp = new[]
            {
                new RenderParam(PARAM_SW_SIZE, pSize),
                new RenderParam(PARAM_SW_FORMAT, _pFmt),       // "bgr0" → BGRA8
                new RenderParam(PARAM_SW_STRIDE, pStride),
                new RenderParam(PARAM_SW_POINTER, gch.AddrOfPinnedObject()),
                new RenderParam(PARAM_BLOCK_FOR_TARGET_TIME, _pBlockZero),
                new RenderParam(PARAM_INVALID, IntPtr.Zero),
            };
            if (mpv_render_context_render(_renderCtx, rp) < 0) return false;
        }
        finally { gch.Free(); Marshal.FreeHGlobal(pSize); Marshal.FreeHGlobal(pStride); }

        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            bool jpeg = ext is ".jpg" or ".jpeg";
            using var ms = new InMemoryRandomAccessStream();
            BitmapEncoder encoder;
            if (jpeg)
            {
                var opts = new Windows.Graphics.Imaging.BitmapPropertySet
                {
                    { "ImageQuality", new Windows.Graphics.Imaging.BitmapTypedValue(
                        Math.Clamp(jpegQuality, 1, 100) / 100.0, Windows.Foundation.PropertyType.Single) }
                };
                encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms, opts);
            }
            else
            {
                encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
            }
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)sw, (uint)sh, 96, 96, pixels);
            await encoder.FlushAsync();
            var bytes = new byte[ms.Size];
            using (var dr = new DataReader(ms.GetInputStreamAt(0)))
            {
                await dr.LoadAsync((uint)ms.Size);
                dr.ReadBytes(bytes);
            }
            File.WriteAllBytes(path, bytes);
            return true;
        }
        catch (Exception ex) { SpikeLog.Write("SCREENSHOT FAILED: " + ex); return false; }
    }

    private System.Collections.Generic.List<FreeFlume.Models.Chapter> ReadChapters()
    {
        var list = new System.Collections.Generic.List<FreeFlume.Models.Chapter>();
        if (_mpv == IntPtr.Zero) return list;
        int count = GetIntProp("chapter-list/count");
        for (int i = 0; i < count; i++)
            list.Add(new FreeFlume.Models.Chapter(GetDoubleProp($"chapter-list/{i}/time"), GetStrProp($"chapter-list/{i}/title")));
        return list;
    }

    private string GetStrProp(string name)
    {
        IntPtr p = mpv_get_property_string(_mpv, name);
        if (p == IntPtr.Zero) return "";
        string s = Marshal.PtrToStringUTF8(p) ?? "";
        mpv_free(p);
        return s;
    }

    private int GetIntProp(string name) => int.TryParse(GetStrProp(name), out var v) ? v : 0;
    private double GetDoubleProp(string name) =>
        double.TryParse(GetStrProp(name), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Display aspect ratio of the current video (width/height), or 0 if unknown.</summary>
    public double VideoAspect
    {
        get
        {
            if (_mpv == IntPtr.Zero) return 0;
            double w = GetDoubleProp("dwidth"), h = GetDoubleProp("dheight");
            return (w > 0 && h > 0) ? w / h : 0;
        }
    }

    private System.Collections.Generic.List<FreeFlume.Models.MediaTrack> ReadTracks()
    {
        var list = new System.Collections.Generic.List<FreeFlume.Models.MediaTrack>();
        if (_mpv == IntPtr.Zero) return list;
        int count = GetIntProp("track-list/count");
        for (int i = 0; i < count; i++)
        {
            string type = GetStrProp($"track-list/{i}/type");
            if (type != "audio" && type != "sub") continue;
            list.Add(new FreeFlume.Models.MediaTrack(
                GetIntProp($"track-list/{i}/id"), type,
                GetStrProp($"track-list/{i}/lang"), GetStrProp($"track-list/{i}/title"),
                GetStrProp($"track-list/{i}/selected") == "yes"));
        }
        return list;
    }

    private int Command(params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++) ptrs[i] = Utf8(args[i]);
            ptrs[args.Length] = IntPtr.Zero;
            return mpv_command(_mpv, ptrs);
        }
        finally { foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p); }
    }

    private static IntPtr Utf8(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        var p = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return p;
    }
    public void FrameStep() { if (_mpv != IntPtr.Zero) mpv_command_string(_mpv, "frame-step"); }

    /// <summary>Step one frame back. mpv's own frame-back-step is unreliable on network streams, so we
    /// pause and seek back exactly 1/fps (fps from container-fps, then estimated-vf-fps, then 30).</summary>
    public void FrameBackStep()
    {
        if (_mpv == IntPtr.Zero) return;
        SetPaused(true);
        double fps = GetDoubleProp("container-fps");
        if (fps <= 0) fps = GetDoubleProp("estimated-vf-fps");
        if (fps <= 0) fps = 30;
        mpv_command_string(_mpv, $"seek {(-1.0 / fps).ToString(CultureInfo.InvariantCulture)} exact");
    }

    /// <summary>libmpv version string (e.g. "mpv 0.38.0"), via a throwaway handle.</summary>
    public static string MpvVersion()
    {
        IntPtr h = mpv_create();
        if (h == IntPtr.Zero) return "";
        try
        {
            IntPtr p = mpv_get_property_string(h, "mpv-version");
            if (p == IntPtr.Zero) return "";
            string s = Marshal.PtrToStringUTF8(p) ?? "";
            mpv_free(p);
            return s;
        }
        finally { mpv_terminate_destroy(h); }
    }

    private void InitGraphics()
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        var res = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out var dev);
        if (res.Failure || dev is null)
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out dev).CheckError();
        _device = dev!;
        _context = _device.ImmediateContext;

        (_w, _h) = PanelPixelSize();

        var desc = new SwapChainDescription1
        {
            Width = (uint)_w,
            Height = (uint)_h,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        _swapChain = factory.CreateSwapChainForComposition(_device, desc, null);

        // Bind the swapchain to this XAML panel.
        var native = this.As<ISwapChainPanelNative>();
        Marshal.ThrowExceptionForHR(native.SetSwapChain(_swapChain.NativePointer));

        CreateStage();
        ApplyMatrixTransform();
    }

    private (int w, int h) PanelPixelSize()
    {
        // Sharpness ON: buffer at PHYSICAL size (logical * composition scale) so the video is
        // rendered at native display resolution, with an inverse-scale swapchain transform to
        // map it back to the panel. OFF: LOGICAL size (system upscales — softer but lighter).
        bool sharp = FreeFlume.Services.Settings.Shared.HiDpiVideoSharpness;
        double sx = sharp ? CompositionScaleX : 1.0;
        double sy = sharp ? CompositionScaleY : 1.0;
        int w = Math.Max(1, (int)(ActualWidth * sx));
        int h = Math.Max(1, (int)(ActualHeight * sy));
        return (w, h);
    }

    private void ApplyMatrixTransform()
    {
        try
        {
            using var sc2 = _swapChain.QueryInterface<IDXGISwapChain2>();
            bool sharp = FreeFlume.Services.Settings.Shared.HiDpiVideoSharpness;
            float sx = sharp ? (float)CompositionScaleX : 1f; if (sx <= 0) sx = 1;
            float sy = sharp ? (float)CompositionScaleY : 1f; if (sy <= 0) sy = 1;
            sc2.MatrixTransform = System.Numerics.Matrix3x2.CreateScale(1f / sx, 1f / sy);
        }
        catch { /* IDXGISwapChain2 unavailable */ }
    }

    private void CreateStage()
    {
        _stage?.Dispose();
        _stage = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_w,
            Height = (uint)_h,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None,
        });
    }

    private void ResizeToPanel()
    {
        var (w, h) = PanelPixelSize();
        if (w == _w && h == _h) return;
        _w = w; _h = h;
        _swapChain.ResizeBuffers(2, (uint)_w, (uint)_h, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
        CreateStage();
        ApplyMatrixTransform();
    }

    private void OnRendering(object? sender, object e)
    {
        if (_disposed || _renderCtx == IntPtr.Zero) return;

        // Note when mpv has a fresh video frame, so the stall watchdog can tell "video flowing"
        // from "decoder hung."
        if ((mpv_render_context_update(_renderCtx) & RENDER_UPDATE_FRAME) != 0)
            _lastFrameTick = Environment.TickCount64;

        // Keep the buffer matched to the panel's current size; otherwise the SwapChainPanel
        // non-uniformly stretches the frame (distorting aspect, e.g. 4:3 shown as 16:9).
        var (pw, ph) = PanelPixelSize();
        if (pw != _w || ph != _h) ResizeToPanel();
        if (_stage is null) return;

        // Render mpv's current frame straight into the mapped staging texture.
        var map = _context.Map(_stage, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        Marshal.WriteInt32(_pSize, 0, _w);
        Marshal.WriteInt32(_pSize, 4, _h);
        Marshal.WriteIntPtr(_pStride, (IntPtr)map.RowPitch);

        var rp = new[]
        {
            new RenderParam(PARAM_SW_SIZE, _pSize),
            new RenderParam(PARAM_SW_FORMAT, _pFmt),
            new RenderParam(PARAM_SW_STRIDE, _pStride),
            new RenderParam(PARAM_SW_POINTER, map.DataPointer),
            new RenderParam(PARAM_BLOCK_FOR_TARGET_TIME, _pBlockZero),
            new RenderParam(PARAM_INVALID, IntPtr.Zero),
        };
        mpv_render_context_render(_renderCtx, rp);
        _context.Unmap(_stage, 0);

        using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _context.CopyResource(back, _stage);
        _swapChain.Present(1, PresentFlags.None);
    }

    /// <summary>Load and play a URL (anything yt-dlp/mpv understands).</summary>
    public void Play(string url) => Play(url, 0);

    /// <summary>Load a URL and, once it's loaded, seek to <paramref name="startSeconds"/> (resume).</summary>
    public void Play(string url, double startSeconds)
    {
        if (_mpv == IntPtr.Zero || !_graphicsReady) { _pendingUrl = url; _pendingStart = startSeconds; return; }
        _resumeTo = startSeconds;
        mpv_set_property_string(_mpv, "hwdec", FreeFlume.Services.Settings.Shared.HwDecodeMode);   // re-read so a changed setting applies
        ApplySubtitleFetch();   // which caption tracks to fetch for this load
        ApplySubtitleStyle();   // re-assert styling so every video reflects current settings
        mpv_command_string(_mpv, $"loadfile \"{url}\"");
    }

    /// <summary>
    /// Read the last rendered frame back from the GPU and save it as a PNG. Proves that
    /// real video pixels are reaching the texture (DirectComposition surfaces can't be
    /// captured by ordinary screenshots, so this is our ground-truth verification).
    /// Must run on the UI thread (shares the D3D immediate context with the render loop).
    /// </summary>
    public async Task GrabFrameToPngAsync(string path)
    {
        try
        {
            if (_stage is null) { SpikeLog.Write("GRAB: no stage texture."); return; }

            using var readback = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_w,
                Height = (uint)_h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
            _context.CopyResource(readback, _stage);

            var map = _context.Map(readback, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            var pixels = new byte[_w * _h * 4];
            long sum = 0;
            unsafe
            {
                byte* src = (byte*)map.DataPointer;
                for (int y = 0; y < _h; y++)
                {
                    Marshal.Copy((IntPtr)(src + (long)y * map.RowPitch), pixels, y * _w * 4, _w * 4);
                }
            }
            _context.Unmap(readback, 0);

            for (int i = 0; i < pixels.Length; i += 4) sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
            double avg = sum / (double)(_w * _h * 3);

            using var ms = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)_w, (uint)_h, 96, 96, pixels);
            await encoder.FlushAsync();
            var bytes = new byte[ms.Size];
            using (var dr = new DataReader(ms.GetInputStreamAt(0)))
            {
                await dr.LoadAsync((uint)ms.Size);
                dr.ReadBytes(bytes);
            }
            File.WriteAllBytes(path, bytes);
            SpikeLog.Write($"GRAB OK: {_w}x{_h} avgBrightness={avg:F1} -> {path}");
        }
        catch (Exception ex)
        {
            SpikeLog.Write("GRAB FAILED: " + ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        _eventThread?.Join(500);   // let the event loop exit before tearing down mpv

        if (_renderCtx != IntPtr.Zero) { mpv_render_context_free(_renderCtx); _renderCtx = IntPtr.Zero; }
        if (_mpv != IntPtr.Zero) { mpv_terminate_destroy(_mpv); _mpv = IntPtr.Zero; }

        _stage?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();

        foreach (var p in new[] { _pApiSw, _pFmt, _pSize, _pStride, _pBlockZero })
            if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
    }
}
