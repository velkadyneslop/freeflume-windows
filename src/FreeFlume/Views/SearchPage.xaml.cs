using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using FreeFlume.Models;
using FreeFlume.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace FreeFlume.Views
{
    /// <summary>Search YT (with filters + pagination), drill into channels/playlists, click to play.</summary>
    public sealed partial class SearchPage : Page
    {
        private static int PageSize => Math.Clamp(Settings.Shared.SearchLimit, 5, 100);
        private enum Mode { Search, Browse }

        private readonly YtDlpService _yt = new();
        private readonly ObservableCollection<SearchResult> _results = new();

        private Mode _mode = Mode.Search;
        private string _query = "";
        private SearchFilters _filters = new();
        private int _page = 1;
        private int _savedSearchPage = 1;
        private string _browseUrl = "";
        private string _channelBase = "";   // base channel URL when drilled into a channel (for in-channel search)
        private bool _channelStreams;       // false = Videos tab, true = Streams tab (channel view only)
        private bool _inChannelSearch;      // true while showing in-channel search results (tabs hidden)

        /// <summary>Set by the player's channel link: browse this channel on next navigation here.</summary>
        public static (string url, string title)? PendingBrowse;
        private bool _ready;
        private bool _searching;

        // Opt-in live YT search suggestions: debounce keystrokes, then fetch + merge with history.
        private readonly DispatcherTimer _suggestTimer;
        private string _suggestText = "";
        private System.Threading.CancellationTokenSource? _suggestCts;

        // Remembered pages for this session: returning to any loaded page is instant (no re-fetch).
        // `full` = yt-dlp returned a full raw page, so a next page very likely exists.
        private readonly Dictionary<string, (List<SearchResult> items, bool full)> _pageCache = new();
        private readonly Dictionary<string, int> _contextTotalPages = new();   // total pages per context (playlists)
        private readonly Dictionary<string, int> _maxPageByContext = new();    // furthest page reached (open-ended "of N")
        private int _totalPages;        // pages in the current context (0 = open-ended, e.g. search)
        private bool _suppressPageBox;  // ignore programmatic PageBox value changes

        public SearchPage()
        {
            this.InitializeComponent();
            NavigationCacheMode = NavigationCacheMode.Enabled;
            ResultsView.ItemsSource = _results;
            ResultsView.ContextRequested += OnResultsContextRequested;
            _suggestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
            _suggestTimer.Tick += OnSuggestTimerTick;
            _ready = true;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (PendingBrowse is { } b) { PendingBrowse = null; BrowseChannel(b.url, b.title); }
        }

        private void BrowseChannel(string url, string title)
        {
            _mode = Mode.Browse;
            _browseUrl = url;
            _channelBase = url.TrimEnd('/');
            _channelStreams = false;
            _inChannelSearch = false;
            _page = 1;
            ContextTitle.Text = title;
            ContextBar.Visibility = Visibility.Visible;
            FilterBar.Visibility = Visibility.Collapsed;
            ChannelSearchBox.Text = "";
            ChannelSearchBox.Visibility = Visibility.Visible;
            UpdateChannelTabs();
            _ = DoLoad();
        }

        // ---- search entry points ----
        private void OnQueryTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            string text = sender.Text.Trim();

            // Live YT suggestions (opt-in): show history instantly, then merge fetched suggestions (debounced).
            if (Settings.Shared.EnableSearchSuggestions && text.Length > 0)
            {
                sender.ItemsSource = HistoryMatches(text);
                _suggestText = text;
                _suggestTimer.Stop();
                _suggestTimer.Start();
                return;
            }

            _suggestTimer.Stop();
            if (!Settings.Shared.RememberSearch) { sender.ItemsSource = null; return; }
            sender.ItemsSource = HistoryMatches(text);
        }

        private static List<string> HistoryMatches(string text)
        {
            var all = Database.Shared.SearchHistory();
            return text.Length == 0 ? all
                : all.Where(q => q.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Debounced fetch: GET YT suggestions for the typed text, then list them above a few history matches.
        private async void OnSuggestTimerTick(object? sender, object e)
        {
            _suggestTimer.Stop();
            string text = _suggestText;
            if (text.Length == 0) return;

            _suggestCts?.Cancel();
            var cts = _suggestCts = new System.Threading.CancellationTokenSource();
            List<string> sugg;
            try { sugg = await SearchSuggest.FetchAsync(text, cts.Token); }
            catch { return; }
            if (cts.IsCancellationRequested || sugg.Count == 0) return;
            if (QueryBox.Text.Trim() != text) return;   // user typed on — this result is stale

            var merged = new List<string>(sugg);
            foreach (var h in HistoryMatches(text))
            {
                if (merged.Count >= sugg.Count + 3) break;
                if (!merged.Contains(h, StringComparer.OrdinalIgnoreCase)) merged.Add(h);
            }
            QueryBox.ItemsSource = merged;
        }

        private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            if (args.ChosenSuggestion is string s) sender.Text = s;
            StartSearch();
        }

        private void OnQueryGotFocus(object sender, RoutedEventArgs e)
        {
            if (!Settings.Shared.RememberSearch || sender is not AutoSuggestBox box) return;
            if (box.Text.Trim().Length == 0)
            {
                box.ItemsSource = Database.Shared.SearchHistory();
                box.IsSuggestionListOpen = true;
            }
        }

        private void OnSearchClick(object sender, RoutedEventArgs e) => StartSearch();

        private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_ready && _mode == Mode.Search && QueryBox.Text.Trim().Length > 0) StartSearch();
        }

        private void StartSearch()
        {
            // A pasted YouTube link opens the video directly instead of running a text search.
            if (TryGetYouTubeVideoUrl(QueryBox.Text, out var videoUrl))
            {
                QueryBox.IsSuggestionListOpen = false;
                Frame.Navigate(typeof(PlayerPage), videoUrl);
                return;
            }

            _mode = Mode.Search;
            _query = QueryBox.Text.Trim();
            QueryBox.IsSuggestionListOpen = false;
            if (Settings.Shared.RememberSearch && _query.Length > 0) Database.Shared.AddSearch(_query);
            _filters = ReadFilters();
            _page = 1;
            ContextBar.Visibility = Visibility.Collapsed;
            FilterBar.Visibility = Visibility.Visible;
            _ = DoLoad();
        }

        private SearchFilters ReadFilters() => new()
        {
            Sort = TagInt(SortCombo),
            UploadDate = TagInt(DateCombo),
            Type = TagInt(TypeCombo),
            Duration = TagInt(DurationCombo),
            Hd = HdCheck.IsChecked == true,
            FourK = FourKCheck.IsChecked == true,
            Subtitles = SubsCheck.IsChecked == true,
            Live = LiveCheck.IsChecked == true,
        };

        private void OnFeatureChanged(object sender, RoutedEventArgs e)
        {
            if (_ready && _mode == Mode.Search && QueryBox.Text.Trim().Length > 0) StartSearch();
        }

        /// <summary>Run a query programmatically (e.g. from a command-line search argument).</summary>
        public void RunQuery(string query)
        {
            QueryBox.Text = query;
            StartSearch();
        }

        private static int TagInt(ComboBox c) =>
            int.TryParse((c.SelectedItem as ComboBoxItem)?.Tag as string, out var v) ? v : 0;

        // Matches a YouTube video id inside any common link form (watch / youtu.be / shorts / embed / live).
        private static readonly System.Text.RegularExpressions.Regex YouTubeUrl = new(
            @"(?:youtube\.com/(?:watch\?(?:[^ ]*&)?v=|shorts/|embed/|live/|v/)|youtu\.be/)([\w-]{11})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>True if the text is a YouTube link; yields a canonical watch URL for the player.</summary>
        private static bool TryGetYouTubeVideoUrl(string text, out string url)
        {
            url = "";
            var t = text.Trim();
            if (t.IndexOf("youtube.com", StringComparison.OrdinalIgnoreCase) < 0 &&
                t.IndexOf("youtu.be", StringComparison.OrdinalIgnoreCase) < 0) return false;
            var m = YouTubeUrl.Match(t);
            if (!m.Success) return false;
            url = "https://www.youtube.com/watch?v=" + m.Groups[1].Value;
            return true;
        }

        // ---- the loader (search or browse, current page) ----
        // Identifies the current result set so cached pages from different queries/playlists don't collide.
        private string ContextKey() => _mode == Mode.Browse
            ? "B|" + _browseUrl
            : $"S|{_query}|{_filters.Sort},{_filters.UploadDate},{_filters.Type},{_filters.Duration}," +
              $"{(_filters.Hd ? 1 : 0)}{(_filters.FourK ? 1 : 0)}{(_filters.Subtitles ? 1 : 0)}{(_filters.Live ? 1 : 0)}";

        private async Task DoLoad()
        {
            if (_searching) return;
            if (_mode == Mode.Search && _query.Length == 0) return;

            string ckBase = ContextKey();
            _totalPages = _contextTotalPages.GetValueOrDefault(ckBase);
            string key = ckBase + "#" + _page;

            // Cached page -> show instantly. This is what makes going back (and re-jumping) fast.
            if (_pageCache.TryGetValue(key, out var cached)) { ShowResults(cached.items, cached.full); return; }

            _results.Clear();   // cache miss: blank the list while the spinner loads the page
            _searching = true;
            SearchButton.IsEnabled = false;
            DetailPane.Visibility = Visibility.Collapsed;
            Status.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = true;
            try
            {
                List<SearchResult> hits;
                int raw;
                if (_mode == Mode.Browse)
                {
                    var (items, total, rawCount) = await _yt.BrowseAsync(_browseUrl, _page, PageSize);
                    hits = items; raw = rawCount;
                    if (total > 0)
                    {
                        _totalPages = (int)Math.Ceiling(total / (double)PageSize);
                        _contextTotalPages[ckBase] = _totalPages;
                    }
                }
                else
                {
                    var (items, rawCount) = await _yt.SearchAsync(_query, _filters, _page, PageSize);
                    hits = items; raw = rawCount;
                }
                bool full = raw >= PageSize;
                _pageCache[key] = (hits, full);
                ShowResults(hits, full);
            }
            catch (Exception ex)
            {
                Status.Text = "Search failed: " + ex.Message;
                Status.Visibility = Visibility.Visible;
                Pager.Visibility = Visibility.Collapsed;
            }
            finally
            {
                LoadingRing.IsActive = false;
                SearchButton.IsEnabled = true;
                _searching = false;
            }
        }

        private void ShowResults(List<SearchResult> hits, bool pageFull)
        {
            // In search mode, honor the include-channels/playlists toggles (drill-in browsing shows everything).
            var shown = _mode != Mode.Search ? hits : hits.Where(r =>
                (r.Kind != ResultKind.Channel || Settings.Shared.SearchIncludeChannels) &&
                (r.Kind != ResultKind.Playlist || Settings.Shared.SearchIncludePlaylists)).ToList();

            // On the Videos tab of a channel, float currently-live streams to the top (the Streams tab is
            // shown as YouTube returns it — live/upcoming already first — so it isn't re-ordered or pinned).
            if (_channelBase.Length > 0 && !_channelStreams && shown.Exists(r => r.IsLive))
                shown = shown.OrderByDescending(r => r.IsLive).ToList();

            Database.Shared.FillWatchProgress(shown);   // refresh watched bars (may have changed since cached)
            Database.Shared.FillVideoMeta(shown);       // apply any cached views + upload dates instantly
            _results.Clear();
            foreach (var r in shown) _results.Add(r);
            EnrichDates(shown);                         // fetch the still-missing upload dates in the background
            if (_results.Count > 0)   // remember the furthest page reached for the running "of N"
            {
                string ck = ContextKey();
                _maxPageByContext[ck] = Math.Max(_maxPageByContext.GetValueOrDefault(ck), _page);
            }
            CheckChannelsLive(shown);
            if (_channelBase.Length > 0 && !_channelStreams && _page == 1) ProbeChannelLive(_channelBase);   // surface the live stream
            UpdatePager(pageFull);
            if (_results.Count == 0)
            {
                Status.Text = _page > 1 ? "No more results." : "No results found.";
                Status.Visibility = Visibility.Visible;
            }
            else Status.Visibility = Visibility.Collapsed;
        }

        private void UpdatePager(bool pageFull)
        {
            Pager.Visibility = (_results.Count > 0 || _page > 1) ? Visibility.Visible : Visibility.Collapsed;
            bool finite = _totalPages > 0;

            _suppressPageBox = true;
            PageBox.Maximum = finite ? _totalPages : 100000;
            PageBox.Value = _page;
            _suppressPageBox = false;

            if (finite)
            {
                PageTotalText.Text = $"of {_totalPages}";
                PageTotalText.Visibility = Visibility.Visible;
            }
            else
            {
                // Open-ended: a running "of N" of the furthest page seen, with + while more remain.
                string ck = ContextKey();
                int seen = Math.Max(_maxPageByContext.GetValueOrDefault(ck), _page);
                bool more = _pageCache.TryGetValue(ck + "#" + seen, out var fp) ? fp.full : pageFull;
                PageTotalText.Text = $"of {seen}" + (more ? "+" : "");
                PageTotalText.Visibility = (_results.Count > 0 || _page > 1) ? Visibility.Visible : Visibility.Collapsed;
            }
            LastButton.Visibility = finite ? Visibility.Visible : Visibility.Collapsed;
            LastButton.IsEnabled = finite && _page < _totalPages;

            FirstButton.IsEnabled = _page > 1;
            PrevButton.IsEnabled = _page > 1;
            NextButton.IsEnabled = finite ? _page < _totalPages : pageFull;
        }

        // Light a red ring on any channel result that's currently live (background, throttled, cached).
        private void CheckChannelsLive(List<SearchResult> items)
        {
            foreach (var r in items)
            {
                if (r.Kind != ResultKind.Channel) continue;
                string url = r.Url.Length > 0 ? r.Url : r.ChannelUrl;
                if (url.Length == 0) continue;
                var item = r;
                _ = Task.Run(async () =>
                {
                    if (await LiveStatus.Shared.IsLiveAsync(url))
                        DispatcherQueue.TryEnqueue(() => item.ChannelLive = true);
                });
            }
        }

        // The flat channel listing doesn't flag the active live stream, so probe the channel's /live page
        // and pin the currently-live stream to the very top of the channel view (deduping any copy below).
        private async void ProbeChannelLive(string channelBase)
        {
            // Cheap gate: only scan the stream list if the channel is live at all.
            var primary = await LiveStatus.Shared.LiveVideoAsync(channelBase);
            if (primary is null) return;

            // The /streams tab lists live streams first; confirm each of the top entries is currently live
            // (the flat listing can't tell us). This catches channels running several streams at once.
            var live = new List<(string Id, string Title)>();
            try
            {
                var (streams, _, _) = await _yt.BrowseAsync(channelBase + "/streams", 1, 12);
                var top = streams.FindAll(s => s.Kind is ResultKind.Video or ResultKind.Short && s.Id.Length == 11);
                if (top.Count > 12) top = top.GetRange(0, 12);
                var checks = top.Select(async s => (s, ok: await LiveStatus.Shared.IsVideoLiveAsync(s.Id)));
                foreach (var (s, ok) in await Task.WhenAll(checks))
                    if (ok) live.Add((s.Id, s.Title));
            }
            catch { }

            if (!live.Exists(x => x.Id == primary.Id)) live.Insert(0, (primary.Id, primary.Title));

            DispatcherQueue.TryEnqueue(() =>
            {
                if (_channelBase != channelBase) return;   // user moved on
                var ids = new HashSet<string>(live.ConvertAll(x => x.Id));
                for (int i = _results.Count - 1; i >= 0; i--)
                    if (ids.Contains(_results[i].Id) || live.Exists(x => _results[i].Url.Contains(x.Id))) _results.RemoveAt(i);
                for (int j = live.Count - 1; j >= 0; j--)   // insert in reverse so the first live ends up on top
                    _results.Insert(0, new SearchResult
                    {
                        Id = live[j].Id,
                        Url = $"https://www.youtube.com/watch?v={live[j].Id}",
                        Title = live[j].Title,
                        Channel = ContextTitle.Text,
                        IsLive = true,
                        Kind = ResultKind.Video,
                    });
            });
        }

        // The fast search listing has no upload date, so fetch it per-video in the background (throttled
        // + cached in the DB) and fill it into the rows live. Cancelled when a new page/search loads.
        private System.Threading.CancellationTokenSource? _enrichCts;

        private void EnrichDates(IReadOnlyList<SearchResult> items)
        {
            _enrichCts?.Cancel();
            var cts = _enrichCts = new System.Threading.CancellationTokenSource();
            var todo = items.Where(r => r.Kind is ResultKind.Video or ResultKind.Short
                                        && r.Published <= 0 && r.Url.Length > 0).ToList();
            if (todo.Count == 0) return;

            _ = Task.Run(async () =>
            {
                using var gate = new System.Threading.SemaphoreSlim(4);   // cap concurrent yt-dlp lookups
                var jobs = todo.Select(async r =>
                {
                    try
                    {
                        await gate.WaitAsync(cts.Token);
                        try
                        {
                            var info = await _yt.GetMediaInfoAsync(r.Url, cts.Token);
                            if (info is null || cts.IsCancellationRequested) return;
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                if (info.Published > 0) r.Published = info.Published;
                                if (r.ViewCount < 0 && info.ViewCount >= 0) r.ViewCount = info.ViewCount;
                            });
                            try { Database.Shared.SetVideoMeta(r.Url, info.ViewCount, info.Published); } catch { }
                        }
                        finally { gate.Release(); }
                    }
                    catch { /* cancelled or fetch failed — leave the date blank */ }
                });
                try { await Task.WhenAll(jobs); } catch { }
            });
        }

        // ---- pagination ----
        private void OnFirstClick(object sender, RoutedEventArgs e) { if (_page != 1) { _page = 1; _ = DoLoad(); } }
        private void OnPrevClick(object sender, RoutedEventArgs e) { if (_page > 1) { _page--; _ = DoLoad(); } }
        private void OnNextClick(object sender, RoutedEventArgs e) { _page++; _ = DoLoad(); }
        private void OnLastClick(object sender, RoutedEventArgs e) { if (_totalPages > 0 && _page != _totalPages) { _page = _totalPages; _ = DoLoad(); } }

        private void OnPageBoxValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_suppressPageBox || _searching) return;
            if (double.IsNaN(args.NewValue)) { sender.Value = _page; return; }
            int target = Math.Max(1, (int)args.NewValue);
            if (_totalPages > 0) target = Math.Min(target, _totalPages);
            if (target == _page) return;
            _page = target;
            _ = DoLoad();
        }

        // ---- result actions ----
        // Single click selects + shows the detail pane; double click (or the pane's button) opens.
        private SearchResult? _selected;
        private System.Threading.CancellationTokenSource? _detailCts;

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selected = ResultsView.SelectedItem as SearchResult;
            if (_selected is null) { DetailPane.Visibility = Visibility.Collapsed; return; }
            ShowDetail(_selected);
        }

        private void OnResultDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Open(_selected);

        private void OnDetailPlay(object sender, RoutedEventArgs e) => Open(_selected);

        private void Open(SearchResult? r)
        {
            if (r is null) return;
            if (r.Kind is ResultKind.Video or ResultKind.Short)
            {
                // Hand the player the surrounding playable results as an autoplay queue.
                var items = new List<SearchResult>();
                foreach (var x in _results)
                    if (x.Kind is ResultKind.Video or ResultKind.Short) items.Add(x);
                int idx = items.FindIndex(x => x.Url == r.Url);
                if (idx >= 0) PlayerPage.PendingQueue = (items, idx);
                // If we're inside a playlist, queue the whole playlist (fetched in the background).
                bool isPlaylist = _mode == Mode.Browse &&
                                  (_browseUrl.Contains("list=") || _browseUrl.Contains("/playlist"));
                PlayerPage.PendingQueueSourceUrl = isPlaylist ? _browseUrl : null;
                Frame.Navigate(typeof(PlayerPage), r);
            }
            else DrillInto(r);
        }

        private async void ShowDetail(SearchResult r)
        {
            // Fill instantly from what the search already gave us, then enrich async.
            DetailTitle.Text = r.Title;
            DetailDesc.Text = "";

            bool isVideo = r.Kind is ResultKind.Video or ResultKind.Short;
            bool isChannel = r.Kind == ResultKind.Channel;

            // Channels get a square avatar; videos/playlists get the 16:9 thumbnail.
            DetailThumbBox.Visibility = isChannel ? Visibility.Collapsed : Visibility.Visible;
            DetailAvatarBox.Visibility = isChannel ? Visibility.Visible : Visibility.Collapsed;
            if (isChannel) DetailAvatar.Source = r.Thumbnail; else DetailThumb.Source = r.Thumbnail;

            // Clickable uploader link for videos; hidden for channels (it would duplicate the title).
            DetailChannel.Content = r.Channel;
            DetailChannel.Visibility = (!isChannel && r.Channel.Length > 0) ? Visibility.Visible : Visibility.Collapsed;

            DetailPlayText.Text = isVideo ? "Play" : isChannel ? "Open channel" : "Open playlist";
            DetailPlayIcon.Glyph = ((char)(isVideo ? 0xE768 : 0xE76C)).ToString();   // Play / ChevronRight
            //x"" : "";   // glyphs set below
            DetailStats.Text = StatsLine(r.ViewCount, -1, r.DurationSeconds, "");
            UpdateSubscribeButton();
            DetailPane.Visibility = Visibility.Visible;

            _detailCts?.Cancel();
            if (!isVideo) return;   // only videos have a rich metadata page

            var cts = _detailCts = new System.Threading.CancellationTokenSource();
            try
            {
                var d = await _yt.GetDetailsAsync(r.Url, cts.Token);
                if (cts.IsCancellationRequested || _selected != r || d is null) return;
                DetailStats.Text = StatsLine(
                    d.ViewCount >= 0 ? d.ViewCount : r.ViewCount,
                    d.LikeCount,
                    d.DurationSeconds > 0 ? d.DurationSeconds : r.DurationSeconds,
                    d.UploadDate);
                if (d.Description.Length > 0)
                    // Timestamps play the video from that point; URLs open in the browser.
                    DescriptionInlines.Fill(DetailDesc, d.Description, secs => PlayAt(r, secs));
                if (d.Channel.Length > 0) DetailChannel.Content = d.Channel;
            }
            catch (OperationCanceledException) { /* superseded by a newer selection */ }
        }

        private void OnDetailChannelClick(object sender, RoutedEventArgs e)
        {
            if (_selected is null) return;
            string url = _selected.ChannelUrl.Length > 0 ? _selected.ChannelUrl
                       : (_selected.ChannelId.StartsWith("UC") ? "https://www.youtube.com/channel/" + _selected.ChannelId : "");
            if (url.Length == 0) return;
            BrowseChannel(url, _selected.Channel.Length > 0 ? _selected.Channel : _selected.Title);
        }

        private void UpdateSubscribeButton()
        {
            bool isChannel = _selected?.Kind == ResultKind.Channel;
            DetailSubscribeButton.Visibility = isChannel ? Visibility.Visible : Visibility.Collapsed;
            if (isChannel && _selected is not null)
                DetailSubscribeText.Text = Database.Shared.IsSubscribed(_selected.Url) ? "Unsubscribe" : "Subscribe";
        }

        private void OnDetailSubscribe(object sender, RoutedEventArgs e)
        {
            if (_selected is not { Kind: ResultKind.Channel } r) return;
            if (Database.Shared.IsSubscribed(r.Url)) Database.Shared.Unsubscribe(r.Url);
            else
            {
                string cid = r.ChannelId.StartsWith("UC") ? r.ChannelId : (r.Id.StartsWith("UC") ? r.Id : "");
                Database.Shared.Subscribe(r.Title.Length > 0 ? r.Title : r.Channel, r.Url, cid, r.ThumbnailUrl);
            }
            UpdateSubscribeButton();
        }

        private void PlayAt(SearchResult r, double secs)
        {
            if (r.Kind is not (ResultKind.Video or ResultKind.Short)) return;
            PlayerPage.PendingStart = secs;
            Open(r);
        }

        private static string StatsLine(long views, long likes, long duration, string uploadDate)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (views >= 0) parts.Add(FormatCount(views) + " views");
            if (likes >= 0) parts.Add(FormatCount(likes) + " likes");
            if (duration > 0) parts.Add(FormatDuration(duration));
            var when = FormatDate(uploadDate);
            if (when.Length > 0) parts.Add(when);
            return string.Join("   ·   ", parts);
        }

        private static string FormatCount(long n) => n switch
        {
            >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
            >= 1_000 => $"{n / 1_000.0:0.#}K",
            _ => n.ToString(),
        };

        private static string FormatDuration(long seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }

        private static string FormatDate(string yyyymmdd)
        {
            if (yyyymmdd.Length == 8 &&
                DateTime.TryParseExact(yyyymmdd, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return dt.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
            return "";
        }

        private void DrillInto(SearchResult r)
        {
            if (_mode == Mode.Search) _savedSearchPage = _page;
            _mode = Mode.Browse;
            _browseUrl = r.Url;
            _page = 1;
            ContextTitle.Text = r.Title.Length > 0 ? r.Title : r.Channel;
            ContextBar.Visibility = Visibility.Visible;
            FilterBar.Visibility = Visibility.Collapsed; // filters are search-only

            // Channels (not playlists) support search-within. Offer the in-channel box.
            bool isChannel = r.Kind == ResultKind.Channel;
            _channelBase = isChannel ? r.Url.TrimEnd('/') : "";
            _channelStreams = false;
            _inChannelSearch = false;
            ChannelSearchBox.Text = "";
            ChannelSearchBox.Visibility = isChannel ? Visibility.Visible : Visibility.Collapsed;
            UpdateChannelTabs();

            _ = DoLoad();
        }

        private void OnChannelSearchKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter || _channelBase.Length == 0) return;
            var q = ChannelSearchBox.Text.Trim();
            if (q.Length == 0) return;
            _mode = Mode.Browse;
            _browseUrl = _channelBase + "/search?query=" + Uri.EscapeDataString(q);
            _inChannelSearch = true;   // hide the Videos/Streams tabs while showing search results
            _page = 1;
            UpdateChannelTabs();
            _ = DoLoad();
        }

        // ---- channel Videos/Streams tabs ----
        private void OnVideosTab(object sender, RoutedEventArgs e) => SwitchChannelTab(false);
        private void OnStreamsTab(object sender, RoutedEventArgs e) => SwitchChannelTab(true);

        // Switch the channel view between uploads (/videos) and the full stream list (/streams). A tab
        // switch replaces the current view (it isn't a back-stack step) and resets to page 1.
        private void SwitchChannelTab(bool streams)
        {
            if (_channelBase.Length == 0) { UpdateChannelTabs(); return; }
            if (_channelStreams == streams) { UpdateChannelTabs(); return; }   // clicking the active tab: no-op
            _channelStreams = streams;
            _inChannelSearch = false;
            ChannelSearchBox.Text = "";
            _browseUrl = streams ? _channelBase + "/streams" : _channelBase;
            _page = 1;
            UpdateChannelTabs();
            _ = DoLoad();
        }

        // Tabs show only while browsing a channel (hidden on playlists and during an in-channel search).
        private void UpdateChannelTabs()
        {
            bool show = _channelBase.Length > 0 && !_inChannelSearch;
            ChannelTabs.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            VideosTabBtn.IsChecked = !_channelStreams;
            StreamsTabBtn.IsChecked = _channelStreams;
        }

        private void OnBackToSearch(object sender, RoutedEventArgs e)
        {
            _mode = Mode.Search;
            _channelBase = "";
            _channelStreams = false;
            _inChannelSearch = false;
            ChannelSearchBox.Visibility = Visibility.Collapsed;
            ContextBar.Visibility = Visibility.Collapsed;
            FilterBar.Visibility = Visibility.Visible;
            UpdateChannelTabs();
            _page = _savedSearchPage;
            _ = DoLoad();
        }

        // ---- shared right-click menu ----
        private void OnResultsContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e) =>
            ResultMenu.Show(sender, e, new ResultMenuContext
            {
                XamlRoot = XamlRoot,
                Play = r => Open(r),
                OpenChannel = r => DrillInto(r),
            });
    }
}
