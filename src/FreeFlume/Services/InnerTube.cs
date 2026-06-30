// FreeFlume — InnerTube playlist pagination (bypasses yt-dlp's anonymous ~200-item wall).
// yt-dlp walks a playlist one InnerTube page at a time following each response's continuationToken;
// for a scripted (non-browser) client YouTube stops emitting those tokens after ~200 items, so any
// page past item 200 came back empty ("No videos found"). A YouTube playlist continuation token is a
// base64url protobuf that just encodes the playlist id + an item offset, so we synthesise the offset
// and jump straight to any page in a single request. See docs/playlist-pagination-fix (Linux parity).
// Fully anonymous — no account, cookies, visitor data, or PO token; pure in-process HTTP.
// Author: velkadyne

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FreeFlume.Models;

namespace FreeFlume.Services;

public static class InnerTube
{
    private static readonly HttpClient Http = MakeClient();

    // The long-lived public WEB InnerTube key + a browser-shaped context. No auth of any kind.
    private const string Key = "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";
    private const string Endpoint = "https://www.youtube.com/youtubei/v1/browse?key=" + Key + "&prettyPrint=false";
    private const string Context = "{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20240814.00.00\",\"hl\":\"en\",\"gl\":\"US\"}}";

    private static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        return c;
    }

    /// <summary>The real playlist id from a browse URL (<c>list=</c> / <c>/playlist?list=</c>), or
    /// null when the URL isn't a regular paginated playlist (channels, watch-only, or "RD" Mix radios).</summary>
    public static string? PlaylistId(string url)
    {
        int i = url.IndexOf("list=", StringComparison.Ordinal);
        if (i < 0) return null;
        string id = url[(i + 5)..];
        int cut = id.IndexOfAny(new[] { '&', '#', '/' });
        if (cut >= 0) id = id[..cut];
        if (id.Length == 0 || id.StartsWith("RD")) return null;   // Mix radios aren't real playlists
        return id;
    }

    /// <summary>Fetch one FreeFlume page of a playlist. Mirrors <see cref="YtDlpService.BrowseAsync"/>'s
    /// tuple: (items, total item count, raw count). A huge <paramref name="pageSize"/> (the play-queue)
    /// walks the whole playlist 0, 100, 200, … so a queued playlist is no longer truncated either.</summary>
    public static async Task<(List<SearchResult> items, int total, int rawCount)> FetchPlaylistPageAsync(
        string plId, int page, int pageSize, CancellationToken ct = default)
    {
        int offset = (page - 1) * pageSize;   // exact alignment: offset = (page-1)·pageSize
        var items = new List<SearchResult>();
        int total = 0;

        // Each InnerTube response returns ~100 items; loop until the requested page is covered (or dry).
        while (items.Count < pageSize)
        {
            var (batch, batchTotal) = await FetchOneAsync(plId, offset, ct);
            if (total == 0 && batchTotal > 0) total = batchTotal;
            if (batch.Count == 0) break;
            items.AddRange(batch);
            offset += batch.Count;
        }

        int raw = items.Count;   // before trimming — lets the open-ended pager show "+" if total is unknown
        if (items.Count > pageSize) items = items.GetRange(0, pageSize);
        return (items, total, raw);
    }

    /// <summary>One InnerTube browse request at a given item offset. Offset 0 uses browseId="VL"+id
    /// (that response also carries the playlist total); deeper offsets POST a crafted continuation.</summary>
    private static async Task<(List<SearchResult> items, int total)> FetchOneAsync(string plId, int offset, CancellationToken ct)
    {
        try
        {
            string body = offset <= 0
                ? "{\"context\":" + Context + ",\"browseId\":\"VL" + plId + "\"}"
                : "{\"context\":" + Context + ",\"continuation\":\"" + CraftToken(plId, offset) + "\"}";

            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return (new List<SearchResult>(), 0);

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, default, ct);

            var items = new List<SearchResult>();
            foreach (var lv in FindAll(doc.RootElement, "lockupViewModel"))
            {
                var r = ParseLockup(lv);
                if (r is not null) items.Add(r);
            }
            int total = offset <= 0 ? ParseTotal(doc.RootElement) : 0;
            return (items, total);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (new List<SearchResult>(), 0); }   // network/format break → caller falls back
    }

    // ---- continuation-token crafting (hand-rolled protobuf → base64url), mirrors the YouTube wire format ----
    // token = base64url( field80226972 = { #2 "VL"+id, #3 wrapB, #35 id } )
    //   wrapB = base64( { #1 = 1, #15 = "PT:"+base64({#1 = offset}) } )  with '=' → "%3D"
    private static string CraftToken(string plId, int offset)
    {
        var pt = new List<byte>();
        WriteVarintField(pt, 1, offset);
        string ptStr = "PT:" + Convert.ToBase64String(pt.ToArray()).TrimEnd('=');

        var wrap = new List<byte>();
        WriteVarintField(wrap, 1, 1);
        WriteStringField(wrap, 15, Encoding.UTF8.GetBytes(ptStr));
        string wrapB = Convert.ToBase64String(wrap.ToArray()).Replace("=", "%3D");

        var body = new List<byte>();
        WriteStringField(body, 2, Encoding.UTF8.GetBytes("VL" + plId));
        WriteStringField(body, 3, Encoding.UTF8.GetBytes(wrapB));
        WriteStringField(body, 35, Encoding.UTF8.GetBytes(plId));

        var outer = new List<byte>();
        WriteStringField(outer, 80226972, body.ToArray());
        return Convert.ToBase64String(outer.ToArray()).Replace('+', '-').Replace('/', '_');
    }

    private static void WriteVarintField(List<byte> b, int field, long value)
    {
        WriteVarint(b, (long)((ulong)field << 3));   // wire type 0 (varint)
        WriteVarint(b, value);
    }

    private static void WriteStringField(List<byte> b, int field, byte[] value)
    {
        WriteVarint(b, (long)(((ulong)field << 3) | 2));   // wire type 2 (length-delimited)
        WriteVarint(b, value.Length);
        b.AddRange(value);
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

    // ---- response parsing (modern lockupViewModel node) ----
    private static SearchResult? ParseLockup(JsonElement lv)
    {
        string id = GetStr(lv, "contentId");
        if (id.Length != 11) return null;   // videos only; skip nested playlists/channels
        string ctype = GetStr(lv, "contentType");
        if (ctype.Length > 0 && ctype != "LOCKUP_CONTENT_TYPE_VIDEO") return null;

        string title = "", channel = "", channelId = "";
        if (lv.TryGetProperty("metadata", out var md) && md.TryGetProperty("lockupMetadataViewModel", out var lm))
        {
            if (lm.TryGetProperty("title", out var t) && t.TryGetProperty("content", out var tc) && tc.ValueKind == JsonValueKind.String)
                title = tc.GetString() ?? "";

            foreach (var cmv in FindAll(lm, "contentMetadataViewModel"))   // first row, first part = channel name
            {
                if (cmv.TryGetProperty("metadataRows", out var rows) && rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0)
                    channel = FirstPartText(rows[0]);
                break;
            }
            foreach (var be in FindAll(lm, "browseEndpoint"))   // first channel browseId
            {
                var bid = GetStr(be, "browseId");
                if (bid.StartsWith("UC")) { channelId = bid; break; }
            }
        }
        if (title.Length == 0) return null;   // minimal-and-defensive: contentId + title are required

        long dur = 0;
        if (lv.TryGetProperty("contentImage", out var ci)) dur = FindDuration(ci);

        return new SearchResult
        {
            Id = id,
            Url = "https://www.youtube.com/watch?v=" + id,
            Title = title,
            Channel = channel,
            ChannelId = channelId,
            ChannelUrl = channelId.Length > 0 ? "https://www.youtube.com/channel/" + channelId : "",
            DurationSeconds = dur,
            ViewCount = -1,
            ThumbnailUrl = "",   // SearchResult builds i.ytimg/vi/{id} from the id
            IsLive = false,
            Kind = ResultKind.Video,
        };
    }

    private static string FirstPartText(JsonElement row)
    {
        if (row.TryGetProperty("metadataParts", out var parts) && parts.ValueKind == JsonValueKind.Array)
            foreach (var p in parts.EnumerateArray())
                if (p.TryGetProperty("text", out var tx) && tx.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    return c.GetString() ?? "";
        return "";
    }

    private static long FindDuration(JsonElement contentImage)
    {
        foreach (var badge in FindAll(contentImage, "thumbnailBadgeViewModel"))
        {
            long s = ParseClock(GetStr(badge, "text"));
            if (s > 0) return s;
        }
        return 0;
    }

    // "h:mm:ss" | "m:ss" | "ss" → seconds (0 if not a clock string, e.g. "LIVE").
    private static long ParseClock(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return 0;
        var parts = s.Split(':');
        if (parts.Length is < 1 or > 3) return 0;
        long total = 0;
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out var n) || n < 0) return 0;
            total = total * 60 + n;
        }
        return total;
    }

    private static readonly Regex VideoCount = new(@"^([\d,]+)\s+videos?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static int ParseTotal(JsonElement root)
    {
        foreach (var s in FindAllStrings(root))   // lazy; the "N videos" header string appears early
        {
            var m = VideoCount.Match(s);
            if (m.Success && int.TryParse(m.Groups[1].Value.Replace(",", ""), out var n)) return n;
        }
        return 0;
    }

    private static IEnumerable<JsonElement> FindAll(JsonElement e, string key)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (p.NameEquals(key)) yield return p.Value;
                foreach (var c in FindAll(p.Value, key)) yield return c;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in e.EnumerateArray())
                foreach (var c in FindAll(i, key)) yield return c;
        }
    }

    private static IEnumerable<string> FindAllStrings(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                yield return e.GetString() ?? "";
                break;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                    foreach (var s in FindAllStrings(p.Value)) yield return s;
                break;
            case JsonValueKind.Array:
                foreach (var i in e.EnumerateArray())
                    foreach (var s in FindAllStrings(i)) yield return s;
                break;
        }
    }

    private static string GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
