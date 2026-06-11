// FreeFlume — one queued/active download (observable for live progress).
// Author: velkadyne
using System.Collections.Generic;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;

namespace FreeFlume.Models;

public enum DownloadKind { Video, Audio, Subtitles }

public enum DownloadStatus { Queued, Downloading, Completed, Failed, Canceled }

public partial class DownloadItem : ObservableObject
{
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public DownloadKind Kind { get; init; }

    /// <summary>Explicit yt-dlp format args (codec/container/subs). Null = use the Kind default.</summary>
    internal IReadOnlyList<string>? FormatArgs { get; init; }

    [ObservableProperty] public partial int Percent { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }
    [ObservableProperty] public partial string Speed { get; set; }
    [ObservableProperty] public partial string Eta { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelVisibility))]
    public partial DownloadStatus Status { get; set; }

    /// <summary>Cancel is only meaningful while queued or downloading.</summary>
    public Visibility CancelVisibility =>
        Status is DownloadStatus.Queued or DownloadStatus.Downloading ? Visibility.Visible : Visibility.Collapsed;

    public string? FilePath { get; set; }
    internal Process? Process { get; set; }

    public DownloadItem()
    {
        StatusText = "Queued";
        Speed = "";
        Eta = "";
        Status = DownloadStatus.Queued;
    }
}
