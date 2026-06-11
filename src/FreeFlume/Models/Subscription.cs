// FreeFlume — a subscribed channel.
// Author: velkadyne
using System;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FreeFlume.Models;

public sealed class Subscription : System.ComponentModel.INotifyPropertyChanged
{
    public string ChannelUrl { get; init; } = "";
    public string ChannelName { get; init; } = "";
    public string AvatarUrl { get; init; } = "";
    public string ChannelId { get; init; } = "";   // YouTube UC… id (for the RSS feed)

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
    public Microsoft.UI.Xaml.Thickness LiveRing => _channelLive ? new(2.5) : new(0);

    public ImageSource? Avatar
    {
        get
        {
            if (string.IsNullOrEmpty(AvatarUrl)) return null;
            var s = AvatarUrl.StartsWith("//") ? "https:" + AvatarUrl : AvatarUrl;
            return Uri.TryCreate(s, UriKind.Absolute, out var uri) ? new BitmapImage(uri) : null;
        }
    }
}
