// FreeFlume — tiny file logger for the Stage-2 spike (WinExe has no console).
// Author: velkadyne
using System.IO;

namespace FreeFlume.Player;

internal static class SpikeLog
{
    private static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "freeflume_spike.log");

    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path, $"[{System.Environment.TickCount64,10}] {message}{System.Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}
