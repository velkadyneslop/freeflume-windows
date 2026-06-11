// FreeFlume — minimal libmpv P/Invoke surface for the WinUI 3 player.
// Author: velkadyne
//
// Only the calls the player needs: core lifecycle, options/commands, and the
// software render API (MPV_RENDER_API_TYPE_SW) which paints frames into a CPU/
// D3D texture surface. The OpenGL render path can be layered on later for perf;
// the SW path is the reliable first integration that proves video-in-SwapChainPanel.

using System.Runtime.InteropServices;

namespace FreeFlume.Player;

internal static class LibMpv
{
    private const string Lib = "libmpv-2.dll";
    private const CallingConvention Cdecl = CallingConvention.Cdecl;

    // ---- mpv_render_param ----
    // C struct: { enum mpv_render_param_type type; void* data; }  (16 bytes on x64)
    [StructLayout(LayoutKind.Sequential)]
    internal struct RenderParam
    {
        public int Type;
        public IntPtr Data;
        public RenderParam(int type, IntPtr data) { Type = type; Data = data; }
    }

    // mpv_render_param_type values (from render.h)
    internal const int PARAM_INVALID = 0;
    internal const int PARAM_API_TYPE = 1;
    internal const int PARAM_BLOCK_FOR_TARGET_TIME = 12;
    internal const int PARAM_SW_SIZE = 17;
    internal const int PARAM_SW_FORMAT = 18;
    internal const int PARAM_SW_STRIDE = 19;
    internal const int PARAM_SW_POINTER = 20;

    // mpv_render_context_update() flags
    internal const ulong RENDER_UPDATE_FRAME = 1 << 0;

    internal delegate void RenderUpdateFn(IntPtr cbCtx);
    internal delegate void WakeupFn(IntPtr cbCtx);

    // ---- core ----
    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern IntPtr mpv_create();

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern void mpv_terminate_destroy(IntPtr ctx);

    [DllImport(Lib, CallingConvention = Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int mpv_set_option_string(IntPtr ctx, string name, string data);

    [DllImport(Lib, CallingConvention = Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int mpv_set_property_string(IntPtr ctx, string name, string data);

    [DllImport(Lib, CallingConvention = Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int mpv_command_string(IntPtr ctx, string args);

    [DllImport(Lib, CallingConvention = Cdecl, CharSet = CharSet.Ansi)]
    internal static extern IntPtr mpv_get_property_string(IntPtr ctx, string name);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern void mpv_free(IntPtr data);

    // Array form: const char **args, NULL-terminated. Avoids command-string escaping
    // (e.g. Windows paths with spaces/backslashes in screenshot-to-file).
    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern int mpv_command(IntPtr ctx, IntPtr[] args);

    [DllImport(Lib, CallingConvention = Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int mpv_request_log_messages(IntPtr ctx, string minLevel);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(Lib, CallingConvention = Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int mpv_observe_property(IntPtr ctx, ulong replyUserdata, string name, int format);

    // mpv_event ids / mpv_format values (client.h)
    internal const int EVENT_SHUTDOWN = 1;
    internal const int EVENT_FILE_LOADED = 8;
    internal const int EVENT_PROPERTY_CHANGE = 22;
    internal const int FORMAT_FLAG = 3;     // value: int* (0/1)
    internal const int FORMAT_DOUBLE = 5;   // value: double*

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern void mpv_set_wakeup_callback(IntPtr ctx, WakeupFn cb, IntPtr d);

    // ---- render context (software) ----
    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern int mpv_render_context_create(out IntPtr res, IntPtr mpv, RenderParam[] @params);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern int mpv_render_context_render(IntPtr ctx, RenderParam[] @params);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern ulong mpv_render_context_update(IntPtr ctx);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern void mpv_render_context_set_update_callback(IntPtr ctx, RenderUpdateFn cb, IntPtr d);

    [DllImport(Lib, CallingConvention = Cdecl)]
    internal static extern void mpv_render_context_free(IntPtr ctx);
}
