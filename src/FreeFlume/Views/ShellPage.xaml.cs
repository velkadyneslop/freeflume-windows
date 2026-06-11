using System;
using FreeFlume.Player;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace FreeFlume.Views
{
    /// <summary>
    /// App shell: a NavigationView sidebar hosting the six FreeFlume sections in an
    /// inner Frame. Selection maps each item's Tag to a page type in FreeFlume.Views.
    /// </summary>
    public sealed partial class ShellPage : Page
    {
        /// <summary>The live shell, so pages can hide its chrome (e.g. for fullscreen video).</summary>
        public static ShellPage? Current { get; private set; }

        private Brush? _navBg;   // the normal (gray) nav background, restored when chrome is shown

        public ShellPage()
        {
            this.InitializeComponent();
            Current = this;
            _navBg = Nav.Background;
            Loaded += (_, __) =>
            {
                if (ContentFrame.Content is null)
                    Navigate(typeof(SearchPage));
            };

            // Player shortcuts are handled solely by the app-wide low-level keyboard hook
            // (App.KeyboardHookProc -> PlayerPage.TryHandleVk), which doesn't need focus.
            // We deliberately do NOT forward KeyDown or grab focus on click here — the old
            // focus-grab interfered with the nav pane toggle, and is no longer needed.
        }

        /// <summary>Show/hide the nav pane so a page can take the whole window (fullscreen).</summary>
        public void SetChromeVisible(bool visible)
        {
            Nav.IsPaneVisible = visible;
            // While the chrome is hidden (fullscreen video), paint the shell black so any 1px
            // seam at the content edge shows black instead of the gray nav background.
            Nav.Background = visible ? _navBg : new SolidColorBrush(Microsoft.UI.Colors.Black);
        }

        /// <summary>Collapse the nav pane to icons (e.g. while watching a video) or expand it.</summary>
        public void SetPaneOpen(bool open) => Nav.IsPaneOpen = open;

        private double _compactPaneLength = 48;   // the pane's normal collapsed (icon-rail) width

        /// <summary>
        /// Hide the sidebar for PiP by collapsing the pane to zero width. We deliberately do NOT
        /// toggle <c>IsPaneVisible</c> — doing that while the window is compact wedges the
        /// NavigationView's layout and it won't recover when the window grows back. Shrinking the
        /// compact-pane length is a plain layout change the pane reflows cleanly.
        /// </summary>
        public void HidePaneForPip()
        {
            _compactPaneLength = Nav.CompactPaneLength;
            Nav.IsPaneOpen = false;
            Nav.IsPaneToggleButtonVisible = false;
            Nav.CompactPaneLength = 0;
        }

        /// <summary>Restore the sidebar after leaving PiP (open = expanded vs. icon rail).</summary>
        public void RestorePaneForPip(bool open)
        {
            Nav.CompactPaneLength = _compactPaneLength;
            Nav.IsPaneToggleButtonVisible = true;
            Nav.IsPaneOpen = open;
        }

        // ---- mini player ----
        private MpvPlayer? _miniPlayer;

        /// <summary>Reparent the live player into the corner overlay and keep it playing.</summary>
        public void ShowMiniPlayer(MpvPlayer player, string title)
        {
            _miniPlayer = player;
            player.MoveTo(MiniVideoHost);
            MiniTitle.Text = title;
            MiniPanel.Visibility = Visibility.Visible;
            player.PausedChanged += OnMiniPaused;
            OnMiniPaused(player.Paused);
        }

        /// <summary>Update the mini-player label if it's showing (e.g. a title that loaded late).</summary>
        public void UpdateMiniTitle(string title)
        {
            if (_miniPlayer is not null && title.Length > 0) MiniTitle.Text = title;
        }

        /// <summary>Hide the mini overlay (the caller reparents the player back).</summary>
        public void HideMiniPlayer()
        {
            if (_miniPlayer is not null) _miniPlayer.PausedChanged -= OnMiniPaused;
            MiniPanel.Visibility = Visibility.Collapsed;
        }

        private void OnMiniPaused(bool paused) => MiniPlayIcon.Glyph = ((char)(paused ? 0xE768 : 0xE769)).ToString();

        private void OnMiniPlayPause(object sender, RoutedEventArgs e) => _miniPlayer?.TogglePause();
        private void OnMiniExpand(object sender, RoutedEventArgs e) => ExpandMini();
        private void OnMiniTapped(object sender, TappedRoutedEventArgs e) => ExpandMini();

        private void ExpandMini() => ContentFrame.Navigate(typeof(PlayerPage), PlayerPage.Resume);

        private void OnMiniClose(object sender, RoutedEventArgs e)
        {
            PlayerPage.Instance?.CloseFromMini();
            HideMiniPlayer();
            _miniPlayer = null;
        }

        // ---- command-line entry points (App parses argv and calls these) ----

        /// <summary>Select a sidebar section by index (0=Search … 4=Downloads).</summary>
        public void SelectTab(int index)
        {
            if (index >= 0 && index < Nav.MenuItems.Count) Nav.SelectedItem = Nav.MenuItems[index];
        }

        /// <summary>Jump straight into the player for a URL (--play).</summary>
        public void PlayUrl(string url) => ContentFrame.Navigate(typeof(PlayerPage), url);

        /// <summary>Open Search and run a query (trailing CLI text).</summary>
        public void SearchFor(string query)
        {
            Navigate(typeof(SearchPage));
            Nav.SelectedItem = Nav.MenuItems[0];
            if (ContentFrame.Content is SearchPage sp) sp.RunQuery(query);
        }

        private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                Navigate(typeof(SettingsPage));
                return;
            }

            if (args.SelectedItemContainer is NavigationViewItem { Tag: string tag })
            {
                var pageType = Type.GetType($"FreeFlume.Views.{tag}");
                if (pageType is not null)
                    Navigate(pageType);
            }
        }

        private void Navigate(Type pageType)
        {
            if (ContentFrame.Content?.GetType() == pageType)
                return;
            ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());
        }
    }
}
