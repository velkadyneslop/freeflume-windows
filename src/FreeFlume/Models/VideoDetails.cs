// FreeFlume — full metadata for a single video, fetched on demand for the detail pane.
// Author: velkadyne

namespace FreeFlume.Models;

public sealed class VideoDetails
{
    public string Title { get; init; } = "";
    public string Channel { get; init; } = "";
    public string ChannelUrl { get; init; } = "";
    public string Description { get; init; } = "";
    public long ViewCount { get; init; } = -1;
    public long LikeCount { get; init; } = -1;
    public long DurationSeconds { get; init; }
    public string UploadDate { get; init; } = "";   // YYYYMMDD
}
