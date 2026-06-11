// FreeFlume — an mpv audio/subtitle track, for the track selectors.
// Author: velkadyne

using System.Collections.Generic;

namespace FreeFlume.Models;

public sealed record MediaTrack(int Id, string Type, string Lang, string Title, bool Selected)
{
    /// <summary>Human label, e.g. "English (United States)" or "Track 2" — the readable name only,
    /// no language-code prefix.</summary>
    public string Label
    {
        get
        {
            if (Title.Length > 0) return Title;                  // mpv's readable name, e.g. "English (United States)"
            if (Lang.Length > 0)
            {
                try { return new System.Globalization.CultureInfo(Lang).DisplayName; }   // "en-US" -> readable
                catch { return Lang; }                          // odd code with no culture: show it raw
            }
            return $"Track {Id}";
        }
    }
}
