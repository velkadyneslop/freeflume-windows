// FreeFlume — best-effort "is this channel live right now?" probe.
// Author: velkadyne
// The subs RSS feed and the flat search JSON don't carry live state, so we check the channel's
// /live page (the live watch page when streaming) over plain HTTP. Cached + concurrency-throttled
// so it stays cheap and fully background; failures just mean "not live" (no ring).

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FreeFlume.Services;

public sealed class LiveStatus
{
    public static readonly LiveStatus Shared = new();

    private static readonly HttpClient Http = CreateClient();
    private readonly Dictionary<string, (bool live, long at)> _cache = new();
    private readonly SemaphoreSlim _gate = new(4);   // at most 4 probes in flight
    private const long TtlMs = 180_000;              // re-check a channel at most every ~3 min

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        h.DefaultRequestHeaders.Add("Cookie", "CONSENT=YES+1");   // skip the EU consent interstitial
        return h;
    }

    /// <summary>True if the channel currently has an active live stream. Best-effort, cached ~3 min.</summary>
    public async Task<bool> IsLiveAsync(string channelUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(channelUrl)) return false;
        long now = Environment.TickCount64;
        lock (_cache)
            if (_cache.TryGetValue(channelUrl, out var c) && now - c.at < TtlMs) return c.live;

        await _gate.WaitAsync(ct);
        try
        {
            lock (_cache)
                if (_cache.TryGetValue(channelUrl, out var c) && Environment.TickCount64 - c.at < TtlMs) return c.live;

            bool live = await ProbeAsync(channelUrl, ct);
            lock (_cache) _cache[channelUrl] = (live, Environment.TickCount64);
            return live;
        }
        catch { return false; }
        finally { _gate.Release(); }
    }

    public sealed record LiveVideo(string Id, string Title);

    /// <summary>The channel's primary currently-live stream (id + title) from /live, or null. Cheap gate
    /// used to decide whether to scan the channel's full stream list.</summary>
    public async Task<LiveVideo?> LiveVideoAsync(string channelUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(channelUrl)) return null;
        await _gate.WaitAsync(ct);
        try
        {
            var (html, finalUrl) = await HeadAsync(channelUrl.TrimEnd('/') + "/live", ct);
            if (!IsLiveHtml(html)) return null;

            // Video id: prefer the final redirected watch URL (/live -> /watch?v=ID), else the HTML.
            var m = System.Text.RegularExpressions.Regex.Match(finalUrl, @"[?&]v=([\w-]{11})");
            if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(html, @"""videoId""\s*:\s*""([\w-]{11})""");
            if (!m.Success) return null;

            var t = System.Text.RegularExpressions.Regex.Match(html, @"<title>(.*?)\s*-\s*YouTube</title>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            string title = t.Success ? System.Net.WebUtility.HtmlDecode(t.Groups[1].Value).Trim() : "Live now";
            return new LiveVideo(m.Groups[1].Value, title);
        }
        catch { return null; }
        finally { _gate.Release(); }
    }

    /// <summary>True if a specific video is *currently* live right now. Cached ~3 min, concurrency-capped.</summary>
    public async Task<bool> IsVideoLiveAsync(string videoId, CancellationToken ct = default)
    {
        if (videoId is not { Length: 11 }) return false;
        string key = "v:" + videoId;
        long now = Environment.TickCount64;
        lock (_cache) if (_cache.TryGetValue(key, out var c) && now - c.at < TtlMs) return c.live;

        await _gate.WaitAsync(ct);
        try
        {
            lock (_cache) if (_cache.TryGetValue(key, out var c) && Environment.TickCount64 - c.at < TtlMs) return c.live;
            var (html, _) = await HeadAsync("https://www.youtube.com/watch?v=" + videoId, ct);
            bool live = IsLiveHtml(html);
            lock (_cache) _cache[key] = (live, Environment.TickCount64);
            return live;
        }
        catch { return false; }
        finally { _gate.Release(); }
    }

    private static async Task<bool> ProbeAsync(string channelUrl, CancellationToken ct)
    {
        var (html, _) = await HeadAsync(channelUrl.TrimEnd('/') + "/live", ct);
        return IsLiveHtml(html);
    }

    // A *currently* live watch page has the HLS manifest and videoDetails.isLive=true.
    // (Scheduled/upcoming streams have neither, so they don't false-positive.)
    private static bool IsLiveHtml(string html) =>
        html.Contains("hlsManifestUrl") && html.Contains("\"isLive\":true");

    /// <summary>GET the first ~1 MB of a page (ytInitial* blocks are near the top) + the final URL.</summary>
    private static async Task<(string html, string finalUrl)> HeadAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return ("", "");
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[1 << 16];
        int read, total = 0;
        while (total < 1_000_000 && (read = await stream.ReadAsync(buf, ct)) > 0) { ms.Write(buf, 0, read); total += read; }
        return (System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length),
                resp.RequestMessage?.RequestUri?.ToString() ?? url);
    }
}
