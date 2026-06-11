// FreeFlume — subscription feeds via YouTube channel RSS.
// Author: velkadyne
// Feed URL: https://www.youtube.com/feeds/videos.xml?channel_id=UC...

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using FreeFlume.Models;

namespace FreeFlume.Services;

public sealed class SubscriptionFeed
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Yt = "http://www.youtube.com/xml/schemas/2015";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";

    /// <summary>Recent uploads for a single channel (empty on any failure).</summary>
    public async Task<List<SearchResult>> ChannelFeedAsync(string channelId, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        if (string.IsNullOrEmpty(channelId)) return results;
        try
        {
            var url = $"https://www.youtube.com/feeds/videos.xml?channel_id={channelId}";
            await using var stream = await Http.GetStreamAsync(url, ct);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
            if (doc.Root is null) return results;

            foreach (var entry in doc.Root.Elements(Atom + "entry"))
            {
                string videoId = entry.Element(Yt + "videoId")?.Value ?? "";
                if (videoId.Length == 0) continue;

                long published = 0;
                if (DateTimeOffset.TryParse(entry.Element(Atom + "published")?.Value, out var dto))
                    published = dto.ToUnixTimeSeconds();

                var group = entry.Element(Media + "group");
                string thumb = group?.Element(Media + "thumbnail")?.Attribute("url")?.Value ?? "";

                long views = -1;
                var stats = group?.Element(Media + "community")?.Element(Media + "statistics");
                if (stats is not null && long.TryParse(stats.Attribute("views")?.Value, out var v)) views = v;

                results.Add(new SearchResult
                {
                    Id = videoId,
                    Url = $"https://www.youtube.com/watch?v={videoId}",
                    Title = entry.Element(Atom + "title")?.Value ?? "",
                    Channel = entry.Element(Atom + "author")?.Element(Atom + "name")?.Value ?? "",
                    ThumbnailUrl = thumb,
                    Published = published,
                    ViewCount = views,
                    Kind = ResultKind.Video,
                });
            }
        }
        catch { /* feed unavailable -> empty */ }
        return results;
    }

    /// <summary>Merged recent uploads across all subscriptions, newest first.</summary>
    public async Task<List<SearchResult>> WhatsNewAsync(IEnumerable<Subscription> subs, CancellationToken ct = default)
    {
        var tasks = subs.Where(s => !string.IsNullOrEmpty(s.ChannelId))
                        .Select(s => ChannelFeedAsync(s.ChannelId, ct));
        var lists = await Task.WhenAll(tasks);
        return lists.SelectMany(x => x)
                    .OrderByDescending(r => r.Published)
                    .Take(100)
                    .ToList();
    }
}
