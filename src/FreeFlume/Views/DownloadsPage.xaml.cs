using System.Collections.Specialized;
using System.Diagnostics;
using FreeFlume.Models;
using FreeFlume.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace FreeFlume.Views
{
    /// <summary>Download queue with live progress; cancel, clear finished, open folder.</summary>
    public sealed partial class DownloadsPage : Page
    {
        public DownloadsPage()
        {
            this.InitializeComponent();
            ItemsView.ItemsSource = DownloadManager.Shared.Items;
            DownloadManager.Shared.Items.CollectionChanged += (_, __) => UpdateEmpty();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            UpdateEmpty();
        }

        private void UpdateEmpty() =>
            EmptyText.Visibility = DownloadManager.Shared.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DownloadItem item)
                DownloadManager.Shared.Cancel(item);
        }

        private void OnClearFinishedClick(object sender, RoutedEventArgs e) => DownloadManager.Shared.ClearFinished();

        private void OnOpenFolderClick(object sender, RoutedEventArgs e)
        {
            var folder = AppPaths.DownloadsDir();
            System.IO.Directory.CreateDirectory(folder);
            try { Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); } catch { }
        }
    }
}
