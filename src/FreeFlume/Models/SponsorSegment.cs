// FreeFlume — a SponsorBlock skip segment.
// Author: velkadyne
namespace FreeFlume.Models;

public readonly record struct SponsorSegment(double Start, double End, string Category)
{
    /// <summary>Friendly label for the skip toast.</summary>
    public string Label => Category switch
    {
        "sponsor" => "sponsor",
        "selfpromo" => "self-promo",
        "interaction" => "interaction reminder",
        "intro" => "intro",
        "outro" => "outro",
        "preview" => "preview",
        "music_offtopic" => "non-music section",
        "filler" => "filler",
        _ => Category,
    };
}
