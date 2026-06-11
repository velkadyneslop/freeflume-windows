// FreeFlume — YouTube search filters (encoded into the sp= URL parameter).
// Author: velkadyne
namespace FreeFlume.Models;

public sealed class SearchFilters
{
    public int Sort { get; set; }        // 0 relevance, 1 rating, 2 upload date, 3 view count
    public int UploadDate { get; set; }  // 0 any, 1 hour, 2 today, 3 week, 4 month, 5 year
    public int Type { get; set; }        // 0 any, 1 video, 2 channel, 3 playlist, 4 movie
    public int Duration { get; set; }    // 0 any, 1 short(<4m), 2 long(>20m), 3 medium(4-20m)

    // Feature toggles (each maps to a varint field inside the sp= filters message).
    public bool Hd { get; set; }
    public bool Subtitles { get; set; }
    public bool Live { get; set; }
    public bool FourK { get; set; }

    public bool Any => Sort > 0 || UploadDate > 0 || Type > 0 || Duration > 0 || Hd || Subtitles || Live || FourK;
}
