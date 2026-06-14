using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using FreeFlume.Models;
using FreeFlume.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Anim = Microsoft.UI.Xaml.Media.Animation;
using Windows.System;

namespace FreeFlume.Views
{
    /// <summary>Plays a result in the embedded mpv player with a transport bar + shortcuts.</summary>
    public sealed partial class PlayerPage : Page
    {
        /// <summary>The player while it's shown full-screen-page (keyboard shortcuts target it). Null while mini.</summary>
        public static PlayerPage? Active { get; private set; }

        /// <summary>The single cached page instance (lives even while mini), for the shell to drive.</summary>
        public static PlayerPage? Instance { get; private set; }

        /// <summary>Navigation parameter meaning "re-show the already-playing video" (mini → full).</summary>
        public static readonly object Resume = new();

        private bool _seeking;   // user is scrubbing the seek bar
        private bool _suppress;  // ignore slider ValueChanged coming from playback updates
        private string? _currentUrl;
        private string _currentTitle = "";
        private string _currentVideoId = "";
        private string _currentChannel = "";
        private string _currentChannelUrl = "";
        private bool _fromUrl;    // launched from a pasted URL (no SearchResult) — fill metadata + history from yt-dlp

        /// <summary>Optional start position (seconds) for the next load — e.g. a description timestamp.</summary>
        public static double? PendingStart;
        private double _resumePos;
        private bool _loop;      // loop-file toggle (R)
        private bool _ended;     // guard against eof-reached firing twice for one file
        private bool _pip;       // true while the video is playing in the separate PiP window
        private bool _forceClose;                           // explicit close — stop instead of going mini
        private readonly DispatcherTimer _resumeHide = new() { Interval = TimeSpan.FromSeconds(12) };
        private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(5) };

        // Optional play queue (set by the launching page) for autoplay-next + P/N.
        public static (List<SearchResult> items, int index)? PendingQueue;
        /// <summary>A playlist URL whose full contents should replace the queue (fetched in the background).</summary>
        public static string? PendingQueueSourceUrl;
        private List<SearchResult>? _queue;
        private int _queueIndex = -1;
        private readonly System.Collections.ObjectModel.ObservableCollection<SearchResult> _queueView = new();
        private SearchResult? _currentQueueItem;   // tracked by reference so reordering keeps the index right

        private readonly SponsorBlock _sb = new();
        private List<SponsorSegment>? _segments;
        private List<Chapter> _chapters = new();

        private readonly YtDlpService _yt = new();
        private Storyboard? _storyboard;
        private System.Threading.CancellationTokenSource? _storyboardCts;
        private readonly Dictionary<string, BitmapImage> _spriteCache = new();
        private string _previewUrl = "";   // sprite currently shown in the preview
        private static readonly HttpClient _http = new();
        private SponsorSegment? _manualSeg;   // segment awaiting a manual Enter-to-skip
        private double? _revertPos;           // where Enter/tap jumps back to after an auto-skip (segment start)
        private double _suppressSkipStart = -1; // start of a reverted segment — don't auto-skip it again
        private readonly DispatcherTimer _skipHide = new() { Interval = TimeSpan.FromSeconds(2.5) };
        private readonly DispatcherTimer _revertHide = new() { Interval = TimeSpan.FromSeconds(6) };
        private readonly DispatcherTimer _hideControls = new() { Interval = TimeSpan.FromSeconds(3) };

        // Glyph codepoints live in XAML string resources (kept out of C# for tooling reasons).
        private string Glyph(string key) => (string)Resources[key];

        public PlayerPage()
        {
            this.InitializeComponent();
            // Cache the page so the mpv player survives navigation (mini-player + seamless resume).
            NavigationCacheMode = NavigationCacheMode.Required;
            Instance = this;

            QueueList.ItemsSource = _queueView;
            _queueView.CollectionChanged += OnQueueReordered;

            Player.PositionChanged += OnPosition;
            Player.DurationChanged += OnDuration;
            Player.PausedChanged += OnPaused;
            Player.MutedChanged += OnMuted;
            Player.EndReached += OnEndReached;
            Player.ChaptersChanged += OnChapters;
            Player.TracksChanged += OnTracks;
            Player.TitleChanged += OnTitle;
            Player.ContextRequested += OnVideoContextRequested;
            Player.DoubleTapped += OnVideoDoubleTapped;   // double-click toggles fullscreen
            _autosave.Tick += (_, __) => SaveProgress();

            // Click-to-seek + scrub: track pointer on the slider.
            SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSeekPressed), true);
            SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSeekReleased), true);
            SeekSlider.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnSeekReleased), true);

            // Storyboard hover preview over the whole seek area (incl. the slider track).
            SeekArea.AddHandler(PointerMovedEvent, new PointerEventHandler(OnSeekHover), true);
            SeekArea.AddHandler(PointerExitedEvent, new PointerEventHandler(OnSeekHoverExit), true);

            _resumeHide.Tick += (_, __) => HideResumeBanner();
            _skipHide.Tick += (_, __) => { _skipHide.Stop(); SkipToast.Visibility = Visibility.Collapsed; };
            _revertHide.Tick += (_, __) => { _revertHide.Stop(); _revertPos = null; SkipToast.Visibility = Visibility.Collapsed; };
            // Auto-hide the controls + title after idle (windowed and fullscreen), unless paused
            // or the pointer is over the bars.
            _hideControls.Tick += (_, __) => { _hideControls.Stop(); if (!Player.Paused && !_overControls) SetControlsVisible(false); };
            TransportBar.PointerEntered += (_, __) => { _overControls = true; SetControlsVisible(true); };
            TransportBar.PointerExited += (_, __) => { _overControls = false; RestartHideTimer(); };
            TopBar.PointerEntered += (_, __) => { _overControls = true; SetControlsVisible(true); };
            TopBar.PointerExited += (_, __) => { _overControls = false; RestartHideTimer(); };
            BuildSpeedMenu();
            BuildQualityMenu();
        }

        private bool _overControls;   // pointer is hovering the transport/top bar

        // ---- speed + quality dropdowns ----
        private void BuildSpeedMenu()
        {
            var menu = new MenuFlyout();
            foreach (var s in new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 })
            {
                double sp = s;
                var item = new MenuFlyoutItem { Text = sp == 1.0 ? "1× (Normal)" : $"{sp}×" };
                item.Click += (_, __) => { Act.SetSpeed(sp); SpeedButton.Content = sp == 1.0 ? "1×" : $"{sp}×"; };
                menu.Items.Add(item);
            }
            SpeedButton.Flyout = menu;
        }

        private static readonly string[] Qualities = { "Best", "2160p", "1440p", "1080p", "720p", "480p", "360p", "Auto" };

        private void BuildQualityMenu()
        {
            var menu = new MenuFlyout();
            foreach (var q in Qualities)
            {
                string label = q;
                var item = new MenuFlyoutItem { Text = label };
                item.Click += (_, __) => ChangeQuality(label);
                menu.Items.Add(item);
            }
            QualityButton.Flyout = menu;
            QualityButton.Content = Settings.Shared.Quality;
        }

        private string _currentQuality = Settings.Shared.Quality;
        private string _currentLang = "";

        private void ChangeQuality(string q)
        {
            _currentQuality = q;
            QualityButton.Content = q;
            ApplyFormatReload();
        }

        // Reload the current video with the chosen quality + audio language, keeping position.
        private void ApplyFormatReload()
        {
            string baseFmt = Settings.FormatFor(_currentQuality);
            string fmt = _currentLang.Length == 0 ? baseFmt
                       : baseFmt.Replace("bestaudio", $"bestaudio[language^={_currentLang}]");
            double pos = Player.Position;
            Player.SetYtdlFormat(fmt);
            if (_currentUrl is not null) Player.Play(_currentUrl, pos);
        }

        private void OnLoopClick(object sender, RoutedEventArgs e) => ToggleLoop();

        // ---- subtitle tracks (mpv sid). Audio *languages* come from yt-dlp (BeginMediaInfo). ----
        private System.Collections.Generic.List<MediaTrack> _subTracks = new();
        private bool _subsOn;
        private int _preferredSid = -1;

        /// <summary>mpv reports the loaded video's title; use it when we don't already have one
        /// (e.g. a pasted URL — a SearchResult carries its own, more accurate title).</summary>
        private void OnTitle(string title)
        {
            if (title.Length == 0 || _currentTitle.Length > 0) return;
            _currentTitle = title;
            TitleText.Text = title;
            ShellPage.Current?.UpdateMiniTitle(title);
        }

        private void OnTracks(System.Collections.Generic.List<MediaTrack> tracks)
        {
            _subTracks = tracks.FindAll(t => t.Type == "sub");
            BuildSubMenu();

            var selSub = _subTracks.Find(t => t.Selected);
            _subsOn = selSub is not null;
            if (selSub is not null) _preferredSid = selSub.Id;
            CcIcon.Foreground = Highlight(_subsOn);
            CcButton.IsEnabled = _subTracks.Count > 0;
        }

        private void BuildSubMenu()
        {
            var menu = new MenuFlyout();
            var off = new MenuFlyoutItem { Text = "Off" };
            off.Click += (_, __) => { Act.SetSubTrack("no"); _subsOn = false; CcIcon.Foreground = Highlight(false); };
            menu.Items.Add(off);
            foreach (var t in _subTracks)
            {
                int id = t.Id;
                var item = new MenuFlyoutItem { Text = t.Label };
                item.Click += (_, __) => SelectSub(id);
                menu.Items.Add(item);
            }
            CcButton.Flyout = menu;
        }

        // Primary CC click (and the C shortcut) toggles captions on/off.
        private void OnCcToggle(SplitButton sender, SplitButtonClickEventArgs args) => ToggleSubs();

        private void ToggleSubs()
        {
            if (_subsOn) { Act.SetSubTrack("no"); _subsOn = false; }
            else
            {
                int sid = _preferredSid > 0 ? _preferredSid : (_subTracks.Count > 0 ? _subTracks[0].Id : -1);
                if (sid <= 0) return;   // no subtitles available
                Act.SetSubTrack(sid.ToString()); _preferredSid = sid; _subsOn = true;
            }
            CcIcon.Foreground = Highlight(_subsOn);
        }

        private void SelectSub(int id)
        {
            _preferredSid = id; _subsOn = true;
            Act.SetSubTrack(id.ToString());
            CcIcon.Foreground = Highlight(true);
        }

        private void OnChannelClick(object sender, RoutedEventArgs e) => OpenCurrentChannel();

        private void OpenCurrentChannel()
        {
            if (_currentChannelUrl.Length == 0) return;
            SearchPage.PendingBrowse = (_currentChannelUrl, _currentChannel);
            ShellPage.Current?.SelectTab(0);   // open Search, which drills into the channel
        }

        // ---- right-click context menu on the video surface ----
        private void OnVideoContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e)
        {
            if (_currentUrl is null) return;
            var menu = BuildVideoMenu();
            if (e.TryGetPosition(sender, out var pos))
                menu.ShowAt(sender, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = pos });
            else
                menu.ShowAt(Player);
            e.Handled = true;
        }

        private MenuFlyout BuildVideoMenu()
        {
            var r = CurrentResult();
            var m = new MenuFlyout();
            m.Items.Add(VItem(Act.Paused ? "Play" : "Pause", () => Act.TogglePause()));
            m.Items.Add(VItem("Fullscreen", ToggleFullscreen));
            m.Items.Add(VItem(_pip ? "Exit picture-in-picture" : "Picture-in-picture", TogglePip));
            if (_currentChannelUrl.Length > 0) m.Items.Add(VItem("Open channel", OpenCurrentChannel));
            m.Items.Add(ResultMenu.BuildPlaylistSub(r, XamlRoot));
            m.Items.Add(ResultMenu.BuildDownloadSub(r));
            m.Items.Add(new MenuFlyoutSeparator());
            m.Items.Add(VItem("Copy Link", () => ResultMenu.CopyLink(ResultMenu.ShortVideoUrl(r))));
            m.Items.Add(VItem("Copy Link at Current Time",
                () => ResultMenu.CopyLink(ResultMenu.ShortVideoUrl(r) + "?t=" + (int)Player.Position)));
            m.Items.Add(VItem("Open in Browser", () => ResultMenu.OpenInBrowser(_currentUrl!)));
            return m;
        }

        private SearchResult CurrentResult() => new()
        {
            Id = _currentVideoId,
            Url = _currentUrl ?? "",
            Title = _currentTitle,
            Channel = _currentChannel,
            ChannelUrl = _currentChannelUrl,
            DurationSeconds = (long)Player.Duration,
        };

        private static MenuFlyoutItem VItem(string text, Action onClick)
        {
            var it = new MenuFlyoutItem { Text = text };
            it.Click += (_, __) => onClick();
            return it;
        }

        private static Microsoft.UI.Xaml.Media.SolidColorBrush Highlight(bool on) =>
            new(on ? Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF) : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

        private async void OnInfoClick(object sender, RoutedEventArgs e)
        {
            if (_currentUrl is null) return;
            // Toggle: a second tap on (i) closes the panel.
            if (InfoPanel.Visibility == Visibility.Visible) { HideInfoPanel(); return; }
            HideQueuePanel();   // the two side panels share the right edge

            InfoTitle.Text = _currentTitle.Length > 0 ? _currentTitle : "Video info";
            InfoChannel.Content = _currentChannel;
            InfoChannel.Visibility = _currentChannel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            InfoStats.Text = "";
            InfoDesc.Text = "Loading…";
            ShowInfoPanel();
            try
            {
                var d = await _yt.GetDetailsAsync(_currentUrl);
                if (d is null) { InfoDesc.Text = "Details unavailable."; return; }

                if (d.Channel.Length > 0)
                {
                    _currentChannel = d.Channel;
                    InfoChannel.Content = d.Channel;
                    InfoChannel.Visibility = Visibility.Visible;
                }
                // History-played videos don't store the channel URL; adopt it from details so the
                // links (here and in the title bar) work.
                if (_currentChannelUrl.Length == 0 && d.ChannelUrl.Length > 0)
                {
                    _currentChannelUrl = d.ChannelUrl;
                    ChannelButton.Content = _currentChannel;
                    ChannelButton.Visibility = Visibility.Visible;
                }

                InfoStats.Text = StatsLineOf(d);
                // Timestamps seek the player (panel stays open); URLs open in the browser.
                DescriptionInlines.Fill(InfoDesc, d.Description, Player.SeekAbsolute);
            }
            catch { InfoDesc.Text = "Details unavailable."; }
        }

        private void OnInfoClose(object sender, RoutedEventArgs e) => HideInfoPanel();

        private void OnInfoChannelClick(object sender, RoutedEventArgs e) => OpenCurrentChannel();

        // Deterministic slide from the right edge (PaneThemeTransition was direction/retrigger-flaky).
        private void ShowInfoPanel()
        {
            InfoPanelTransform.X = InfoPanel.Width;   // start off-screen to avoid a flash
            InfoPanel.Visibility = Visibility.Visible;
            SlideInfo(InfoPanel.Width, 0, null);
        }

        private void HideInfoPanel()
        {
            if (InfoPanel.Visibility != Visibility.Visible) return;
            SlideInfo(0, InfoPanel.Width, () => InfoPanel.Visibility = Visibility.Collapsed);
        }

        private void SlideInfo(double from, double to, Action? onDone)
        {
            var anim = new Anim.DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new Anim.CubicEase { EasingMode = Anim.EasingMode.EaseOut },
            };
            Anim.Storyboard.SetTarget(anim, InfoPanelTransform);
            Anim.Storyboard.SetTargetProperty(anim, "X");
            var sb = new Anim.Storyboard();
            sb.Children.Add(anim);
            if (onDone is not null) sb.Completed += (_, __) => onDone();
            sb.Begin();
        }

        private void OnVideoDoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            ToggleFullscreen();
            e.Handled = true;   // don't let the gesture bubble to the NavigationView (it re-showed the pane)
        }

        private static string StatsLineOf(VideoDetails d)
        {
            var parts = new List<string>();   // channel shown as a separate link
            if (d.ViewCount >= 0) parts.Add($"{d.ViewCount:N0} views");
            if (d.LikeCount >= 0) parts.Add($"{d.LikeCount:N0} likes");
            if (d.UploadDate.Length == 8 &&
                DateTime.TryParseExact(d.UploadDate, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var up))
                parts.Add(up.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture));
            return string.Join("   ·   ", parts.FindAll(s => s.Length > 0));
        }

        /// <summary>Re-push subtitle styling onto the running player (called when settings change).</summary>
        public void ApplySubtitleStyle() => Player.ApplySubtitleStyle();

        private void OnManualSkipClick(object sender, RoutedEventArgs e) => ManualSkip();

        // Skip the pending manual segment and arm the revert window (auto-skips are NOT revertable —
        // only the user's own manual skips can be undone).
        private void ManualSkip()
        {
            if (_manualSeg is not SponsorSegment ms) return;
            _revertPos = ms.Start;
            Act.SeekAbsolute(ms.End);
            ClearManualPrompt();
            ShowSkipToast(ms.Label);
        }

        private void ShowResumeBanner(double pos)
        {
            _resumePos = pos;
            ResumeText.Text = $"Resume from {Fmt(pos)}?";
            ResumeBanner.Visibility = Visibility.Visible;
            _resumeHide.Stop();
            _resumeHide.Start();
        }

        private void HideResumeBanner()
        {
            _resumeHide.Stop();
            ResumeBanner.Visibility = Visibility.Collapsed;
        }

        private void OnResumeClick(object sender, RoutedEventArgs e)
        {
            Player.SeekAbsolute(_resumePos);
            HideResumeBanner();
        }

        private void OnDismissResume(object sender, RoutedEventArgs e) => HideResumeBanner();

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Active = this;
            ShellPage.Current?.HideMiniPlayer();   // we're full now
            AttachPlayerToSelf();                  // pull the surface back from the mini overlay
            ShellPage.Current?.SetPaneOpen(false); // collapse the sidebar while watching

            // Resume = re-showing the already-playing video (from the mini player): don't reload.
            if (ReferenceEquals(e.Parameter, Resume))
            {
                _autosave.Start();
                RestartHideTimer();
                return;
            }

            // Pick up a play queue the launching page left for us (autoplay-next + P/N).
            if (PendingQueue is { } q) SetQueue(q.items, q.index);
            else SetQueue(null, -1);
            PendingQueue = null;
            var queueSource = PendingQueueSourceUrl; PendingQueueSourceUrl = null;

            if (e.Parameter is SearchResult r) LoadResult(r);
            else if (e.Parameter is string url)
            {
                _fromUrl = true;
                _currentUrl = url; _currentTitle = ""; _currentVideoId = ExtractVideoId(url);
                _currentChannel = ""; _currentChannelUrl = ""; ChannelButton.Visibility = Visibility.Collapsed;
                _ended = false; OnChapters(new List<Chapter>()); OnTracks(new List<MediaTrack>()); _currentLang = "";
                Player.Play(url); StartSponsor(_currentVideoId); BeginMediaInfo();
            }

            // For a playlist, pull the whole thing into the queue in the background.
            if (queueSource is not null) _ = LoadFullQueueAsync(queueSource);

            _autosave.Start();
            RestartHideTimer();                      // start the idle auto-hide
        }

        // Replace the (single-page) queue with the playlist's full contents, fetched in one flat
        // yt-dlp pass, keeping the currently-playing video as the current item.
        private async System.Threading.Tasks.Task LoadFullQueueAsync(string browseUrl)
        {
            string? expect = _currentUrl;
            if (expect is null) return;
            try
            {
                var (full, _, _) = await _yt.BrowseAsync(browseUrl, 1, 5000);
                if (_currentUrl != expect) return;          // user moved on while we fetched
                if (full.Count <= _queueView.Count) return; // nothing more than we already have

                int idx = full.FindIndex(x =>
                    (x.Id.Length == 11 && x.Id == _currentVideoId) || x.Url == _currentUrl);
                Database.Shared.FillWatchProgress(full);

                _suppressQueueSync = true;
                _queue = full;
                _queueView.Clear();
                foreach (var it in full) _queueView.Add(it);
                _suppressQueueSync = false;

                _queueIndex = idx;
                _currentQueueItem = idx >= 0 ? full[idx] : null;
                SyncQueueHighlight();
                SetQueueControlsVisible(full.Count > 1);
            }
            catch { /* keep the partial queue */ }
        }

        private void AttachPlayerToSelf() => Player.MoveTo(VideoHost);

        /// <summary>Load and start a result (initial play and each autoplay/queue advance).</summary>
        private void LoadResult(SearchResult r)
        {
            _fromUrl = false;
            _currentUrl = r.Url;
            _currentTitle = r.Title;
            _currentVideoId = r.Id.Length == 11 ? r.Id : ExtractVideoId(r.Url);
            _ended = false;
            TitleText.Text = r.Title;
            _currentChannel = r.Channel;
            _currentChannelUrl = r.ChannelUrl.Length > 0 ? r.ChannelUrl
                                 : (r.ChannelId.StartsWith("UC") ? "https://www.youtube.com/channel/" + r.ChannelId : "");
            ChannelButton.Content = r.Channel;
            ChannelButton.Visibility = r.Channel.Length > 0 && _currentChannelUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearManualPrompt();
            _revertPos = null; _suppressSkipStart = -1;
            HideResumeBanner();
            InfoPanel.Visibility = Visibility.Collapsed;
            OnChapters(new List<Chapter>());   // clear stale chapters until the new file loads
            OnTracks(new List<MediaTrack>());
            _currentLang = "";

            double? start = PendingStart; PendingStart = null;
            var wp = Database.Shared.GetProgress(r.Url);
            if (Settings.Shared.RememberHistory) Database.Shared.AddHistory(r);

            bool resumable = wp.Position > 5 && !wp.Completed &&
                             (wp.Duration <= 0 || wp.Position < wp.Duration - 15);
            var mode = Settings.Shared.ResumeMode;
            if (start.HasValue)
                Player.Play(r.Url, start.Value);                 // jump to a description timestamp
            else if (resumable && mode == "resume")
                Player.Play(r.Url, wp.Position);
            else
            {
                Player.Play(r.Url);
                if (resumable && mode == "ask") ShowResumeBanner(wp.Position);
            }

            StartSponsor(r.Id.Length == 11 ? r.Id : ExtractVideoId(r.Url));
            BeginMediaInfo();
        }

        private void StartSponsor(string videoId)
        {
            _segments = null;
            RedrawSegments();
            if (Settings.Shared.SponsorBlockEnabled && videoId.Length > 0) _ = LoadSegments(videoId);
        }

        // ---- progress + autoplay queue ----
        private void SaveProgress()
        {
            if (_currentUrl is null || Player.Duration <= 0) return;
            bool completed = Player.Position >= Player.Duration - 15;
            Database.Shared.SetProgress(_currentUrl, (long)Player.Position, (long)Player.Duration, completed);
        }

        private void OnEndReached()
        {
            if (_ended) return;
            _ended = true;
            if (_currentUrl is not null && Player.Duration > 0)
                Database.Shared.SetProgress(_currentUrl, (long)Player.Duration, (long)Player.Duration, true);
            if (_loop) return;   // loop-file repeats the same video; don't advance
            if (Settings.Shared.AutoplayNext) PlayNext();
        }

        private void PlayNext() { if (_queue is not null && _queueIndex + 1 < _queue.Count) PlayQueueIndex(_queueIndex + 1); }
        private void PlayPrev() { if (_queue is not null && _queueIndex > 0) PlayQueueIndex(_queueIndex - 1); }

        private void PlayQueueIndex(int idx)
        {
            if (_queue is null || idx < 0 || idx >= _queue.Count) return;
            SaveProgress();
            _queueIndex = idx;
            _currentQueueItem = _queue[idx];
            LoadResult(_queue[idx]);
            SyncQueueHighlight();
        }

        // ---- "Up Next" queue panel ----
        private void SetQueue(List<SearchResult>? items, int index)
        {
            _queue = items;
            _queueIndex = index;
            _currentQueueItem = (items is not null && index >= 0 && index < items.Count) ? items[index] : null;

            _suppressQueueSync = true;
            _queueView.Clear();
            if (items is not null) foreach (var it in items) _queueView.Add(it);
            _suppressQueueSync = false;
            SyncQueueHighlight();

            bool has = items is not null && items.Count > 1;
            SetQueueControlsVisible(has);
            if (!has && QueuePanel.Visibility == Visibility.Visible) HideQueuePanel();
        }

        // Up Next button + the bottom-bar prev/next buttons only make sense with a queue/playlist.
        private void SetQueueControlsVisible(bool show)
        {
            var v = show ? Visibility.Visible : Visibility.Collapsed;
            QueueButton.Visibility = v;
            PrevButton.Visibility = v;
            NextButton.Visibility = v;
        }

        private bool _suppressQueueSync;   // ignore CollectionChanged during programmatic (re)population

        private void OnQueueReordered(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // Only user drag-reorders reach here (programmatic (re)population is suppressed).
            if (_suppressQueueSync) return;
            _queue = new List<SearchResult>(_queueView);
            if (_currentQueueItem is not null) _queueIndex = _queueView.IndexOf(_currentQueueItem);
        }

        private void SyncQueueHighlight()
        {
            for (int i = 0; i < _queueView.Count; i++) _queueView[i].IsCurrent = i == _queueIndex;
            if (_queueIndex >= 0 && _queueIndex < _queueView.Count)
                QueueList.ScrollIntoView(_queueView[_queueIndex]);
        }

        private void OnQueueItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SearchResult r)
            {
                int idx = _queueView.IndexOf(r);
                if (idx >= 0 && idx != _queueIndex) PlayQueueIndex(idx);
            }
        }

        private void OnQueueClick(object sender, RoutedEventArgs e) => ToggleQueuePanel();

        private void ToggleQueuePanel()
        {
            if (QueuePanel.Visibility == Visibility.Visible) HideQueuePanel();
            else ShowQueuePanel();
        }

        private void OnQueueClose(object sender, RoutedEventArgs e) => HideQueuePanel();

        private void ShowQueuePanel()
        {
            if (_queue is null || _queue.Count <= 1) return;
            HideInfoPanel();   // the two side panels share the right edge
            QueueAutoplayCheck.IsChecked = Settings.Shared.AutoplayNext;
            SyncQueueHighlight();
            QueuePanelTransform.X = QueuePanel.Width;
            QueuePanel.Visibility = Visibility.Visible;
            SlideQueue(QueuePanel.Width, 0, null);
        }

        private void HideQueuePanel()
        {
            if (QueuePanel.Visibility != Visibility.Visible) return;
            SlideQueue(0, QueuePanel.Width, () => QueuePanel.Visibility = Visibility.Collapsed);
        }

        private void SlideQueue(double from, double to, Action? onDone)
        {
            var anim = new Anim.DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new Anim.CubicEase { EasingMode = Anim.EasingMode.EaseOut },
            };
            Anim.Storyboard.SetTarget(anim, QueuePanelTransform);
            Anim.Storyboard.SetTargetProperty(anim, "X");
            var sb = new Anim.Storyboard();
            sb.Children.Add(anim);
            if (onDone is not null) sb.Completed += (_, __) => onDone();
            sb.Begin();
        }

        private void OnQueueAutoplayToggled(object sender, RoutedEventArgs e)
        {
            bool v = QueueAutoplayCheck.IsChecked == true;
            if (v == Settings.Shared.AutoplayNext) return;
            Settings.Shared.AutoplayNext = v;
            Settings.Shared.Save();
        }

        private void ToggleLoop()
        {
            _loop = !_loop;
            Act.SetLoop(_loop);
            LoopIcon.Foreground = Highlight(_loop);
            ShowToast(_loop ? "Loop on" : "Loop off");
        }

        private async System.Threading.Tasks.Task LoadSegments(string videoId)
        {
            var segs = await _sb.FetchSegmentsAsync(videoId, Settings.Shared.EnabledSponsorCategories());
            if (_currentUrl is not null && _currentUrl.Contains(videoId)) // ignore if we've moved on
            {
                _segments = segs;
                RedrawSegments();
            }
        }

        private static string ExtractVideoId(string url)
        {
            var m = System.Text.RegularExpressions.Regex.Match(url, @"[?&]v=([\w-]{11})");
            if (m.Success) return m.Groups[1].Value;
            m = System.Text.RegularExpressions.Regex.Match(url, @"/(?:shorts|embed)/([\w-]{11})");
            return m.Success ? m.Groups[1].Value : "";
        }

        // The skip toast doubles as the revert affordance: it stays up for the whole revert window
        // (6s), and pressing Enter OR tapping it within that window undoes the auto-skip.
        private void ShowSkipToast(string label)
        {
            _lastShotPath = null;
            SkipToastText.Text = $"⏭ Skipped {label} — Enter to revert";
            SkipToast.Visibility = Visibility.Visible;
            _skipHide.Stop();
            _revertHide.Stop();
            _revertHide.Start();
        }

        private void DoRevert(double pos)
        {
            _suppressSkipStart = pos;   // don't immediately re-skip the segment we land back in
            _revertPos = null;
            _revertHide.Stop();
            SkipToast.Visibility = Visibility.Collapsed;
            Act.SeekAbsolute(pos);
            ShowToast("↩ Reverted skip");
        }

        private void ShowToast(string text)
        {
            _lastShotPath = null;   // only screenshot toasts are tappable-to-open
            SkipToastText.Text = text;
            SkipToast.Visibility = Visibility.Visible;
            _skipHide.Stop();
            _skipHide.Start();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            // Navigating away while in PiP: close the PiP window without resuming here, and end playback.
            if (_pip) { _suppressPipReturn = true; _pipWindow?.Close(); _forceClose = true; }
            if (Active == this) Active = null;
            Player.SetCursorVisible(true);   // never leave the cursor hidden when we leave the player
            _hideControls.Stop();
            _storyboardCts?.Cancel();
            HidePreview();
            InfoPanel.Visibility = Visibility.Collapsed;
            QueuePanel.Visibility = Visibility.Collapsed;
            PipBanner.Visibility = Visibility.Collapsed;
            SaveProgress();

            // Leaving the full player view: exit fullscreen and restore the sidebar for browsing.
            var aw = App.MainWindow?.AppWindow;
            if (aw?.Presenter.Kind == AppWindowPresenterKind.FullScreen)
                aw.SetPresenter(AppWindowPresenterKind.Overlapped);
            ShellPage.Current?.SetChromeVisible(true);
            ShellPage.Current?.SetPaneOpen(true);

            // Leaving the player while a video plays + mini enabled -> float it in the mini player.
            // This covers both browsing away (New) and pressing Back/Esc (Back); an explicit close
            // (the PiP/mini ✕) sets _forceClose so it actually stops.
            bool goMini = (e.NavigationMode == NavigationMode.New || e.NavigationMode == NavigationMode.Back)
                          && !_forceClose
                          && Settings.Shared.MiniPlayer
                          && _currentUrl is not null && !_ended;
            _forceClose = false;
            if (goMini)
            {
                ShellPage.Current?.ShowMiniPlayer(Player, _currentTitle);
            }
            else
            {
                _autosave.Stop();
                Player.Stop();
                _currentUrl = null;
                KeepAwake.Set(false);
            }
        }

        /// <summary>Closing the mini player: stop playback and clear state.</summary>
        public void CloseFromMini()
        {
            _autosave.Stop();
            SaveProgress();
            Player.Stop();
            _currentUrl = null;
            _ended = true;
            KeepAwake.Set(false);
        }

        // ---- playback state -> UI ----
        private void OnPosition(double p)
        {
            if (_seeking) return;
            _suppress = true; SeekSlider.Value = p; _suppress = false;
            UpdateTime();

            UpdateSponsor(p);
        }

        private void UpdateTime() => TimeText.Text = $"{Fmt(Act.Position)} / {Fmt(Act.Duration)}";

        private void UpdateSponsor(double p)
        {
            if (_segments is not { Count: > 0 } || _seeking) return;

            SponsorSegment? inside = null;
            foreach (var seg in _segments)
                if (p >= seg.Start && p < seg.End - 0.5) { inside = seg; break; }

            // Once we've left the segment the user reverted into, allow auto-skip there again.
            if (_suppressSkipStart >= 0 && (inside is not SponsorSegment cur || cur.Start != _suppressSkipStart))
                _suppressSkipStart = -1;

            if (inside is SponsorSegment a)
            {
                int mode = Settings.Shared.SponsorMode(a.Category);
                if (mode == 1) // auto-skip (not revertable)
                {
                    Act.SeekAbsolute(a.End);
                    ShowToast($"⏭ Skipped {a.Label}");
                    ClearManualPrompt();
                }
                else if (mode == 2 && a.Start != _suppressSkipStart
                         && (_manualSeg is null || _manualSeg.Value.Start != a.Start)) // manual
                {
                    _manualSeg = a;
                    SkipPromptText.Text = $"Skip {a.Label}? (Enter)";
                    SkipPrompt.Visibility = Visibility.Visible;
                }
            }
            else
            {
                ClearManualPrompt();
            }
        }

        private void ClearManualPrompt()
        {
            if (_manualSeg is null) return;
            _manualSeg = null;
            SkipPrompt.Visibility = Visibility.Collapsed;
        }

        private void OnSeekAreaSizeChanged(object sender, SizeChangedEventArgs e) { RedrawSegments(); RedrawChapters(); }

        /// <summary>Title of the chapter containing <paramref name="t"/> (the last chapter that starts
        /// at or before t), or "" if there are no chapters.</summary>
        private string ChapterTitleAt(double t)
        {
            string title = "";
            foreach (var ch in _chapters)
            {
                if (ch.Start <= t + 0.001) title = ch.Title;
                else break;
            }
            return title;
        }

        // ---- chapters ----
        private void OnChapters(List<Chapter> chapters)
        {
            _chapters = chapters;
            ChaptersButton.Visibility = chapters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            BuildChapterMenu();
            RedrawChapters();
        }

        private void BuildChapterMenu()
        {
            if (_chapters.Count == 0) { ChaptersButton.Flyout = null; return; }
            var menu = new MenuFlyout();
            foreach (var ch in _chapters)
            {
                double start = ch.Start;
                var item = new MenuFlyoutItem { Text = $"{Fmt(start)}   {ch.Title}" };
                item.Click += (_, __) => Act.SeekAbsolute(start);
                menu.Items.Add(item);
            }
            ChaptersButton.Flyout = menu;
        }

        private void RedrawChapters()
        {
            ChapterCanvas.Children.Clear();
            double dur = Player.Duration;
            if (_chapters.Count == 0 || dur <= 0) return;
            double w = ChapterCanvas.ActualWidth, h = ChapterCanvas.ActualHeight;
            if (w <= 0) return;
            var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            foreach (var ch in _chapters)
            {
                if (ch.Start <= 0) continue;   // no marker for the opening chapter
                double x = Math.Clamp(ch.Start / dur, 0, 1) * w;
                var tick = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 2, Height = h, Fill = brush };
                Canvas.SetLeft(tick, x);
                ChapterCanvas.Children.Add(tick);
            }
        }

        // ---- storyboard hover preview ----
        private async void BeginMediaInfo()
        {
            _storyboard = null;
            _spriteCache.Clear();
            HidePreview();
            _storyboardCts?.Cancel();
            var cts = _storyboardCts = new System.Threading.CancellationTokenSource();
            var url = _currentUrl;
            if (url is null) return;
            try
            {
                var info = await _yt.GetMediaInfoAsync(url, cts.Token);
                if (cts.IsCancellationRequested || _currentUrl != url || info is null) return;

                // Backfill views + upload date for this video wherever it's listed (history, playlists).
                Database.Shared.SetVideoMeta(url, info.ViewCount, info.Published);

                var sb = info.Storyboard;
                if (sb is not null)
                {
                    _storyboard = sb;
                    PreviewClip.Width = sb.TileWidth;
                    PreviewClip.Height = sb.TileHeight;
                    PreviewClip.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, sb.TileWidth, sb.TileHeight) };
                    // Size the Image to the FULL sprite so the offset can reveal any tile.
                    PreviewSprite.Width = sb.Columns * sb.TileWidth;
                    PreviewSprite.Height = sb.Rows * sb.TileHeight;
                }

                // A pasted URL carries no SearchResult metadata — fill the channel, fall back for
                // the title, and record it in history from yt-dlp's dump.
                if (_fromUrl)
                {
                    // Keep the channel for the info panel + "Open channel" menu, but don't show it
                    // under the title — search-launched videos don't, so this stays consistent.
                    if (_currentChannel.Length == 0 && info.Channel.Length > 0)
                    {
                        _currentChannel = info.Channel;
                        _currentChannelUrl = info.ChannelUrl;
                    }
                    if (_currentTitle.Length == 0 && info.Title.Length > 0)
                    {
                        _currentTitle = info.Title;
                        TitleText.Text = info.Title;
                    }
                    if (Settings.Shared.RememberHistory)
                    {
                        string thumb = _currentVideoId.Length == 11
                            ? $"https://i.ytimg.com/vi/{_currentVideoId}/hqdefault.jpg"
                            : info.ThumbnailUrl;
                        Database.Shared.AddHistory(new SearchResult
                        {
                            Id = _currentVideoId,
                            Url = url,
                            Title = _currentTitle.Length > 0 ? _currentTitle : info.Title,
                            Channel = info.Channel,
                            ChannelUrl = info.ChannelUrl,
                            ThumbnailUrl = thumb,
                            DurationSeconds = info.Duration > 0 ? info.Duration : (long)Player.Duration,
                            ViewCount = info.ViewCount,
                            Published = info.Published,
                            Kind = ResultKind.Video,
                        });
                    }
                }
            }
            catch (OperationCanceledException) { /* superseded */ }
        }

        private void OnSeekHover(object sender, PointerRoutedEventArgs e)
        {
            if (_storyboard is null || Player.Duration <= 0) { HidePreview(); return; }
            double w = SeekArea.ActualWidth;
            if (w <= 0) return;
            double x = e.GetCurrentPoint(SeekArea).Position.X;
            double frac = Math.Clamp(x / w, 0, 1);
            ShowPreview(frac * Player.Duration, x, w);
        }

        private void OnSeekHoverExit(object sender, PointerRoutedEventArgs e) => HidePreview();

        private void ShowPreview(double t, double x, double w)
        {
            var tile = _storyboard!.TileAt(t);
            if (tile is null) { HidePreview(); return; }
            var (url, row, col) = tile.Value;

            Canvas.SetLeft(PreviewSprite, -col * _storyboard.TileWidth);
            Canvas.SetTop(PreviewSprite, -row * _storyboard.TileHeight);
            PreviewTime.Text = Fmt(t);

            string chapter = ChapterTitleAt(t);
            PreviewChapter.Text = chapter;
            PreviewChapter.Visibility = chapter.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            double popupW = _storyboard.TileWidth + 8;   // tile + padding/border
            PreviewPopup.HorizontalOffset = Math.Clamp(x - popupW / 2, 0, Math.Max(0, w - popupW));
            PreviewPopup.VerticalOffset = -(_storyboard.TileHeight + 30);
            PreviewPopup.IsOpen = true;

            if (_previewUrl == url) return;   // same sprite already shown/loading
            _previewUrl = url;
            if (_spriteCache.TryGetValue(url, out var bmp)) PreviewSprite.Source = bmp;
            else { PreviewSprite.Source = null; _ = LoadSprite(url); }
        }

        // Fetch the sprite ourselves (BitmapImage's own loader fails on these signed sb/ URLs)
        // and decode it from the byte stream.
        private async System.Threading.Tasks.Task LoadSprite(string url)
        {
            if (_spriteCache.ContainsKey(url)) return;
            try
            {
                var bytes = await _http.GetByteArrayAsync(url);
                var image = new BitmapImage();
                using var ms = new MemoryStream(bytes);
                await image.SetSourceAsync(ms.AsRandomAccessStream());
                _spriteCache[url] = image;
                if (_previewUrl == url) PreviewSprite.Source = image;   // still on this tile
            }
            catch { /* preview unavailable for this sprite */ }
        }

        private void HidePreview()
        {
            if (PreviewPopup is not null) PreviewPopup.IsOpen = false;
            _previewUrl = "";
        }

        private void RedrawSegments()
        {
            SegmentCanvas.Children.Clear();
            double dur = Player.Duration;
            if (_segments is not { Count: > 0 } || dur <= 0) return;
            double w = SegmentCanvas.ActualWidth, h = SegmentCanvas.ActualHeight;
            if (w <= 0) return;
            const double bandH = 4;
            double top = (h - bandH) / 2;
            foreach (var seg in _segments)
            {
                if (Settings.Shared.SponsorMode(seg.Category) == 0) continue;
                double x = Math.Clamp(seg.Start / dur, 0, 1) * w;
                double bw = Math.Max(2, Math.Clamp((seg.End - seg.Start) / dur, 0, 1) * w);
                var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = bw, Height = bandH, RadiusX = 2, RadiusY = 2,
                    Fill = BrushFromHex(SponsorBlock.ColorFor(seg.Category)),
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, top);
                SegmentCanvas.Children.Add(rect);
            }
        }

        private static SolidColorBrush BrushFromHex(string hex)
        {
            hex = hex.TrimStart('#');
            byte a = System.Convert.ToByte(hex.Substring(0, 2), 16);
            byte r = System.Convert.ToByte(hex.Substring(2, 2), 16);
            byte g = System.Convert.ToByte(hex.Substring(4, 2), 16);
            byte b = System.Convert.ToByte(hex.Substring(6, 2), 16);
            return new SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
        }

        private void OnDuration(double d)
        {
            SeekSlider.Maximum = d > 0 ? d : 1;
            UpdateTime();
            RedrawSegments();
            RedrawChapters();
        }

        private void OnPaused(bool paused)
        {
            PlayPauseIcon.Glyph = Glyph(paused ? "GlyphPlay" : "GlyphPause");
            // Keep the screen/system awake only while actually playing a video.
            KeepAwake.Set(!paused && _currentUrl is not null);
            // Keep controls up while paused; resume the idle hide-timer when playing.
            if (paused) { SetControlsVisible(true); _hideControls.Stop(); }
            else RestartHideTimer();
        }
        private void OnMuted(bool muted) => MuteIcon.Glyph = Glyph(muted ? "GlyphMute" : "GlyphVolume");

        // ---- transport controls ----
        private void OnPlayPauseClick(object sender, RoutedEventArgs e) => Act.TogglePause();
        private void OnPrevClick(object sender, RoutedEventArgs e) => PlayPrev();
        private void OnNextClick(object sender, RoutedEventArgs e) => PlayNext();
        private void OnMuteClick(object sender, RoutedEventArgs e) => Act.ToggleMute();
        private void OnVolumeValueChanged(object sender, RangeBaseValueChangedEventArgs e) => Act.SetVolume(e.NewValue);

        private void OnSeekValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) return;
            if (_seeking) TimeText.Text = $"{Fmt(e.NewValue)} / {Fmt(Act.Duration)}";
        }

        private void OnSeekPressed(object sender, PointerRoutedEventArgs e)
        {
            _seeking = true;
            double x = e.GetCurrentPoint(SeekSlider).Position.X;
            double frac = SeekSlider.ActualWidth > 0 ? Math.Clamp(x / SeekSlider.ActualWidth, 0, 1) : 0;
            _suppress = true; SeekSlider.Value = frac * SeekSlider.Maximum; _suppress = false;
            TimeText.Text = $"{Fmt(SeekSlider.Value)} / {Fmt(Act.Duration)}";
        }

        private void OnSeekReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_seeking) return;
            _seeking = false;
            Act.SeekAbsolute(SeekSlider.Value);
        }

        private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

        private void ToggleFullscreen()
        {
            var aw = App.MainWindow?.AppWindow;
            if (aw is null) return;
            if (aw.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            {
                aw.SetPresenter(AppWindowPresenterKind.Overlapped);
                FullscreenIcon.Glyph = Glyph("GlyphFullscreen");
                ShellPage.Current?.SetChromeVisible(true);   // restore sidebar...
                ShellPage.Current?.SetPaneOpen(false);       // ...but keep it collapsed while watching
            }
            else
            {
                aw.SetPresenter(AppWindowPresenterKind.FullScreen);
                FullscreenIcon.Glyph = Glyph("GlyphBackToWindow");
                ShellPage.Current?.SetChromeVisible(false);  // hide sidebar so video fills the screen
            }
            SetControlsVisible(true);
            RestartHideTimer();
        }

        private bool IsFullscreen =>
            App.MainWindow?.AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

        // ---- picture-in-picture (separate floating window with its own mpv instance) ----
        private PipWindow? _pipWindow;
        private bool _suppressPipReturn;   // closing PiP because we're navigating away, not returning

        /// <summary>The player the transport controls currently drive: the PiP window's instance while
        /// in PiP (so its controls work like the Linux version), else the main embedded player.</summary>
        private FreeFlume.Player.MpvPlayer Act => (_pip && _pipWindow is not null) ? _pipWindow.Player : Player;

        private void OnPipClick(object sender, RoutedEventArgs e) => TogglePip();

        private void TogglePip()
        {
            if (_pip) ExitPip(); else EnterPip();
        }

        private void EnterPip()
        {
            if (_pip || _currentUrl is null) return;

            // Leave fullscreen first.
            var aw = App.MainWindow?.AppWindow;
            if (aw?.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            {
                aw.SetPresenter(AppWindowPresenterKind.Overlapped);
                FullscreenIcon.Glyph = Glyph("GlyphFullscreen");
                ShellPage.Current?.SetChromeVisible(true);
                ShellPage.Current?.SetPaneOpen(false);
            }

            double pos = Player.Position;
            double aspect = Player.VideoAspect;
            string url = _currentUrl;
            SaveProgress();

            _pip = true;
            Player.SetPaused(true);     // pause (no audio) but keep the main loaded for an instant return
            _pipWindow = new PipWindow(aspect);
            _pipWindow.Returned += OnPipReturned;
            // Drive the PiP video from the main transport bar: mirror its state onto our UI.
            var pp = _pipWindow.Player;
            pp.PositionChanged += OnPosition;
            pp.DurationChanged += OnDuration;
            pp.PausedChanged += OnPaused;
            pp.MutedChanged += OnMuted;
            _pipWindow.Activate();
            // Carry the current subtitle selection into PiP so captions stay in sync (§8).
            string subSel = (_subsOn && _preferredSid > 0) ? _preferredSid.ToString() : "no";
            _pipWindow.PlayPip(url, pos, _currentTitle, subSel);

            PipBanner.Visibility = Visibility.Visible;
            _hideControls.Stop();
            SetControlsVisible(true);   // keep the transport bar up so it can control the PiP video
        }

        /// <summary>Close the PiP window — which resumes playback in the main player (see OnPipReturned).</summary>
        private void ExitPip() => _pipWindow?.Close();

        private void OnReturnFromPip(object sender, RoutedEventArgs e) => ExitPip();

        // Raised (UI thread) when the PiP window closes; resume the main player at its position.
        private void OnPipReturned(double pos)
        {
            if (_pipWindow is not null)
            {
                _pipWindow.Returned -= OnPipReturned;
                var pp = _pipWindow.Player;
                pp.PositionChanged -= OnPosition;
                pp.DurationChanged -= OnDuration;
                pp.PausedChanged -= OnPaused;
                pp.MutedChanged -= OnMuted;
                _pipWindow = null;
            }
            _pip = false;
            PipBanner.Visibility = Visibility.Collapsed;
            if (_suppressPipReturn) { _suppressPipReturn = false; return; }   // navigated away — don't resume
            // Main player was only paused, so resuming is just a seek to the PiP position + unpause.
            Player.SeekAbsolute(pos);
            Player.SetPaused(false);
            SetControlsVisible(true);
            RestartHideTimer();
        }

        private void SetControlsVisible(bool visible)
        {
            if (_pip)   // in PiP: placard fills the video, but keep the transport bar up to drive the PiP video
            {
                TransportBar.Visibility = Visibility.Visible;
                TopBar.Visibility = Visibility.Collapsed;
                return;
            }
            TransportBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            // The top bar (title) hides with the controls when windowed; it stays hidden in fullscreen.
            TopBar.Visibility = (visible && !IsFullscreen) ? Visibility.Visible : Visibility.Collapsed;

            // Hide the mouse cursor along with the controls in fullscreen; any mouse move reveals both
            // (a pointer move calls SetControlsVisible(true), which shows it again).
            Player.SetCursorVisible(visible || !IsFullscreen);
        }

        private void RestartHideTimer() { _hideControls.Stop(); _hideControls.Start(); }

        private void OnPlayerPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            SetControlsVisible(true);
            RestartHideTimer();
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        // ---- screenshot ----
        private void OnScreenshotClick(object sender, RoutedEventArgs e) => TakeScreenshot();

        private long _lastShotTick;
        private string? _lastShotPath;

        private async void TakeScreenshot()
        {
            if (_currentUrl is null || Player.Duration <= 0) return;
            // Debounce: collapse rapid repeats so one press saves exactly one screenshot.
            long now = Environment.TickCount64;
            if (now - _lastShotTick < 700) return;
            _lastShotTick = now;

            var (path, _) = BuildScreenshotTarget();
            bool ok = await Player.ScreenshotNativeAsync(path, 95);
            ShowToast(ok ? $"\U0001F4F8 Saved {System.IO.Path.GetFileName(path)} — tap to open" : "Screenshot failed");
            _lastShotPath = ok ? path : null;   // set after ShowToast (which clears it)
        }

        private void OnToastTapped(object sender, TappedRoutedEventArgs e)
        {
            if (_revertPos is double rp) { DoRevert(rp); return; }   // tap the skip toast to undo it
            if (_lastShotPath is null || !System.IO.File.Exists(_lastShotPath)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"/select,\"{_lastShotPath}\"") { UseShellExecute = true });
            }
            catch { }
        }

        private (string path, double jxlDistance) BuildScreenshotTarget()
        {
            var s = Settings.Shared;
            string root = string.IsNullOrWhiteSpace(s.ScreenshotFolder)
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "FreeFlume")
                : s.ScreenshotFolder;

            string baseName = Sanitize(_currentTitle.Length > 0 ? _currentTitle : "video");
            if (_currentVideoId.Length > 0) baseName += "_" + _currentVideoId;

            string dir = System.IO.Path.Combine(root, baseName);
            System.IO.Directory.CreateDirectory(dir);

            var (ext, dist) = s.ScreenshotFormat switch
            {
                "jpg" => ("jpg", 0.0),
                _ => ("png", 0.0),
            };

            string ts = Fmt(Act.Position).Replace(':', '-');   // playback position, e.g. 1-23
            int n = 1; string path;
            do { path = System.IO.Path.Combine(dir, $"{baseName}_{ts}_{n:000}.{ext}"); n++; }
            while (System.IO.File.Exists(path));
            return (path, dist);
        }

        private static string Sanitize(string s)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            s = s.Trim();
            if (s.Length > 80) s = s[..80].Trim();
            return s.Length == 0 ? "video" : s;
        }

        /// <summary>Handle a shortcut by Win32 virtual-key code (from the low-level keyboard hook).
        /// Runs on the UI thread. Returns true if the key was consumed.</summary>
        public bool TryHandleVk(int vk)
        {
            // ---- fixed bindings (not rebindable) ----
            if (vk == 0x0D)   // Enter: SponsorBlock manual skip
            {
                if (_manualSeg is not null) { ManualSkip(); return true; }     // manual skip — arms revert
                if (_revertPos is double rp) { DoRevert(rp); return true; }    // undo the manual skip
                return false;
            }
            if (vk == 0x1B)   // Escape: exit PiP / fullscreen / go back
            {
                if (_pip) { ExitPip(); return true; }
                if (IsFullscreen) { ToggleFullscreen(); return true; }
                if (Frame.CanGoBack) { Frame.GoBack(); return true; }
                return false;
            }
            if ((vk == 0x25 || vk == 0x27) && ShiftDown())   // Shift+arrows: frame step
            {
                if (vk == 0x25) Act.FrameBackStep(); else Act.FrameStep();
                return true;
            }
            if (vk is 0xBB or 0x6B) { VolumeSlider.Value = Math.Min(130, VolumeSlider.Value + 5); return true; }  // +/= , numpad+
            if (vk is 0xBD or 0x6D) { VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5); return true; }    // -  , numpad-

            // ---- rebindable bindings ----
            string? action = ShortcutFor(vk);
            if (action is null) return false;
            RunShortcut(action);
            return true;
        }

        private static string? ShortcutFor(int vk)
        {
            foreach (var s in Models.Shortcuts.All)
                if (Settings.Shared.ShortcutVk(s.Id) == vk) return s.Id;
            return null;
        }

        private void RunShortcut(string id)
        {
            switch (id)
            {
                case "PlayPause": Act.TogglePause(); break;
                case "SeekBackward": Act.SeekRelative(-5); break;
                case "SeekForward": Act.SeekRelative(5); break;
                case "VolumeUp": VolumeSlider.Value = Math.Min(130, VolumeSlider.Value + 5); break;
                case "VolumeDown": VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5); break;
                case "Mute": Act.ToggleMute(); break;
                case "Captions": ToggleSubs(); break;
                case "Fullscreen": ToggleFullscreen(); break;
                case "Loop": ToggleLoop(); break;
                case "PreviousVideo": PlayPrev(); break;
                case "NextVideo": PlayNext(); break;
                case "PreviousFrame": Act.FrameBackStep(); break;
                case "NextFrame": Act.FrameStep(); break;
                // Run on the dispatcher: from the keyboard hook there's no SynchronizationContext, so
                // the async screenshot's await continuations would otherwise land off the UI thread.
                case "Screenshot": DispatcherQueue.TryEnqueue(() => TakeScreenshot()); break;
                case "Info": OnInfoClick(this, new RoutedEventArgs()); break;
                case "Queue": ToggleQueuePanel(); break;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private static bool ShiftDown() => (GetAsyncKeyState(0x10) & 0x8000) != 0;   // VK_SHIFT

        private static string Fmt(double seconds)
        {
            if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }
    }
}
