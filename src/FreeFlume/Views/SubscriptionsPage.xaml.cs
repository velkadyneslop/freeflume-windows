using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using FreeFlume.Models;
using FreeFlume.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FreeFlume.Views
{
    /// <summary>Subscribed channels + their recent uploads (YT RSS). "What's New" merges all.</summary>
    public sealed partial class SubscriptionsPage : Page
    {
        private readonly ObservableCollection<Subscription> _channels = new();
        private readonly ObservableCollection<SearchResult> _feed = new();
        private readonly SubscriptionFeed _feedSvc = new();
        private int _gen;

        public SubscriptionsPage()
        {
            this.InitializeComponent();
            ChannelsView.ItemsSource = _channels;
            FeedView.ItemsSource = _feed;
            FeedView.ContextRequested += OnFeedContextRequested;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            LoadChannels();
            _ = ShowWhatsNewAsync();
        }

        private void LoadChannels()
        {
            _channels.Clear();
            foreach (var s in Database.Shared.Subscriptions()) _channels.Add(s);
            CheckChannelsLive();
        }

        // Light a red ring on any channel that's currently live (background, throttled, cached).
        private void CheckChannelsLive()
        {
            foreach (var sub in _channels)
            {
                if (string.IsNullOrEmpty(sub.ChannelUrl)) continue;
                var s = sub;
                _ = Task.Run(async () =>
                {
                    if (await LiveStatus.Shared.IsLiveAsync(s.ChannelUrl))
                        DispatcherQueue.TryEnqueue(() => s.ChannelLive = true);
                });
            }
        }

        private async Task ShowWhatsNewAsync()
        {
            ChannelsView.SelectedItem = null;
            FeedTitle.Text = "What's New";
            if (_channels.Count == 0) { ShowStatus("Subscribe to channels from Search to see their latest uploads here."); return; }
            await LoadFeedAsync(() => _feedSvc.WhatsNewAsync(_channels));
        }

        private void OnWhatsNewClick(object sender, RoutedEventArgs e) => _ = ShowWhatsNewAsync();

        private async void OnImportClick(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            foreach (var ext in new[] { ".json", ".db", ".csv", ".opml", ".xml", ".txt" }) picker.FileTypeFilter.Add(ext);
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            List<SubscriptionIO.Entry> entries;
            try { entries = SubscriptionIO.Parse(file.Path); }
            catch (System.Exception ex) { await Info("Import failed", ex.Message); return; }

            foreach (var en in entries)
                Database.Shared.Subscribe(en.Name.Length > 0 ? en.Name : en.ChannelId, en.Url, en.ChannelId, en.Avatar);

            LoadChannels();
            _ = ShowWhatsNewAsync();
            await Info("Import complete", entries.Count == 0
                ? "No channels were found in that file."
                : $"Imported {entries.Count} channel(s).");
        }

        private async void OnExportClick(object sender, RoutedEventArgs e)
        {
            var subs = Database.Shared.Subscriptions();
            if (subs.Count == 0) { await Info("Export", "You have no subscriptions to export."); return; }

            var picker = new Windows.Storage.Pickers.FileSavePicker();
            picker.FileTypeChoices.Add("OPML", new List<string> { ".opml" });
            picker.SuggestedFileName = "freeflume-subscriptions";
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            await Windows.Storage.FileIO.WriteTextAsync(file, SubscriptionIO.ToOpml(subs));
            await Info("Export complete", $"Saved {subs.Count} channel(s) to {file.Name}.");
        }

        private async Task Info(string title, string message)
        {
            var dlg = new ContentDialog { Title = title, Content = message, CloseButtonText = "OK", XamlRoot = XamlRoot };
            await dlg.ShowAsync();
        }

        private async void OnChannelSelected(object sender, SelectionChangedEventArgs e)
        {
            if (ChannelsView.SelectedItem is not Subscription sub) return;
            FeedTitle.Text = sub.ChannelName;
            await LoadFeedAsync(() => _feedSvc.ChannelFeedAsync(sub.ChannelId));
        }

        private async Task LoadFeedAsync(System.Func<Task<System.Collections.Generic.List<SearchResult>>> fetch)
        {
            int gen = ++_gen;
            _feed.Clear();
            Status.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = true;
            try
            {
                var items = await fetch();
                if (gen != _gen) return; // a newer load started
                Database.Shared.FillWatchProgress(items);
                foreach (var r in items) _feed.Add(r);
                if (_feed.Count == 0) ShowStatus("No recent uploads found.");
            }
            finally
            {
                if (gen == _gen) LoadingRing.IsActive = false;
            }
        }

        private void ShowStatus(string text)
        {
            LoadingRing.IsActive = false;
            Status.Text = text;
            Status.Visibility = Visibility.Visible;
        }

        private void OnUnsubscribe(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is Subscription s)
            {
                Database.Shared.Unsubscribe(s.ChannelUrl);
                LoadChannels();
                _ = ShowWhatsNewAsync();
            }
        }

        private void OnFeedItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SearchResult r) PlayFeed(r);
        }

        /// <summary>Play a feed item, handing the player the rest of the feed as an autoplay queue.</summary>
        private void PlayFeed(SearchResult r)
        {
            var items = new System.Collections.Generic.List<SearchResult>(_feed);
            int idx = items.FindIndex(x => x.Url == r.Url);
            if (idx >= 0) PlayerPage.PendingQueue = (items, idx);
            Frame.Navigate(typeof(PlayerPage), r);
        }

        private void OnFeedContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e) =>
            ResultMenu.Show(sender, e, new ResultMenuContext { XamlRoot = XamlRoot, Play = PlayFeed });

        // ---- channel-list extras (Subscription rows aren't SearchResults, so handled here) ----
        private void OnChannelOpen(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not Subscription s) return;
            string url = !string.IsNullOrEmpty(s.ChannelUrl) ? s.ChannelUrl
                       : (s.ChannelId.StartsWith("UC") ? "https://www.youtube.com/channel/" + s.ChannelId : "");
            if (url.Length == 0) return;
            SearchPage.PendingBrowse = (url, s.ChannelName);
            ShellPage.Current?.SelectTab(0);   // open Search, which drills into the channel
        }

        private void OnChannelCopyLink(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is Subscription s)
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(s.ChannelUrl);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            }
        }

        private async void OnChannelOpenBrowser(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is Subscription s &&
                System.Uri.TryCreate(s.ChannelUrl, System.UriKind.Absolute, out var uri))
                await Windows.System.Launcher.LaunchUriAsync(uri);
        }
    }
}
