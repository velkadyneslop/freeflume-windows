// FreeFlume — seek-bar hover preview sprites (YouTube storyboard).
// Author: velkadyne
//
// A storyboard is a set of sprite-sheet images ("fragments"), each a grid of
// Rows x Columns thumbnails of TileWidth x TileHeight. Each fragment covers
// Duration seconds; TileAt() maps a playback time to the sprite + tile cell.

using System;
using System.Collections.Generic;

namespace FreeFlume.Models;

public sealed record StoryboardFragment(string Url, double Duration);

public sealed class Storyboard
{
    public int TileWidth { get; init; }
    public int TileHeight { get; init; }
    public int Rows { get; init; }
    public int Columns { get; init; }
    public IReadOnlyList<StoryboardFragment> Fragments { get; init; } = Array.Empty<StoryboardFragment>();

    /// <summary>Map a playback time to (sprite url, tile row, tile column), or null.</summary>
    public (string url, int row, int col)? TileAt(double t)
    {
        if (Fragments.Count == 0 || Columns <= 0 || Rows <= 0) return null;
        int tilesPerFragment = Rows * Columns;
        double acc = 0;
        for (int i = 0; i < Fragments.Count; i++)
        {
            var f = Fragments[i];
            bool last = i == Fragments.Count - 1;
            if (t < acc + f.Duration || last)
            {
                double local = f.Duration > 0 ? (t - acc) / f.Duration : 0;
                int idx = (int)Math.Floor(Math.Clamp(local, 0, 0.9999) * tilesPerFragment);
                idx = Math.Clamp(idx, 0, tilesPerFragment - 1);
                return (f.Url, idx / Columns, idx % Columns);
            }
            acc += f.Duration;
        }
        return null;
    }
}
