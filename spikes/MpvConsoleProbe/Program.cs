// FreeFlume spike — prove libmpv + yt-dlp playback pipeline works on this machine.
// Author: velkadyne
//
// Loads libmpv-2.dll via P/Invoke, enables the ytdl hook (pointed at our bundled
// yt-dlp.exe), and plays a known-good YouTube test video in mpv's own window for a
// few seconds. Success = we observe FILE_LOADED + VIDEO_RECONFIG events and mpv's
// terminal output shows it decoding (AV: timestamps).

using System.Runtime.InteropServices;

static class Mpv
{
    const string Lib = "libmpv-2.dll";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int mpv_set_option_string(IntPtr ctx, string name, string data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int mpv_command_string(IntPtr ctx, string args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_error_string(int error);

    // mpv_event: first field is int event_id.
    public static int EventId(IntPtr ev) => Marshal.ReadInt32(ev);
}

class Program
{
    // mpv_event_id values we care about (from client.h)
    const int MPV_EVENT_SHUTDOWN = 1;
    const int MPV_EVENT_LOG_MESSAGE = 2;
    const int MPV_EVENT_START_FILE = 6;
    const int MPV_EVENT_FILE_LOADED = 8;
    const int MPV_EVENT_VIDEO_RECONFIG = 17;
    const int MPV_EVENT_PLAYBACK_RESTART = 21;

    static int Main(string[] argv)
    {
        // Make sure mpv's ytdl hook can find our bundled yt-dlp.exe.
        string baseDir = AppContext.BaseDirectory;
        Environment.SetEnvironmentVariable("PATH",
            baseDir + ";" + Environment.GetEnvironmentVariable("PATH"));

        Console.WriteLine($"[spike] base dir: {baseDir}");
        Console.WriteLine($"[spike] libmpv present: {File.Exists(Path.Combine(baseDir, "libmpv-2.dll"))}");
        Console.WriteLine($"[spike] yt-dlp present: {File.Exists(Path.Combine(baseDir, "yt-dlp.exe"))}");

        IntPtr ctx = Mpv.mpv_create();
        if (ctx == IntPtr.Zero) { Console.WriteLine("[spike] FAIL: mpv_create returned null"); return 2; }
        Console.WriteLine("[spike] mpv_create OK");

        // Print mpv's own logs to our stdout so we can SEE it decoding.
        Mpv.mpv_set_option_string(ctx, "terminal", "yes");
        Mpv.mpv_set_option_string(ctx, "msg-level", "all=status,ytdl_hook=v");
        Mpv.mpv_set_option_string(ctx, "vo", "gpu");          // mpv creates its own window
        Mpv.mpv_set_option_string(ctx, "ytdl", "yes");
        Mpv.mpv_set_option_string(ctx, "script-opts",
            "ytdl_hook-ytdl_path=" + Path.Combine(baseDir, "yt-dlp.exe"));
        Mpv.mpv_set_option_string(ctx, "ytdl-format", "bestvideo[height<=720]+bestaudio/best[height<=720]/best");
        Mpv.mpv_set_option_string(ctx, "force-window", "yes");

        int rc = Mpv.mpv_initialize(ctx);
        Console.WriteLine($"[spike] mpv_initialize rc={rc}");
        if (rc < 0) { Console.WriteLine("[spike] FAIL: init"); return 3; }

        // Default: "Me at the zoo" — the first & most stable YouTube video. Override via argv[0].
        string url = argv.Length > 0 ? argv[0] : "https://www.youtube.com/watch?v=jNQXAC9IVRw";
        Console.WriteLine($"[spike] loadfile {url}");
        Mpv.mpv_command_string(ctx, $"loadfile \"{url}\"");

        var start = Environment.TickCount64;
        bool fileLoaded = false, videoReconfig = false, restarted = false;
        const long runMs = 22000;

        while (Environment.TickCount64 - start < runMs)
        {
            IntPtr ev = Mpv.mpv_wait_event(ctx, 0.25);
            int id = Mpv.EventId(ev);
            switch (id)
            {
                case MPV_EVENT_START_FILE: Console.WriteLine("[event] START_FILE"); break;
                case MPV_EVENT_FILE_LOADED: fileLoaded = true; Console.WriteLine("[event] FILE_LOADED"); break;
                case MPV_EVENT_VIDEO_RECONFIG: videoReconfig = true; Console.WriteLine("[event] VIDEO_RECONFIG"); break;
                case MPV_EVENT_PLAYBACK_RESTART: restarted = true; Console.WriteLine("[event] PLAYBACK_RESTART (decoding!)"); break;
                case MPV_EVENT_SHUTDOWN: Console.WriteLine("[event] SHUTDOWN"); goto done;
            }
        }
    done:
        Console.WriteLine("[spike] quitting");
        Mpv.mpv_command_string(ctx, "quit");
        Mpv.mpv_terminate_destroy(ctx);

        bool ok = fileLoaded && videoReconfig && restarted;
        Console.WriteLine($"\n[spike] RESULT: fileLoaded={fileLoaded} videoReconfig={videoReconfig} playbackRestart={restarted}");
        Console.WriteLine(ok ? "[spike] SUCCESS: libmpv + yt-dlp pipeline works on this machine."
                            : "[spike] INCONCLUSIVE: see events/logs above.");
        return ok ? 0 : 1;
    }
}
