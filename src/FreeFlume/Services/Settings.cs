// FreeFlume — user settings, persisted as JSON in the app data dir.
// Author: velkadyne
// Keys mirror the Linux app (docs/SOURCE-CATALOG.md). Consumers read Settings.Shared.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeFlume.Services;

public sealed class Settings
{
    // Appearance
    public string ColorScheme { get; set; } = "system";     // system | light | dark

    // Playback
    public string Quality { get; set; } = "Auto";           // Best | Auto | 1080p | 720p | 480p | 360p
    public int Volume { get; set; } = 100;                   // 0..130
    public string HwDecodeMode { get; set; } = "auto-copy";  // mpv hwdec: auto-copy | d3d11va | no
    public bool AutoplayNext { get; set; } = true;
    public bool MiniPlayer { get; set; } = true;            // float a mini player when leaving the full player
    public bool RememberPipSize { get; set; } = true;       // reuse the last picture-in-picture window size
    public int PipWidth { get; set; } = 480;                // remembered PiP window width (physical px)
    public bool HiDpiVideoSharpness { get; set; } = false;  // off by default (subtle; costs CPU/GPU)
    public string ResumeMode { get; set; } = "resume";       // resume | ask | start

    // Privacy
    public bool RememberHistory { get; set; } = true;
    public bool RememberSearch { get; set; } = true;

    // Backends — keep yt-dlp current (YouTube breaks it often). mpv is not self-updated.
    public bool AutoUpdateYtDlp { get; set; } = true;
    public long LastYtDlpCheck { get; set; }     // unix seconds of the last auto-check

    // SponsorBlock — master toggle + per-category mode (0=disabled, 1=auto-skip, 2=manual)
    public bool SponsorBlockEnabled { get; set; } = false;

    public Dictionary<string, int> SponsorBlockModes { get; set; } = new()
    {
        ["sponsor"] = 1,
        ["selfpromo"] = 1,
        ["interaction"] = 1,
        ["intro"] = 2,
        ["outro"] = 2,
        ["preview"] = 2,
        ["music_offtopic"] = 2,
        ["filler"] = 0,
    };

    public int SponsorMode(string category) => SponsorBlockModes.TryGetValue(category, out var m) ? m : 0;

    /// <summary>Categories that should be fetched (auto or manual, not disabled).</summary>
    public List<string> EnabledSponsorCategories() =>
        SponsorBlockModes.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();

    // Search
    public int SearchLimit { get; set; } = 20;               // results per page (5..100)
    public bool SearchIncludeChannels { get; set; } = true;  // show channel results inline
    public bool SearchIncludePlaylists { get; set; } = true; // show playlist results inline
    public bool EnableSearchSuggestions { get; set; } = false; // off: sends typed text to Google

    // Downloads
    public string DownloadFolder { get; set; } = "";         // empty => default
    public int DownloadMaxHeight { get; set; } = 0;          // 0 = no cap; else 2160/1440/1080/720/480/360
    public bool DownloadEmbedSubs { get; set; } = false;     // embed subtitles into video downloads

    // Screenshots
    public string ScreenshotFolder { get; set; } = "";       // empty => <Pictures>\FreeFlume
    public string ScreenshotFormat { get; set; } = "png";    // png | jxl-lossless | jxl-lossy | jpg

    // Keyboard shortcuts (action id -> VK code; missing = use the default)
    public Dictionary<string, int> Shortcuts { get; set; } = new();

    public int ShortcutVk(string id)
    {
        if (Shortcuts.TryGetValue(id, out var v)) return v;
        foreach (var d in FreeFlume.Models.Shortcuts.All) if (d.Id == id) return d.DefaultVk;
        return 0;
    }

    // Subtitles. The player fetches ALL human-made subtitles (you pick the language from the in-player
    // CC menu); SubtitleIncludeAuto also pulls YouTube's auto-generated captions. SubtitleLanguage is
    // only used by the "Download subtitles" action.
    public string SubtitleLanguage { get; set; } = "en";     // download-subtitles language code
    public bool SubtitleIncludeAuto { get; set; } = false;   // also fetch auto-generated captions
    public string SubtitleShadowColor { get; set; } = "#FF000000";
    public string SubtitleFont { get; set; } = "";          // empty => mpv default
    public int SubtitleFontSize { get; set; } = 55;
    public string SubtitleColor { get; set; } = "#FFFFFF";
    public int SubtitleOutline { get; set; } = 3;           // border thickness
    public bool SubtitleBold { get; set; } = false;
    public bool SubtitleBackground { get; set; } = false;   // translucent box behind text
    public int SubtitleShadowOffset { get; set; } = 0;

    // ---- runtime helpers ----

    [JsonIgnore]
    public string EffectiveDownloadFolder =>
        string.IsNullOrWhiteSpace(DownloadFolder) ? AppPaths.DownloadsDir() : DownloadFolder;

    /// <summary>The Quality preset as an mpv ytdl-format string.</summary>
    [JsonIgnore]
    public string QualityFormat => FormatFor(Quality);

    /// <summary>The ytdl-format string for a quality label (shared by Settings + the in-player switcher).</summary>
    public static string FormatFor(string quality) => quality switch
    {
        "Best" => "bestvideo+bestaudio/best",
        "2160p" => "bestvideo[height<=2160]+bestaudio/best[height<=2160]/best",
        "1440p" => "bestvideo[height<=1440]+bestaudio/best[height<=1440]/best",
        "1080p" => "bestvideo[height<=1080]+bestaudio/best[height<=1080]/best",
        "720p" => "bestvideo[height<=720]+bestaudio/best[height<=720]/best",
        "480p" => "bestvideo[height<=480]+bestaudio/best[height<=480]/best",
        "360p" => "bestvideo[height<=360]+bestaudio/best[height<=360]/best",
        _ => "bestvideo*+bestaudio/best",   // Auto
    };

    // ---- persistence ----

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Settings Shared { get; } = Load();

    private static Settings Load()
    {
        try
        {
            var f = AppPaths.SettingsFile();
            if (File.Exists(f))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(f)) ?? new Settings();
        }
        catch { /* fall back to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try { File.WriteAllText(AppPaths.SettingsFile(), JsonSerializer.Serialize(this, JsonOpts)); }
        catch { /* best-effort */ }
    }
}
