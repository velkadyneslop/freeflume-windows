// FreeFlume — keep the display + system awake while a video is actually playing.
// Author: velkadyne
// Windows equivalent of the Linux D-Bus idle inhibition: SetThreadExecutionState with
// ES_DISPLAY_REQUIRED|ES_SYSTEM_REQUIRED while playing, reset to ES_CONTINUOUS to release.
// Call only from the UI thread (the per-thread state must live on a long-lived thread).

using System;
using System.Runtime.InteropServices;

namespace FreeFlume.Services;

public static class KeepAwake
{
    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(ExecutionState flags);

    private static bool _active;

    /// <summary>Hold the system + display awake (true) or release it (false). Idempotent.</summary>
    public static void Set(bool keepAwake)
    {
        if (keepAwake == _active) return;
        _active = keepAwake;
        SetThreadExecutionState(keepAwake
            ? ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired
            : ExecutionState.Continuous);
    }
}
