// FreeFlume — the one consistent right-click menu shared by every result list.
// Author: velkadyne
//
// Pages attach ContextRequested on their ListView and forward it to ResultMenu.Show,
// passing a ResultMenuContext that wires the page-specific bits (how Play seeds the
// autoplay queue, an optional Remove-from-X, channel drill-in).

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FreeFlume.Models;
using FreeFlume.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace FreeFlume.Views
{
    /// <summary>Describes how a page wants the shared result menu to behave.</summary>
    public sealed class ResultMenuContext
    {
        public required XamlRoot XamlRoot { get; init; }
        public required Action<SearchResult> Play { get; init; }   // play (page seeds its autoplay queue)
        public Action<SearchResult>? OpenChannel { get; init; }    // channel results (Search drill-in)
        public string? RemoveLabel { get; init; }                  // e.g. "Remove from History"
        public Action<SearchResult>? Remove { get; init; }         // paired with RemoveLabel
        public Action? Changed { get; init; }                      // called after subscribe/unsubscribe
    }

    public static class ResultMenu
    {
        /// <summary>Build + show the menu for whichever row raised ContextRequested.</summary>
        public static void Show(UIElement owner, ContextRequestedEventArgs e, ResultMenuContext ctx)
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is not SearchResult r) return;
            var menu = Build(r, ctx);
            if (e.TryGetPosition(owner, out var pos)) menu.ShowAt(owner, new FlyoutShowOptions { Position = pos });
            else if (e.OriginalSource is FrameworkElement fe) menu.ShowAt(fe);
            e.Handled = true;
        }

        public static MenuFlyout Build(SearchResult r, ResultMenuContext ctx) =>
            r.Kind == ResultKind.Channel ? BuildChannel(r, ctx) : BuildVideo(r, ctx);

        // ---- video ----
        private static MenuFlyout BuildVideo(SearchResult r, ResultMenuContext ctx)
        {
            var m = new MenuFlyout();
            m.Items.Add(Item("Play", () => ctx.Play(r)));
            m.Items.Add(BuildPlaylistSub(r, ctx.XamlRoot));
            m.Items.Add(BuildDownloadSub(r));
            m.Items.Add(new MenuFlyoutSeparator());
            m.Items.Add(Item("Copy Link", () => CopyLink(ShortVideoUrl(r))));
            m.Items.Add(Item("Open in Browser", () => OpenInBrowser(r.Url)));

            string chUrl = ChannelUrlOf(r);
            if (chUrl.Length > 0)
            {
                m.Items.Add(new MenuFlyoutSeparator());
                bool subbed = Database.Shared.IsSubscribed(chUrl);
                m.Items.Add(Item(subbed ? "Unsubscribe from Channel" : "Subscribe to Channel", () =>
                {
                    if (subbed) Database.Shared.Unsubscribe(chUrl);
                    else Database.Shared.Subscribe(r.Channel, chUrl, ChannelIdOf(r));
                    ctx.Changed?.Invoke();
                }));
            }

            if (ctx.Remove is not null && ctx.RemoveLabel is not null)
            {
                m.Items.Add(new MenuFlyoutSeparator());
                m.Items.Add(Item(ctx.RemoveLabel, () => ctx.Remove(r)));
            }
            return m;
        }

        // ---- channel ----
        private static MenuFlyout BuildChannel(SearchResult r, ResultMenuContext ctx)
        {
            var m = new MenuFlyout();
            if (ctx.OpenChannel is not null) m.Items.Add(Item("Open Channel", () => ctx.OpenChannel(r)));
            bool subbed = Database.Shared.IsSubscribed(r.Url);
            m.Items.Add(Item(subbed ? "Unsubscribe" : "Subscribe", () =>
            {
                if (subbed) Database.Shared.Unsubscribe(r.Url);
                else Database.Shared.Subscribe(r.Title.Length > 0 ? r.Title : r.Channel, r.Url, ChannelIdOf(r));
                ctx.Changed?.Invoke();
            }));
            m.Items.Add(new MenuFlyoutSeparator());
            m.Items.Add(Item("Copy Link", () => CopyLink(r.Url)));
            m.Items.Add(Item("Open in Browser", () => OpenInBrowser(r.Url)));
            return m;
        }

        public static MenuFlyoutSubItem BuildPlaylistSub(SearchResult r, XamlRoot xamlRoot)
        {
            var sub = new MenuFlyoutSubItem { Text = "Save to Playlist" };
            foreach (var pl in Database.Shared.Playlists())
            {
                long id = pl.Id;
                sub.Items.Add(Item(pl.Name, () => Database.Shared.AddToPlaylist(id, r)));
            }
            if (sub.Items.Count > 0) sub.Items.Add(new MenuFlyoutSeparator());
            sub.Items.Add(Item("New Playlist…", () => _ = NewPlaylist(r, xamlRoot)));
            return sub;
        }

        public static MenuFlyoutSubItem BuildDownloadSub(SearchResult r)
        {
            var dl = new MenuFlyoutSubItem { Text = "Download" };

            var video = new MenuFlyoutSubItem { Text = "Video" };
            video.Items.Add(Item("MKV (Best Quality)", () => Video(r, "", "mkv")));
            video.Items.Add(Item("MKV (AV1)", () => Video(r, "[vcodec^=av01]", "mkv")));
            video.Items.Add(Item("WebM (VP9)", () => Video(r, "[vcodec^=vp9]", "webm")));
            video.Items.Add(Item("MP4 (H.264/AVC)", () => Video(r, "[vcodec^=avc1]", "mp4")));
            dl.Items.Add(video);

            var audio = new MenuFlyoutSubItem { Text = "Audio" };
            audio.Items.Add(Item("MP3", () => Audio(r, "mp3")));
            audio.Items.Add(Item("M4A (AAC)", () => Audio(r, "m4a")));
            audio.Items.Add(Item("Opus", () => Audio(r, "opus")));
            audio.Items.Add(Item("Vorbis (OGG)", () => Audio(r, "vorbis")));
            dl.Items.Add(audio);

            string lang = string.IsNullOrWhiteSpace(Settings.Shared.SubtitleLanguage) ? "en" : Settings.Shared.SubtitleLanguage;
            dl.Items.Add(Item("Subtitles (SRT)", () => DownloadManager.Shared.Enqueue(r, DownloadKind.Subtitles, new[]
            {
                "--skip-download", "--write-subs", "--write-auto-subs", "--convert-subs", "srt",
                "--sleep-subtitles", "1", "--sub-langs", lang,
            })));
            return dl;
        }

        private static void Video(SearchResult r, string codecFilter, string container)
        {
            int h = Settings.Shared.DownloadMaxHeight;
            string hf = h > 0 ? $"[height<={h}]" : "";   // resolution cap from settings
            string fmt = $"bv*{codecFilter}{hf}+ba/b{hf}";
            var args = new System.Collections.Generic.List<string> { "-f", fmt, "--merge-output-format", container };
            if (Settings.Shared.DownloadEmbedSubs)
            {
                string lang = string.IsNullOrWhiteSpace(Settings.Shared.SubtitleLanguage) ? "en" : Settings.Shared.SubtitleLanguage;
                args.Add("--embed-subs"); args.Add("--write-subs"); args.Add("--sub-langs"); args.Add(lang);
                if (Settings.Shared.SubtitleIncludeAuto) args.Add("--write-auto-subs");
            }
            DownloadManager.Shared.Enqueue(r, DownloadKind.Video, args.ToArray());
        }

        private static void Audio(SearchResult r, string fmt) =>
            DownloadManager.Shared.Enqueue(r, DownloadKind.Audio, new[] { "-x", "--audio-format", fmt, "--audio-quality", "0" });

        private static async System.Threading.Tasks.Task NewPlaylist(SearchResult r, XamlRoot xamlRoot)
        {
            var box = new TextBox { PlaceholderText = "Playlist name" };
            var dlg = new ContentDialog
            {
                Title = "New playlist",
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = box,
                XamlRoot = xamlRoot,
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                var name = box.Text.Trim();
                if (name.Length > 0) Database.Shared.AddToPlaylist(Database.Shared.CreatePlaylist(name), r);
            }
        }

        // ---- channel helpers ----
        private static string ChannelUrlOf(SearchResult r)
        {
            if (r.ChannelUrl.Length > 0) return r.ChannelUrl;
            if (r.ChannelId.StartsWith("UC")) return "https://www.youtube.com/channel/" + r.ChannelId;
            return "";
        }

        private static string ChannelIdOf(SearchResult r)
        {
            if (r.ChannelId.StartsWith("UC")) return r.ChannelId;
            if (r.Id.StartsWith("UC")) return r.Id;
            var m = System.Text.RegularExpressions.Regex.Match(r.Url, @"/channel/(UC[\w-]+)");
            return m.Success ? m.Groups[1].Value : r.ChannelId;
        }

        // ---- shared actions ----
        /// <summary>Short youtu.be link for a video (used for all "Copy Link"); falls back to the full URL.</summary>
        public static string ShortVideoUrl(SearchResult r)
        {
            string id = r.Id.Length == 11 ? r.Id : ExtractVideoId(r.Url);
            return id.Length == 11 ? "https://youtu.be/" + id : r.Url;
        }

        private static string ExtractVideoId(string url)
        {
            var m = Regex.Match(url, @"(?:[?&]v=|/shorts/|/embed/|youtu\.be/)([\w-]{11})");
            return m.Success ? m.Groups[1].Value : "";
        }

        public static void CopyLink(string url)
        {
            var dp = new DataPackage();
            dp.SetText(url);
            Clipboard.SetContent(dp);
        }

        public static async void OpenInBrowser(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) await Windows.System.Launcher.LaunchUriAsync(uri);
        }

        private static MenuFlyoutItem Item(string text, Action onClick)
        {
            var it = new MenuFlyoutItem { Text = text };
            it.Click += (_, __) => onClick();
            return it;
        }
    }
}
