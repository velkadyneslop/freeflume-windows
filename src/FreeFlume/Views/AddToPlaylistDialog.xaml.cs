using FreeFlume.Models;
using FreeFlume.Services;
using Microsoft.UI.Xaml.Controls;

namespace FreeFlume.Views
{
    /// <summary>Adds a result to an existing or newly-created playlist.</summary>
    public sealed partial class AddToPlaylistDialog : ContentDialog
    {
        private readonly SearchResult _item;

        public AddToPlaylistDialog(SearchResult item)
        {
            this.InitializeComponent();
            _item = item;

            foreach (var pl in Database.Shared.Playlists()) PlaylistCombo.Items.Add(pl);
            if (PlaylistCombo.Items.Count > 0) PlaylistCombo.SelectedIndex = 0;

            PrimaryButtonClick += OnAdd;
        }

        private void OnAdd(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var newName = NewNameBox.Text.Trim();
            long playlistId;
            if (newName.Length > 0)
                playlistId = Database.Shared.CreatePlaylist(newName);
            else if (PlaylistCombo.SelectedItem is Playlist pl)
                playlistId = pl.Id;
            else
            {
                args.Cancel = true; // nothing chosen
                return;
            }
            Database.Shared.AddToPlaylist(playlistId, _item);
        }
    }
}
