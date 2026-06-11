using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using FreeFlume.Models;
using FreeFlume.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FreeFlume.Views
{
    /// <summary>Local playlists: create/delete, add from Search, reorder, play, remove.</summary>
    public sealed partial class PlaylistsPage : Page
    {
        private readonly ObservableCollection<Playlist> _playlists = new();
        private readonly ObservableCollection<SearchResult> _items = new();
        private long _currentId = -1;
        private bool _loadingItems;

        public PlaylistsPage()
        {
            this.InitializeComponent();
            PlaylistsView.ItemsSource = _playlists;
            ItemsView.ItemsSource = _items;
            ItemsView.ContextRequested += OnContextRequested;
            _items.CollectionChanged += OnItemsReordered;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            LoadPlaylists();
        }

        private void LoadPlaylists()
        {
            long keep = _currentId;
            _playlists.Clear();
            foreach (var p in Database.Shared.Playlists()) _playlists.Add(p);

            var select = _playlists.FirstOrDefault(p => p.Id == keep) ?? _playlists.FirstOrDefault();
            PlaylistsView.SelectedItem = select;
            if (select is null) { _currentId = -1; LoadItems(); }
        }

        private void OnPlaylistSelected(object sender, SelectionChangedEventArgs e)
        {
            _currentId = (PlaylistsView.SelectedItem as Playlist)?.Id ?? -1;
            DeleteButton.IsEnabled = _currentId >= 0;
            LoadItems();
        }

        private void LoadItems()
        {
            _loadingItems = true;
            _items.Clear();
            if (_currentId >= 0)
            {
                var items = Database.Shared.PlaylistItems(_currentId);
                Database.Shared.FillWatchProgress(items);
                foreach (var r in items) _items.Add(r);
            }
            EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _loadingItems = false;
        }

        private void OnItemsReordered(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_loadingItems || _currentId < 0 || e.Action != NotifyCollectionChangedAction.Move) return;
            Database.Shared.ReorderPlaylist(_currentId, _items.Select(i => i.Url).ToList());
        }

        private async void OnNewClick(object sender, RoutedEventArgs e)
        {
            var input = new TextBox { PlaceholderText = "Playlist name" };
            var dlg = new ContentDialog
            {
                Title = "New playlist",
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                Content = input,
                XamlRoot = XamlRoot,
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                var name = input.Text.Trim();
                if (name.Length > 0) { _currentId = Database.Shared.CreatePlaylist(name); LoadPlaylists(); }
            }
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            if (PlaylistsView.SelectedItem is not Playlist pl) return;
            var dlg = new ContentDialog
            {
                Title = $"Delete “{pl.Name}”?",
                Content = "This removes the playlist and its items.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                Database.Shared.DeletePlaylist(pl.Id);
                _currentId = -1;
                LoadPlaylists();
            }
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SearchResult r) PlayFrom(r);
        }

        /// <summary>Play a playlist item, handing the player the whole list as an autoplay queue.</summary>
        private void PlayFrom(SearchResult r)
        {
            var items = _items.ToList();
            int idx = items.FindIndex(x => x.Url == r.Url);
            if (idx >= 0) PlayerPage.PendingQueue = (items, idx);
            Frame.Navigate(typeof(PlayerPage), r);
        }

        private void OnContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e) =>
            ResultMenu.Show(sender, e, new ResultMenuContext
            {
                XamlRoot = XamlRoot,
                Play = PlayFrom,
                RemoveLabel = "Remove from playlist",
                Remove = r => { if (_currentId >= 0) { Database.Shared.RemoveFromPlaylist(_currentId, r.Url); LoadItems(); } },
            });
    }
}
