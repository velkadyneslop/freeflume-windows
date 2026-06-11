// FreeFlume — rebindable player keyboard shortcuts (action id, label, default VK code).
// Author: velkadyne

namespace FreeFlume.Models;

public sealed record ShortcutDef(string Id, string Label, int DefaultVk);

public static class Shortcuts
{
    // Enter (SponsorBlock skip), Esc (exit/back) and Shift+arrows (frame step) stay fixed.
    public static readonly ShortcutDef[] All =
    {
        new("PlayPause", "Play / Pause", 0x20),        // Space
        new("SeekBackward", "Seek backward 5s", 0x25), // Left
        new("SeekForward", "Seek forward 5s", 0x27),   // Right
        new("VolumeUp", "Volume up", 0x26),            // Up
        new("VolumeDown", "Volume down", 0x28),        // Down
        new("Mute", "Mute", 0x4D),                     // M
        new("Captions", "Captions on / off", 0x43),    // C
        new("Fullscreen", "Fullscreen", 0x46),         // F
        new("Loop", "Loop", 0x52),                     // R
        new("PreviousVideo", "Previous in queue", 0x50),// P
        new("NextVideo", "Next in queue", 0x4E),       // N
        new("PreviousFrame", "Previous frame", 0xBC),  // ,
        new("NextFrame", "Next frame", 0xBE),          // .
        new("Screenshot", "Screenshot", 0x53),         // S
        new("Info", "Info panel", 0x49),               // I
        new("Queue", "Up Next panel", 0x51),           // Q
    };
}
