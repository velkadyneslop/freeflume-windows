// FreeFlume — a single search/browse result (video, channel, or playlist).
// Author: velkadyne
// Mirrors the Linux app's SearchResult struct (see docs/SOURCE-CATALOG.md).

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FreeFlume.Models;

public enum ResultKind { Video, Short, Channel, Playlist }

public sealed class SearchResult : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Url { get; init; } = "";
    public string Title { get; init; } = "";
    public string Channel { get; init; } = "";
    public string ChannelUrl { get; init; } = "";   // uploader/channel page (for subscribe-from-video)
    public string ChannelId { get; init; } = "";     // UC… id when known
    public long DurationSeconds { get; init; }
    public string ThumbnailUrl { get; init; } = "";
    public bool IsLive { get; init; }
    public ResultKind Kind { get; init; } = ResultKind.Video;

    // Views + upload date can arrive late (background metadata fetch for search rows), so they notify
    // and re-raise Meta — the list bindings are {x:Bind Meta, Mode=OneWay}.
    private long _viewCount = -1;
    public long ViewCount
    {
        get => _viewCount;
        set { if (_viewCount == value) return; _viewCount = value; Notify(nameof(ViewCount)); Notify(nameof(Meta)); }
    }
    private long _published;
    public long Published
    {
        get => _published;
        set { if (_published == value) return; _published = value; Notify(nameof(Published)); Notify(nameof(Meta)); }
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));

    /// <summary>Watched portion 0..1 (set when a list is built); 0 = unwatched.</summary>
    public double WatchedFraction { get; set; }

    // ---- live channel indicator (a red ring set by a background probe) ----
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private bool _channelLive;
    public bool ChannelLive
    {
        get => _channelLive;
        set
        {
            if (_channelLive == value) return;
            _channelLive = value;
            PropertyChanged?.Invoke(this, new(nameof(ChannelLive)));
            PropertyChanged?.Invoke(this, new(nameof(LiveRing)));
        }
    }
    /// <summary>Border thickness for the live ring (0 when not live).</summary>
    public Microsoft.UI.Xaml.Thickness LiveRing => _channelLive ? new(2.5) : new(0);

    // ---- "Up Next" queue: highlight the currently-playing row ----
    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new(nameof(IsCurrent)));
            PropertyChanged?.Invoke(this, new(nameof(QueueHighlight)));
        }
    }
    public Microsoft.UI.Xaml.Media.Brush QueueHighlight => _isCurrent
        ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 0x4C, 0xC2, 0xFF))
        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private const double ThumbWidth = 120;
    public double ProgressWidth => WatchedFraction * ThumbWidth;
    public Microsoft.UI.Xaml.Visibility ProgressVisibility =>
        WatchedFraction > 0 ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    // ---- display helpers (bound from XAML; getters run on the UI thread) ----

    public ImageSource? Thumbnail
    {
        get
        {
            var uri = ResolveThumbnailUri();
            return uri is null ? null : new BitmapImage(uri);
        }
    }

    private Uri? ResolveThumbnailUri()
    {
        // Build the stable unsigned thumbnail from the video id (yt-dlp's signed urls can
        // 403). Derive the id from the watch URL when it wasn't stored (History/Playlists).
        string vid = Id.Length == 11 ? Id : ExtractVideoId(Url);
        if ((Kind is ResultKind.Video or ResultKind.Short) && vid.Length == 11)
            // mqdefault is native 16:9 (320x180); hqdefault is 4:3 and letterboxes wide
            // video with black bars top/bottom.
            return new Uri($"https://i.ytimg.com/vi/{vid}/mqdefault.jpg");

        if (string.IsNullOrEmpty(ThumbnailUrl)) return null;
        // Handle protocol-relative ("//host/..") and reject anything non-absolute.
        var s = ThumbnailUrl.StartsWith("//") ? "https:" + ThumbnailUrl : ThumbnailUrl;
        return Uri.TryCreate(s, UriKind.Absolute, out var uri) ? uri : null;
    }

    private static string ExtractVideoId(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"[?&]v=([\w-]{11})");
        if (m.Success) return m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(url, @"/(?:shorts|embed)/([\w-]{11})");
        return m.Success ? m.Groups[1].Value : "";
    }

    public string DurationText => Kind switch
    {
        ResultKind.Channel => "Channel",
        ResultKind.Playlist => "Playlist",
        _ when IsLive => "LIVE",
        _ when DurationSeconds <= 0 => "",
        _ => FormatDuration(DurationSeconds),
    };

    public string Meta =>
        string.Join("   ·   ", new[] { Channel, DurationText, ViewsText, UploadDateText }.Where(s => !string.IsNullOrEmpty(s)));

    private string ViewsText => ViewCount switch
    {
        < 0 => "",
        >= 1_000_000 => $"{ViewCount / 1_000_000.0:0.#}M views",
        >= 1_000 => $"{ViewCount / 1_000.0:0.#}K views",
        _ => $"{ViewCount} views",
    };

    /// <summary>Upload date as a compact "5 Jan 2020" (empty when unknown).</summary>
    private string UploadDateText => Published <= 0 ? ""
        : DateTimeOffset.FromUnixTimeSeconds(Published).ToLocalTime()
            .ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatDuration(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
