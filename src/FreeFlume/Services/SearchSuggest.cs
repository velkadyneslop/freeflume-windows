// FreeFlume — live YouTube search suggestions (opt-in).
// Author: velkadyne
// Off by default: it sends typed text to Google, against the no-telemetry default. When the user enables
// Settings.EnableSearchSuggestions, the search box queries Google's public suggest endpoint as you type.
// Response shape: ["<query>", ["suggestion 1", "suggestion 2", …], …].

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FreeFlume.Services;

public static class SearchSuggest
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; FreeFlume)");
        return h;
    }

    /// <summary>Live YT query suggestions for the typed text, or an empty list on any failure.</summary>
    public static async Task<List<string>> FetchAsync(string query, CancellationToken ct = default)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(query)) return result;
        try
        {
            var url = "https://suggestqueries.google.com/complete/search?client=firefox&ds=yt&q="
                      + Uri.EscapeDataString(query);
            var json = await Http.GetStringAsync(url, ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return result;
            var arr = root[1];
            if (arr.ValueKind != JsonValueKind.Array) return result;
            foreach (var e in arr.EnumerateArray())
            {
                var s = e.GetString();
                if (!string.IsNullOrWhiteSpace(s)) result.Add(s);
            }
        }
        catch { /* network/parse failure → no suggestions, never throws */ }
        return result;
    }
}
