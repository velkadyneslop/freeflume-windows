using System.Diagnostics;
using System.Threading.Tasks;
using FreeFlume.Services;
using Microsoft.UI.Xaml;
using Windows.System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace FreeFlume.Views
{
    /// <summary>App settings, persisted to settings.json. Consumers read Settings.Shared.</summary>
    public sealed partial class SettingsPage : Page
    {
        private readonly Settings _s = Settings.Shared;
        // Start true so control events raised during XAML construction (e.g. a Slider coercing its value
        // up to a non-zero Minimum, firing ValueChanged before InitFromSettings) don't overwrite settings.
        private bool _loading = true;

        public SettingsPage()
        {
            this.InitializeComponent();
            Loaded += (_, __) => InitFromSettings();
        }

        private void InitFromSettings()
        {
            _loading = true;
            SelectByTagOf(SchemeCombo, _s.ColorScheme);
            SelectByContentOf(QualityCombo, _s.Quality);
            VolumeSlider.Value = _s.Volume;
            SelectByTagOf(ResumeCombo, _s.ResumeMode);
            SelectByTagOf(HwCombo, _s.HwDecodeMode);
            AutoplayToggle.IsOn = _s.AutoplayNext;
            MiniPlayerToggle.IsOn = _s.MiniPlayer;
            RememberPipSizeToggle.IsOn = _s.RememberPipSize;
            SharpnessToggle.IsOn = _s.HiDpiVideoSharpness;
            SponsorToggle.IsOn = _s.SponsorBlockEnabled;
            FolderText.Text = _s.EffectiveDownloadFolder;
            SelectByTagOf(MaxHeightCombo, _s.DownloadMaxHeight.ToString());
            EmbedSubsToggle.IsOn = _s.DownloadEmbedSubs;
            SearchLimitBox.Value = _s.SearchLimit;
            IncludeChannelsToggle.IsOn = _s.SearchIncludeChannels;
            IncludePlaylistsToggle.IsOn = _s.SearchIncludePlaylists;
            SearchSuggestionsToggle.IsOn = _s.EnableSearchSuggestions;
            ShotFolderText.Text = EffectiveShotFolder();
            SelectByTagOf(ShotFormatCombo, _s.ScreenshotFormat);
            SubFontBox.Text = _s.SubtitleFont;
            SubSizeSlider.Value = _s.SubtitleFontSize;
            SelectByTagOf(SubColorCombo, _s.SubtitleColor);
            SubOutlineSlider.Value = _s.SubtitleOutline;
            SubShadowSlider.Value = _s.SubtitleShadowOffset;
            SubBoldToggle.IsOn = _s.SubtitleBold;
            SubBackgroundToggle.IsOn = _s.SubtitleBackground;
            SelectByTagOf(SubShadowColorCombo, _s.SubtitleShadowColor);
            SubAutoToggle.IsOn = _s.SubtitleIncludeAuto;
            HistoryToggle.IsOn = _s.RememberHistory;
            SearchHistoryToggle.IsOn = _s.RememberSearch;
            AutoUpdateToggle.IsOn = _s.AutoUpdateYtDlp;
            BackendText.Text = $"Data folder: {AppPaths.DataDir()}\nDetecting backend versions…";
            DetectBackends();
            BuildSponsorRows();
            SetSponsorCategoriesEnabled(_s.SponsorBlockEnabled);
            BuildShortcutRows();
            _loading = false;
        }

        private void BuildSponsorRows()
        {
            if (SponsorCategories.Children.Count > 0) return;
            foreach (var (key, name, color) in SponsorBlock.CategoryInfo)
            {
                var row = new Grid { Padding = new Thickness(0, 5, 0, 5) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var swatch = new Border
                {
                    Width = 12, Height = 12, CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(2, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                    Background = BrushFromHex(color),
                };
                Grid.SetColumn(swatch, 0);

                var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(label, 1);

                var combo = new ComboBox { MinWidth = 130 };
                combo.Items.Add(new ComboBoxItem { Content = "Disabled" });
                combo.Items.Add(new ComboBoxItem { Content = "Auto-skip" });
                combo.Items.Add(new ComboBoxItem { Content = "Manual" });
                combo.SelectedIndex = _s.SponsorMode(key);
                combo.SelectionChanged += (_, __) => { _s.SponsorBlockModes[key] = combo.SelectedIndex; _s.Save(); };
                Grid.SetColumn(combo, 2);

                row.Children.Add(swatch);
                row.Children.Add(label);
                row.Children.Add(combo);
                SponsorCategories.Children.Add(row);
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

        // ---- handlers ----
        private void OnSchemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _s.ColorScheme = TagOf(SchemeCombo) ?? "system";
            _s.Save();
            App.ApplyTheme();
        }

        private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _s.Quality = ContentOf(QualityCombo) ?? "Auto";
            _s.Save();
        }

        private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_loading) return;
            _s.Volume = (int)e.NewValue;
            _s.Save();
        }

        private void OnResumeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _s.ResumeMode = TagOf(ResumeCombo) ?? "resume";
            _s.Save();
        }

        private void OnHwModeChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) { _s.HwDecodeMode = TagOf(HwCombo) ?? "auto-copy"; _s.Save(); } }
        private void OnEmbedSubsToggled(object sender, RoutedEventArgs e) { if (!_loading) { _s.DownloadEmbedSubs = EmbedSubsToggle.IsOn; _s.Save(); } }
        private void OnIncludeChannelsToggled(object sender, RoutedEventArgs e) { if (!_loading) { _s.SearchIncludeChannels = IncludeChannelsToggle.IsOn; _s.Save(); } }
        private void OnIncludePlaylistsToggled(object sender, RoutedEventArgs e) { if (!_loading) { _s.SearchIncludePlaylists = IncludePlaylistsToggle.IsOn; _s.Save(); } }
        private void OnSearchSuggestionsToggled(object sender, RoutedEventArgs e) { if (!_loading) { _s.EnableSearchSuggestions = SearchSuggestionsToggle.IsOn; _s.Save(); } }

        private void OnSearchLimitChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (_loading || double.IsNaN(args.NewValue)) return;
            _s.SearchLimit = Math.Clamp((int)args.NewValue, 5, 100);
            _s.Save();
        }
        private void OnAutoplayToggled(object sender, RoutedEventArgs e) { if (!_loading) { _s.AutoplayNext = AutoplayToggle.IsOn; _s.Save(); } }
        private void OnMiniPlayerToggled(object sender, RoutedEventArgs e) { if (!_loading) { _s.MiniPlayer = MiniPlayerToggle.IsOn; _s.Save(); } }
        private void OnRememberPipSizeToggled(object sender, RoutedEventArgs e) { if (!_loading) { _s.RememberPipSize = RememberPipSizeToggle.IsOn; _s.Save(); } }
        private void OnSharpnessToggled(object sender, RoutedEventArgs e) { if (!_loading) { _s.HiDpiVideoSharpness = SharpnessToggle.IsOn; _s.Save(); } }
        private void OnSponsorToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _s.SponsorBlockEnabled = SponsorToggle.IsOn;
            _s.Save();
            SetSponsorCategoriesEnabled(SponsorToggle.IsOn);
        }

        private void SetSponsorCategoriesEnabled(bool on)
        {
            SponsorCategories.Opacity = on ? 1.0 : 0.4;
            SponsorCategories.IsHitTestVisible = on;
        }
        private void OnHistoryToggled(object sender, RoutedEventArgs e) { if (!_loading) { _s.RememberHistory = HistoryToggle.IsOn; _s.Save(); } }
        private void OnSearchHistoryToggled(object sender, RoutedEventArgs e) { if (!_loading) { _s.RememberSearch = SearchHistoryToggle.IsOn; _s.Save(); } }
        private void OnClearSearchClick(object sender, RoutedEventArgs e) => Database.Shared.ClearSearchHistory();

        // ---- subtitle fetching (applies on the next video load) ----
        private void OnSubAutoToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _s.SubtitleIncludeAuto = SubAutoToggle.IsOn;
            _s.Save();
        }

        private void OnSubShadowColorChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _s.SubtitleShadowColor = TagOf(SubShadowColorCombo) ?? "#FF000000";
            _s.Save(); ApplySubLive();
        }

        // ---- subtitle styling (live-applies to a playing video) ----
        private static void ApplySubLive() => PlayerPage.Active?.ApplySubtitleStyle();

        private void OnSubFontChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading) return;
            _s.SubtitleFont = SubFontBox.Text.Trim();
            _s.Save(); ApplySubLive();
        }

        private void OnSubSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_loading) return;
            _s.SubtitleFontSize = (int)e.NewValue;
            _s.Save(); ApplySubLive();
        }

        private void OnSubColorChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _s.SubtitleColor = TagOf(SubColorCombo) ?? "#FFFFFF";
            _s.Save(); ApplySubLive();
        }

        private void OnSubOutlineChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_loading) return;
            _s.SubtitleOutline = (int)e.NewValue;
            _s.Save(); ApplySubLive();
        }

        private void OnSubShadowChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_loading) return;
            _s.SubtitleShadowOffset = (int)e.NewValue;
            _s.Save(); ApplySubLive();
        }

        private void OnSubBoldToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _s.SubtitleBold = SubBoldToggle.IsOn;
            _s.Save(); ApplySubLive();
        }

        private void OnSubBackgroundToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _s.SubtitleBackground = SubBackgroundToggle.IsOn;
            _s.Save(); ApplySubLive();
        }

        private async void OnChangeFolderClick(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                _s.DownloadFolder = folder.Path;
                _s.Save();
                FolderText.Text = folder.Path;
            }
        }

        private void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            _s.AutoUpdateYtDlp = AutoUpdateToggle.IsOn;
            _s.Save();
        }

        private async void OnUpdateYtDlpClick(object sender, RoutedEventArgs e)
        {
            UpdateYtDlpButton.IsEnabled = false;
            UpdateStatusText.Text = "Checking for yt-dlp updates…";
            string result = await YtDlp.UpdateAsync();
            UpdateStatusText.Text = result;
            DetectBackends();   // refresh the shown version
            UpdateYtDlpButton.IsEnabled = true;
        }

        private async void DetectBackends()
        {
            string ytdlp = await RunVersion(YtDlp.ExePath, "--version");
            string ffmpeg = FirstLine(await RunVersion("ffmpeg.exe", "-version"));
            string mpv = Player.MpvPlayer.MpvVersion();
            if (mpv.Length == 0) mpv = "mpv (libmpv-2.dll)";
            BackendText.Text = $"FreeFlume v{App.Version}\nData folder: {AppPaths.DataDir()}\n" +
                               $"yt-dlp {Short(ytdlp)}\n{Short(mpv)}\nffmpeg {Short(ffmpeg)}";
        }

        private static async Task<string> RunVersion(string exe, string arg)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = System.IO.Path.Combine(System.AppContext.BaseDirectory, exe),
                    Arguments = arg,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null) return "(not found)";
                string outp = await p.StandardOutput.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(outp)) outp = await p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                return outp.Trim();
            }
            catch { return "(not found)"; }
        }

        private static string FirstLine(string s)
        {
            int nl = s.IndexOf('\n');
            return (nl >= 0 ? s[..nl] : s).Trim();
        }

        private static string Short(string s) => s.Length > 80 ? s[..80] : s;

        // ---- keyboard shortcuts ----
        private void BuildShortcutRows()
        {
            ShortcutRows.Children.Clear();
            foreach (var def in FreeFlume.Models.Shortcuts.All)
            {
                var row = new Grid { Padding = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new TextBlock { Text = def.Label, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(label, 0);

                var btn = new Button { Content = VkName(_s.ShortcutVk(def.Id)), MinWidth = 96 };
                string id = def.Id, lbl = def.Label;
                btn.Click += (_, __) => RebindAsync(id, lbl, btn);
                Grid.SetColumn(btn, 1);

                row.Children.Add(label);
                row.Children.Add(btn);
                ShortcutRows.Children.Add(row);
            }
        }

        private async void RebindAsync(string id, string label, Button btn)
        {
            var content = new TextBlock { Text = "Press any key…   (Esc to cancel)", TextWrapping = TextWrapping.Wrap };
            var dlg = new ContentDialog { Title = $"Rebind: {label}", Content = content, CloseButtonText = "Cancel", XamlRoot = XamlRoot };
            int? captured = null;
            dlg.KeyDown += (_, e) =>
            {
                var k = e.Key;
                if (k is VirtualKey.None or VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu
                       or VirtualKey.LeftWindows or VirtualKey.RightWindows or VirtualKey.CapitalLock) return;
                if (k == VirtualKey.Escape) { dlg.Hide(); return; }
                captured = (int)k;
                e.Handled = true;
                dlg.Hide();
            };
            await dlg.ShowAsync();
            if (captured is int vk)
            {
                // If another action already uses this key, clear it (no duplicates).
                foreach (var d in FreeFlume.Models.Shortcuts.All)
                    if (d.Id != id && _s.ShortcutVk(d.Id) == vk) _s.Shortcuts[d.Id] = 0;
                _s.Shortcuts[id] = vk;
                _s.Save();
                BuildShortcutRows();   // refresh (a cleared conflict may now show blank)
            }
        }

        private void OnResetShortcuts(object sender, RoutedEventArgs e)
        {
            _s.Shortcuts.Clear();
            _s.Save();
            BuildShortcutRows();
        }

        private static string VkName(int vk) => vk switch
        {
            0 => "—",
            0x20 => "Space", 0x25 => "Left", 0x27 => "Right", 0x26 => "Up", 0x28 => "Down",
            0x0D => "Enter", 0x1B => "Esc", 0x08 => "Backspace", 0x09 => "Tab",
            0xBC => ",", 0xBE => ".", 0xBD => "-", 0xBB => "=", 0xBF => "/", 0xDC => "\\",
            0xDB => "[", 0xDD => "]", 0xBA => ";", 0xDE => "'", 0xC0 => "`",
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),                 // A–Z
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),                 // 0–9
            >= 0x60 and <= 0x69 => "Num" + (vk - 0x60),                   // numpad 0–9
            >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),                     // F1–F24
            _ => "0x" + vk.ToString("X2"),
        };

        private string EffectiveShotFolder() => string.IsNullOrWhiteSpace(_s.ScreenshotFolder)
            ? System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), "FreeFlume")
            : _s.ScreenshotFolder;

        private void OnMaxHeightChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _s.DownloadMaxHeight = int.TryParse(TagOf(MaxHeightCombo), out var h) ? h : 0;
            _s.Save();
        }

        private void OnShotFormatChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            _s.ScreenshotFormat = TagOf(ShotFormatCombo) ?? "png";
            _s.Save();
        }

        private async void OnChangeShotFolderClick(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                _s.ScreenshotFolder = folder.Path;
                _s.Save();
                ShotFolderText.Text = folder.Path;
            }
        }

        private async void OnClearHistoryClick(object sender, RoutedEventArgs e)
        {
            var dlg = new ContentDialog
            {
                Title = "Clear watch history?",
                Content = "This permanently removes all history entries.",
                PrimaryButtonText = "Clear",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                Database.Shared.ClearHistory();
        }

        // ---- combo helpers ----
        private static void SelectByTagOf(ComboBox combo, string tag)
        {
            foreach (var obj in combo.Items)
                if (obj is ComboBoxItem ci && (ci.Tag as string) == tag) { combo.SelectedItem = ci; return; }
        }

        private static void SelectByContentOf(ComboBox combo, string content)
        {
            foreach (var obj in combo.Items)
                if (obj is ComboBoxItem ci && (ci.Content as string) == content) { combo.SelectedItem = ci; return; }
        }

        private static string? TagOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;
        private static string? ContentOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Content as string;
    }
}
