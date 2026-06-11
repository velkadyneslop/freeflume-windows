using System.Collections.ObjectModel;
using FreeFlume.Models;
using FreeFlume.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FreeFlume.Views
{
    /// <summary>Watch history, newest first. Click to play; remove single items or clear all.</summary>
    public sealed partial class HistoryPage : Page
    {
        private readonly ObservableCollection<SearchResult> _items = new();

        public HistoryPage()
        {
            this.InitializeComponent();
            ItemsView.ItemsSource = _items;
            ItemsView.ContextRequested += OnContextRequested;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Refresh();
        }

        private void Refresh()
        {
            _items.Clear();
            var history = Database.Shared.History();
            Database.Shared.FillWatchProgress(history);
            foreach (var r in history) _items.Add(r);
            EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SearchResult r) PlayFrom(r);
        }

        private void PlayFrom(SearchResult r)
        {
            var items = new System.Collections.Generic.List<SearchResult>(_items);
            int idx = items.FindIndex(x => x.Url == r.Url);
            if (idx >= 0) PlayerPage.PendingQueue = (items, idx);
            Frame.Navigate(typeof(PlayerPage), r);
        }

        private void OnContextRequested(UIElement sender, Microsoft.UI.Xaml.Input.ContextRequestedEventArgs e) =>
            ResultMenu.Show(sender, e, new ResultMenuContext
            {
                XamlRoot = XamlRoot,
                Play = PlayFrom,
                RemoveLabel = "Remove from History",
                Remove = r => { Database.Shared.RemoveHistoryItem(r.Url); Refresh(); },
            });

        private void OnClearClick(object sender, RoutedEventArgs e)
        {
            Database.Shared.ClearHistory();
            Refresh();
        }
    }
}
