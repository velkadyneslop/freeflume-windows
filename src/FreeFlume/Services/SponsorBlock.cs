// FreeFlume — SponsorBlock segment lookup (privacy-preserving hash-prefix API).
// Author: velkadyne
// GET https://sponsor.ajay.app/api/skipSegments/<sha256(videoId)[:4]>?categories=[..]&actionTypes=["skip"]

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreeFlume.Models;

namespace FreeFlume.Services;

public sealed class SponsorBlock
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>All categories with display name + the standard SponsorBlock color.</summary>
    public static readonly (string Key, string Name, string Color)[] CategoryInfo =
    {
        ("sponsor",        "Sponsor",              "#FF00D400"),
        ("selfpromo",      "Self-promotion",       "#FFFFFF00"),
        ("interaction",    "Interaction reminder", "#FFCC00FF"),
        ("intro",          "Intro / intermission", "#FF00FFFF"),
        ("outro",          "Outro / credits",      "#FF0202ED"),
        ("preview",        "Preview / recap",      "#FF008FD6"),
        ("music_offtopic", "Non-music section",    "#FFFF9900"),
        ("filler",         "Filler tangent",       "#FF7300FF"),
    };

    public static string ColorFor(string category)
    {
        foreach (var c in CategoryInfo) if (c.Key == category) return c.Color;
        return "#FF888888";
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("FreeFlume/1.0");
        return c;
    }

    /// <summary>Skip segments for a video id (empty on failure). 3 attempts, 700ms backoff.</summary>
    public async Task<List<SponsorSegment>> FetchSegmentsAsync(string videoId, IReadOnlyList<string> categories, CancellationToken ct = default)
    {
        var result = new List<SponsorSegment>();
        if (string.IsNullOrEmpty(videoId) || categories.Count == 0) return result;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(videoId))).ToLowerInvariant();
        var prefix = hash.Substring(0, 4);
        var url = $"https://sponsor.ajay.app/api/skipSegments/{prefix}" +
                  $"?categories={Uri.EscapeDataString(JsonSerializer.Serialize(categories))}" +
                  $"&actionTypes={Uri.EscapeDataString("[\"skip\"]")}";

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var resp = await Http.GetAsync(url, ct);
                if ((int)resp.StatusCode == 404) return result;        // no segments for this prefix
                if (!resp.IsSuccessStatusCode) { await Backoff(attempt, ct); continue; }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, default, ct);
                Parse(doc.RootElement, videoId, result);
                return result;
            }
            catch (OperationCanceledException) { return result; }
            catch { await Backoff(attempt, ct); }
        }
        return result;
    }

    private static void Parse(JsonElement root, string videoId, List<SponsorSegment> result)
    {
        if (root.ValueKind != JsonValueKind.Array) return;
        foreach (var entry in root.EnumerateArray())
        {
            // Hash-prefix returns multiple videos; keep only the exact match.
            if (!entry.TryGetProperty("videoID", out var vid) || vid.GetString() != videoId) continue;
            if (!entry.TryGetProperty("segments", out var segs) || segs.ValueKind != JsonValueKind.Array) continue;

            foreach (var s in segs.EnumerateArray())
            {
                if (s.TryGetProperty("actionType", out var at) && at.GetString() != "skip") continue;
                if (!s.TryGetProperty("segment", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 2) continue;
                double start = arr[0].GetDouble();
                double end = arr[1].GetDouble();
                string cat = s.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "";
                if (end > start) result.Add(new SponsorSegment(start, end, cat));
            }
        }
    }

    private static Task Backoff(int attempt, CancellationToken ct) => Task.Delay(700, ct);
}
