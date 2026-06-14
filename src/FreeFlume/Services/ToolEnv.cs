// FreeFlume — make the bundled media tools discoverable to subprocesses.
// Author: velkadyne
// yt-dlp (invoked directly AND spawned by mpv's ytdl hook) needs its sibling tools on PATH:
//  - the app folder, where the bundled yt-dlp.exe / ffmpeg.exe / deno.exe sit (next to FreeFlume.exe);
//  - ~/.deno/bin, the default location of a user-installed Deno (the official `curl … | sh` / install).
// Deno is what lets yt-dlp solve YouTube's "nsig" JS challenge and serve full-resolution formats; without
// it on PATH YouTube falls back to low-res SABR. Idempotent + called once at startup.

using System;
using System.Collections.Generic;
using System.IO;

namespace FreeFlume.Services;

public static class ToolEnv
{
    private static bool _done;

    /// <summary>Prepend the app folder and ~/.deno/bin to this process's PATH (inherited by yt-dlp/mpv).</summary>
    public static void Configure()
    {
        if (_done) return;
        _done = true;
        try
        {
            var parts = new List<string> { AppContext.BaseDirectory.TrimEnd('\\') };

            var denoBin = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".deno", "bin");
            if (Directory.Exists(denoBin)) parts.Add(denoBin);

            var current = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", string.Join(";", parts) + ";" + current);
        }
        catch { /* best-effort: a missing tool just degrades a feature, never crashes */ }
    }
}
