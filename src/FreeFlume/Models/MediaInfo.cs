// FreeFlume — playback extras fetched in one yt-dlp pass: the seek storyboard + basic metadata.
// Author: velkadyne

namespace FreeFlume.Models;

public sealed record MediaInfo(
    Storyboard? Storyboard,
    string Title = "",
    string Channel = "",
    string ChannelUrl = "",
    string ThumbnailUrl = "",
    long Duration = 0,
    long ViewCount = -1,
    long Published = 0);   // upload date as unix seconds
