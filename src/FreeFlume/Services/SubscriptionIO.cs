// FreeFlume — import/export of channel subscriptions.
// Author: velkadyne
//
// Import understands NewPipe (.json), FreeTube (.db, newline-delimited JSON), Google
// Takeout (.csv), OPML (.opml/.xml), and plain URL/text lists. Export writes OPML, which
// NewPipe / FreeTube / most readers can import back. Every channel is reduced to a YT
// channel URL + UC… id (needed for the RSS "What's New" feed).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FreeFlume.Models;

namespace FreeFlume.Services;

public static class SubscriptionIO
{
    public sealed record Entry(string Name, string Url, string ChannelId, string Avatar = "");

    // ---- import ----
    public static List<Entry> Parse(string path)
    {
        string text = File.ReadAllText(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string head = text.TrimStart();

        if (ext is ".opml" or ".xml" || head.StartsWith("<opml", StringComparison.OrdinalIgnoreCase) || text.Contains("<outline", StringComparison.OrdinalIgnoreCase))
            return ParseOpml(text);
        if (ext == ".csv" || head.StartsWith("Channel Id", StringComparison.OrdinalIgnoreCase))
            return ParseCsv(text);
        if (ext is ".json" or ".db" || head.StartsWith('{') || head.StartsWith('['))
            return ParseJson(text);
        return ParseUrls(text);
    }

    private static List<Entry> ParseJson(string text)
    {
        var list = new List<Entry>();
        if (!TryCollect(text, list))                          // whole file: array (YT/Takeout) or object (NewPipe)
            foreach (var line in text.Split('\n'))            // or one object per line (FreeTube .db)
            {
                var t = line.Trim();
                if (t.StartsWith('{')) TryCollect(t, list);
            }
        return Dedupe(list);
    }

    private static bool TryCollect(string json, List<Entry> list)
    {
        try { using var doc = JsonDocument.Parse(json); Walk(doc.RootElement, list); return true; }
        catch { return false; }
    }

    // Recursively find channel entries: arrays of items (YouTube/Takeout) or an object
    // with a "subscriptions" array (NewPipe / a FreeTube profile).
    private static void Walk(JsonElement el, List<Entry> list)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) Walk(item, list);
                break;
            case JsonValueKind.Object:
                if (TryEntry(el, out var e)) list.Add(e);
                if (el.TryGetProperty("subscriptions", out var subs)) Walk(subs, list);
                break;
        }
    }

    private static bool TryEntry(JsonElement o, out Entry entry)
    {
        entry = null!;
        // NewPipe non-YouTube services carry a non-zero service_id — skip them.
        if (o.TryGetProperty("service_id", out var sid) && sid.ValueKind == JsonValueKind.Number && sid.GetInt32() != 0)
            return false;

        string url = GetStr(o, "url");
        string id = ChannelIdFrom(o);
        if (id.Length == 0) id = ExtractId(url);
        bool looksYouTube = id.StartsWith("UC") || (url.Length > 0 && url.Contains("youtube", StringComparison.OrdinalIgnoreCase));
        if (!looksYouTube) return false;

        if (url.Length == 0 && id.Length > 0) url = ChannelUrl(id);
        entry = new Entry(NameFrom(o), url, id, AvatarFrom(o));
        return true;
    }

    private static string ChannelIdFrom(JsonElement o)
    {
        if (o.TryGetProperty("snippet", out var sn) && sn.ValueKind == JsonValueKind.Object)
        {
            if (sn.TryGetProperty("resourceId", out var rid) && rid.ValueKind == JsonValueKind.Object)
            { var c = GetStr(rid, "channelId"); if (c.StartsWith("UC")) return c; }
            var c2 = GetStr(sn, "channelId"); if (c2.StartsWith("UC")) return c2;
        }
        var c3 = GetStr(o, "channelId"); if (c3.StartsWith("UC")) return c3;
        if (o.TryGetProperty("id", out var idEl))                       // FreeTube: id is the UC string
        {
            if (idEl.ValueKind == JsonValueKind.String) { var s = idEl.GetString() ?? ""; if (s.StartsWith("UC")) return s; }
            else if (idEl.ValueKind == JsonValueKind.Object) { var c = GetStr(idEl, "channelId"); if (c.StartsWith("UC")) return c; }
        }
        return "";
    }

    private static string NameFrom(JsonElement o)
    {
        if (o.TryGetProperty("snippet", out var sn) && sn.ValueKind == JsonValueKind.Object)
        { var t = GetStr(sn, "title"); if (t.Length > 0) return t; }
        var n = GetStr(o, "name"); if (n.Length > 0) return n;
        return GetStr(o, "title");
    }

    private static string AvatarFrom(JsonElement o)
    {
        if (o.TryGetProperty("snippet", out var sn) && sn.ValueKind == JsonValueKind.Object &&
            sn.TryGetProperty("thumbnails", out var th) && th.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "high", "medium", "default" })
                if (th.TryGetProperty(key, out var k) && k.ValueKind == JsonValueKind.Object)
                { var u = GetStr(k, "url"); if (u.Length > 0) return u; }
        return GetStr(o, "thumbnail");
    }

    private static List<Entry> ParseCsv(string text)
    {
        var list = new List<Entry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("Channel Id", StringComparison.OrdinalIgnoreCase)) continue;   // header
            var cols = line.Split(',', 3);                     // id, url, title (title may contain commas)
            string id = cols[0].Trim();
            string url = cols.Length > 1 ? cols[1].Trim() : "";
            string name = cols.Length > 2 ? cols[2].Trim() : "";
            if (url.Length == 0 && id.StartsWith("UC")) url = ChannelUrl(id);
            if (id.Length == 0) id = ExtractId(url);
            if (url.Length > 0) list.Add(new Entry(name, url, id));
        }
        return Dedupe(list);
    }

    private static List<Entry> ParseOpml(string text)
    {
        var list = new List<Entry>();
        XDocument doc;
        try { doc = XDocument.Parse(text); } catch { return list; }
        foreach (var o in doc.Descendants().Where(e => e.Name.LocalName == "outline"))
        {
            string xmlUrl = (string?)o.Attribute("xmlUrl") ?? "";
            string name = (string?)o.Attribute("text") ?? (string?)o.Attribute("title") ?? "";
            if (xmlUrl.Length == 0) continue;
            string id = ExtractId(xmlUrl);
            string url = id.Length > 0 ? ChannelUrl(id) : xmlUrl;
            if (id.Length > 0 || url.Contains("youtube", StringComparison.OrdinalIgnoreCase))
                list.Add(new Entry(name, url, id));
        }
        return Dedupe(list);
    }

    private static List<Entry> ParseUrls(string text)
    {
        var list = new List<Entry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!line.Contains("youtube", StringComparison.OrdinalIgnoreCase) && !line.StartsWith("UC")) continue;
            string url = line.StartsWith("UC") ? ChannelUrl(line) : line;
            list.Add(new Entry("", url, ExtractId(url)));
        }
        return Dedupe(list);
    }

    // ---- export ----
    public static string ToOpml(IReadOnlyList<Subscription> subs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<opml version=\"1.1\">");
        sb.AppendLine("  <head><title>FreeFlume subscriptions</title></head>");
        sb.AppendLine("  <body>");
        sb.AppendLine("    <outline text=\"YouTube Subscriptions\" title=\"YouTube Subscriptions\">");
        foreach (var s in subs)
        {
            string id = s.ChannelId.Length > 0 ? s.ChannelId : ExtractId(s.ChannelUrl);
            string xmlUrl = id.Length > 0 ? $"https://www.youtube.com/feeds/videos.xml?channel_id={id}" : s.ChannelUrl;
            string name = Xml(s.ChannelName);
            sb.AppendLine($"      <outline text=\"{name}\" title=\"{name}\" type=\"rss\" xmlUrl=\"{Xml(xmlUrl)}\" />");
        }
        sb.AppendLine("    </outline>");
        sb.AppendLine("  </body>");
        sb.AppendLine("</opml>");
        return sb.ToString();
    }

    // ---- helpers ----
    private static string ChannelUrl(string id) => "https://www.youtube.com/channel/" + id;

    private static string ExtractId(string s)
    {
        var m = Regex.Match(s, @"(UC[\w-]{20,})");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static List<Entry> Dedupe(List<Entry> list)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outp = new List<Entry>();
        foreach (var e in list)
        {
            string key = e.ChannelId.Length > 0 ? e.ChannelId : e.Url;
            if (seen.Add(key)) outp.Add(e);
        }
        return outp;
    }

    private static string GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Xml(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
