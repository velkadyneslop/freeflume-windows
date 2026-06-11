// FreeFlume — a local playlist (name + item count).
// Author: velkadyne
namespace FreeFlume.Models;

public sealed class Playlist
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public int ItemCount { get; init; }

    public string Display => $"{Name}  ({ItemCount})";
}
