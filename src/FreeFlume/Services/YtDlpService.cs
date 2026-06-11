// FreeFlume — yt-dlp subprocess driver (search/browse extraction).
// Author: velkadyne
// Uses the bundled yt-dlp.exe next to the app. Args mirror docs/SOURCE-CATALOG.md.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreeFlume.Models;

namespace FreeFlume.Services;

public sealed class YtDlpService
{
    private static string _exe => YtDlp.ExePath;

    public sealed class YtDlpException(string message) : Exception(message);

    /// <summary>Search YT (one page). When filters are set, uses the results-page URL with sp=.</summary>
    public async Task<(List<SearchResult> items, int rawCount)> SearchAsync(string query, SearchFilters filters, int page, int pageSize, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return (new List<SearchResult>(), 0);

        int end = page * pageSize;
        int start = (page - 1) * pageSize + 1;

        // Always use the results-page URL (not ytsearch) so channels & playlists appear
        // in the results, just like the website. Filters add the sp= parameter.
        var target = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query);
        var sp = BuildSp(filters);
        if (sp.Length > 0) target += "&sp=" + Uri.EscapeDataString(sp);

        var (items, _, raw) = await RunAndParse(FlatArgs(start, end, target), throwOnEmpty: true, ct);
        return (items, raw);
    }

    /// <summary>Browse a channel or playlist (drill-in). Channel URLs get a /videos tab.
    /// Returns the page items, the playlist's total item count (0 if open-ended), and the raw
    /// entry count yt-dlp returned (so callers can tell a full page from the last page).</summary>
    public Task<(List<SearchResult> items, int total, int rawCount)> BrowseAsync(string url, int page, int pageSize, CancellationToken ct = default)
    {
        int end = page * pageSize;
        int start = (page - 1) * pageSize + 1;
        return RunAndParse(FlatArgs(start, end, NormalizeBrowseUrl(url)), throwOnEmpty: false, ct);
    }

    private static string[] FlatArgs(int start, int end, string target) => new[]
    {
        "--flat-playlist", "--dump-json", "--no-warnings", "--ignore-errors",
        "--playlist-start", start.ToString(), "--playlist-end", end.ToString(),
        target,
    };

    private static string NormalizeBrowseUrl(string url)
    {
        if (url.Contains("/playlist") || url.Contains("list=") || url.Contains("/videos") ||
            url.Contains("/shorts") || url.Contains("/streams") || url.Contains("/playlists") ||
            url.Contains("/featured") || url.Contains("/search") || url.Contains("/community"))
            return url;
        if (url.Contains("/channel/") || url.Contains("/@") || url.Contains("/c/") || url.Contains("/user/"))
            return url.TrimEnd('/') + "/videos";
        return url;
    }

    private async Task<(List<SearchResult> items, int total, int rawCount)> RunAndParse(IReadOnlyList<string> args, bool throwOnEmpty, CancellationToken ct)
    {
        if (!File.Exists(_exe)) throw new YtDlpException("yt-dlp.exe was not found next to the app.");
        var (stdout, stderr, exit) = await RunAsync(args, ct);

        var results = new List<SearchResult>();
        int total = 0, raw = 0;
        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{') continue;
            raw++;   // a raw entry from yt-dlp (before our Shorts/Mix filtering)
            var parsed = TryParse(trimmed);
            if (parsed is not null) results.Add(parsed);
            if (total == 0)   // playlist_count is constant across entries — read it once
            {
                try
                {
                    using var d = JsonDocument.Parse(trimmed);
                    if (d.RootElement.TryGetProperty("playlist_count", out var pc) && pc.ValueKind == JsonValueKind.Number)
                        total = pc.GetInt32();
                }
                catch { }
            }
        }

        if (results.Count == 0 && exit != 0 && throwOnEmpty)
            throw new YtDlpException(FirstLine(stderr) ?? $"yt-dlp exited with code {exit}.");

        return (results, total, raw);
    }

    /// <summary>Fetch full metadata (description, likes, date) for one video — for the detail pane.</summary>
    public async Task<VideoDetails?> GetDetailsAsync(string url, CancellationToken ct = default)
    {
        if (!File.Exists(_exe)) return null;
        var args = new[]
        {
            "--dump-json", "--no-warnings", "--no-playlist",
            "--extractor-args", "youtube:player_client=default,android", url,
        };
        try
        {
            var (stdout, _, _) = await RunAsync(args, ct);
            foreach (var line in stdout.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0 || t[0] != '{') continue;
                using var doc = JsonDocument.Parse(t);
                var root = doc.RootElement;
                return new VideoDetails
                {
                    Title = Str(root, "title"),
                    Channel = FirstNonEmpty(Str(root, "channel"), Str(root, "uploader")),
                    ChannelUrl = FirstNonEmpty(Str(root, "channel_url"), Str(root, "uploader_url")),
                    Description = Str(root, "description"),
                    ViewCount = LongOf(root, "view_count"),
                    LikeCount = LongOf(root, "like_count"),
                    DurationSeconds = (long)Num(root, "duration"),
                    UploadDate = Str(root, "upload_date"),
                };
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort: no detail */ }
        return null;
    }

    /// <summary>One yt-dlp pass for playback extras: the seek storyboard + distinct audio languages.</summary>
    public async Task<MediaInfo?> GetMediaInfoAsync(string url, CancellationToken ct = default)
    {
        if (!File.Exists(_exe)) return null;
        var args = new[] { "--dump-json", "--no-warnings", "--no-playlist", url };
        try
        {
            var (stdout, _, _) = await RunAsync(args, ct);
            foreach (var line in stdout.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length == 0 || t[0] != '{') continue;
                using var doc = JsonDocument.Parse(t);
                var root = doc.RootElement;
                string channel = Str(root, "channel"); if (channel.Length == 0) channel = Str(root, "uploader");
                string channelUrl = Str(root, "channel_url"); if (channelUrl.Length == 0) channelUrl = Str(root, "uploader_url");
                long views = root.TryGetProperty("view_count", out var vc) && vc.ValueKind == JsonValueKind.Number ? vc.GetInt64() : -1;
                return new MediaInfo(
                    ParseStoryboard(root),
                    Str(root, "title"), channel, channelUrl, Str(root, "thumbnail"), (long)Num(root, "duration"),
                    views, UploadDateToUnix(Str(root, "upload_date")));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* best-effort */ }
        return null;
    }

    /// <summary>yt-dlp's "YYYYMMDD" upload date → unix seconds (0 if absent/unparseable).</summary>
    private static long UploadDateToUnix(string ymd)
    {
        if (ymd.Length != 8) return 0;
        return System.DateTime.TryParseExact(ymd, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
            ? new System.DateTimeOffset(dt, System.TimeSpan.Zero).ToUnixTimeSeconds() : 0;
    }

    private static Storyboard? ParseStoryboard(JsonElement root)
    {
        if (!root.TryGetProperty("formats", out var formats) || formats.ValueKind != JsonValueKind.Array) return null;

        // Pick the storyboard whose TILE width is best for a hover preview (~160px): the
        // largest width that doesn't exceed 160, else the smallest available.
        JsonElement best = default; int bestW = 0; bool found = false;
        foreach (var f in formats.EnumerateArray())
        {
            bool isSb = Str(f, "format_note").Equals("storyboard", StringComparison.OrdinalIgnoreCase)
                        || Str(f, "format_id").StartsWith("sb");
            if (!isSb) continue;
            if (!f.TryGetProperty("fragments", out var fr) || fr.ValueKind != JsonValueKind.Array || fr.GetArrayLength() == 0) continue;
            int w = (int)Num(f, "width");
            if (w <= 0) continue;
            if (!found || BetterTile(w, bestW)) { bestW = w; best = f; found = true; }
        }
        if (!found) return null;

        var frags = new List<StoryboardFragment>();
        foreach (var fr in best.GetProperty("fragments").EnumerateArray())
        {
            string u = Str(fr, "url");
            if (u.Length > 0) frags.Add(new StoryboardFragment(u, Num(fr, "duration")));
        }
        if (frags.Count == 0) return null;

        return new Storyboard
        {
            TileWidth = (int)Num(best, "width"),
            TileHeight = (int)Num(best, "height"),
            Rows = (int)Num(best, "rows"),
            Columns = (int)Num(best, "columns"),
            Fragments = frags,
        };
    }

    // True if tile width w is a better preview size than cur (target ~160px, never oversized).
    private static bool BetterTile(int w, int cur)
    {
        bool wOk = w <= 160, curOk = cur <= 160;
        if (wOk && curOk) return w > cur;     // both fit: prefer larger
        if (wOk) return true;                 // w fits, cur too big
        if (curOk) return false;
        return w < cur;                       // both too big: prefer smaller
    }

    // ---- YouTube sp= filter encoding (protobuf, base64) ----
    private static string BuildSp(SearchFilters f)
    {
        var inner = new List<byte>();
        if (f.UploadDate > 0) WriteVarintField(inner, 1, f.UploadDate);
        if (f.Type > 0) WriteVarintField(inner, 2, f.Type);
        if (f.Duration > 0) WriteVarintField(inner, 3, f.Duration);
        if (f.Hd) WriteVarintField(inner, 4, 1);          // HD
        if (f.Subtitles) WriteVarintField(inner, 5, 1);   // subtitles / CC
        if (f.Live) WriteVarintField(inner, 8, 1);        // live
        if (f.FourK) WriteVarintField(inner, 14, 1);      // 4K

        var outer = new List<byte>();
        if (f.Sort > 0) WriteVarintField(outer, 1, f.Sort);
        if (inner.Count > 0)
        {
            WriteVarint(outer, (2 << 3) | 2);   // field 2, wire type 2 (length-delimited)
            WriteVarint(outer, inner.Count);
            outer.AddRange(inner);
        }
        return outer.Count == 0 ? "" : Convert.ToBase64String(outer.ToArray());
    }

    private static void WriteVarintField(List<byte> b, int field, long value)
    {
        WriteVarint(b, (field << 3) | 0);  // wire type 0 (varint)
        WriteVarint(b, value);
    }

    private static void WriteVarint(List<byte> b, long value)
    {
        ulong u = (ulong)value;
        do
        {
            byte by = (byte)(u & 0x7F);
            u >>= 7;
            if (u != 0) by |= 0x80;
            b.Add(by);
        } while (u != 0);
    }

    // ---- process + parse ----
    private async Task<(string stdout, string stderr, int exit)> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try { proc.Start(); }
        catch (Exception ex) { throw new YtDlpException("Could not launch yt-dlp: " + ex.Message); }

        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (await outTask, await errTask, proc.ExitCode);
    }

    private static SearchResult? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string id = Str(root, "id");
            string url = Str(root, "url");
            if (url.Length == 0) url = Str(root, "webpage_url");
            if (url.Length == 0 && id.Length > 0) url = $"https://www.youtube.com/watch?v={id}";
            if (url.Length == 0) return null;

            string ieKey = Str(root, "ie_key");
            var kind = ResultKind.Video;
            if (ieKey.Contains("Tab")) kind = url.Contains("playlist") || url.Contains("list=") ? ResultKind.Playlist : ResultKind.Channel;
            else if (url.Contains("/shorts/")) kind = ResultKind.Short;

            // Drop auto-generated "Mix" radio playlists (their list/id always starts with RD).
            if (kind == ResultKind.Playlist && (id.StartsWith("RD") || url.Contains("list=RD")))
                return null;

            // Shorts are excluded on desktop.
            if (kind == ResultKind.Short) return null;

            return new SearchResult
            {
                Id = id,
                Url = url,
                Title = Str(root, "title"),
                Channel = FirstNonEmpty(Str(root, "channel"), Str(root, "uploader")),
                ChannelUrl = FirstNonEmpty(Str(root, "channel_url"), Str(root, "uploader_url")),
                ChannelId = Str(root, "channel_id"),
                DurationSeconds = (long)Num(root, "duration"),
                ViewCount = root.TryGetProperty("view_count", out var vc) && vc.ValueKind == JsonValueKind.Number ? vc.GetInt64() : -1,
                ThumbnailUrl = BestThumbnail(root),
                IsLive = Str(root, "live_status") == "is_live",
                Kind = kind,
            };
        }
        catch { return null; }
    }

    private static string BestThumbnail(JsonElement root)
    {
        if (root.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
        {
            string best = ""; int bestW = -1;
            foreach (var t in thumbs.EnumerateArray())
            {
                int w = t.TryGetProperty("width", out var we) && we.ValueKind == JsonValueKind.Number ? we.GetInt32() : 0;
                string u = t.TryGetProperty("url", out var ue) ? ue.GetString() ?? "" : "";
                if (u.Length > 0 && w >= bestW) { bestW = w; best = u; }
            }
            if (best.Length > 0) return best;
        }
        return Str(root, "thumbnail");
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static long LongOf(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : -1;

    private static string FirstNonEmpty(params string[] xs) { foreach (var x in xs) if (!string.IsNullOrEmpty(x)) return x; return ""; }

    private static string? FirstLine(string s)
    {
        foreach (var l in s.Split('\n')) { var t = l.Trim(); if (t.Length > 0) return t; }
        return null;
    }
}
