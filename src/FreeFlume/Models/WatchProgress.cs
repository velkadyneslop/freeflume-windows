// FreeFlume — saved playback position for a video (resume + watched indicators).
// Author: velkadyne
namespace FreeFlume.Models;

public readonly record struct WatchProgress(long Position, long Duration, bool Completed)
{
    public static readonly WatchProgress None = new(0, 0, false);
}
