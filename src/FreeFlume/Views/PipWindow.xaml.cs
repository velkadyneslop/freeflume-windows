// FreeFlume — true Picture-in-Picture: a separate borderless, always-on-top window with its OWN
// mpv instance (XAML can't move a SwapChainPanel across windows). The main window shows a
// "playing in PiP" placard; closing this window returns playback to the main player at this
// window's position.
// Author: velkadyne

using System;
using System.Runtime.InteropServices;
using FreeFlume.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FreeFlume.Views
{
    public sealed partial class PipWindow : Window
    {
        /// <summary>Raised when the PiP window closes; carries the playback position to resume at.</summary>
        public event Action<double>? Returned;

        public double Position => PipPlayer.Position;

        /// <summary>The PiP window's mpv instance, so the main page's transport controls can drive it.</summary>
        public FreeFlume.Player.MpvPlayer Player => PipPlayer;

        private readonly double _aspect;
        private readonly DispatcherTimer _hideBar = new() { Interval = TimeSpan.FromSeconds(3) };
        private bool _overBar;
        private bool _closed;
        private IntPtr _hwnd;

        public PipWindow(double aspect)
        {
            _aspect = aspect > 0 ? aspect : 16.0 / 9.0;
            InitializeComponent();
            Title = "FreeFlume — Picture in Picture";

            // Fresh borderless presenter (mutating the existing one leaves a title-bar strip).
            var aw = AppWindow;
            var p = OverlappedPresenter.Create();
            p.IsAlwaysOnTop = true;
            p.IsResizable = true;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.SetBorderAndTitleBar(false, false);
            aw.SetPresenter(p);

            // Size to the remembered width (or 480) at the video aspect, parked bottom-right.
            var work = DisplayArea.GetFromWindowId(aw.Id, DisplayAreaFallback.Primary).WorkArea;
            int w = (Settings.Shared.RememberPipSize && Settings.Shared.PipWidth > 0) ? Settings.Shared.PipWidth : 480;
            w = Math.Clamp(w, 240, Math.Max(240, work.Width - 48));
            int h = (int)Math.Round(w / _aspect);
            aw.MoveAndResize(new Windows.Graphics.RectInt32(
                work.X + work.Width - w - 24, work.Y + work.Height - h - 24, w, h));

            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            HookResize();
            // Dark caption so any residual title-bar pixels are black (not a white strip).
            int dark = 1;
            try { DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)); } catch { }

            PipPlayer.PausedChanged += OnPaused;
            PipPlayer.TitleChanged += t => { if (!string.IsNullOrEmpty(t) && TitleText.Text.Length == 0) TitleText.Text = t; };

            _hideBar.Tick += (_, __) => { _hideBar.Stop(); if (!_overBar && !PipPlayer.Paused) Bar.Visibility = Visibility.Collapsed; };

            // Esc returns to the player; Space toggles play/pause.
            if (Content is UIElement root)
            {
                root.KeyDown += (_, e) =>
                {
                    if (e.Key == Windows.System.VirtualKey.Escape) { Close(); e.Handled = true; }
                    else if (e.Key == Windows.System.VirtualKey.Space) { PipPlayer.TogglePause(); e.Handled = true; }
                };
            }

            Closed += OnClosed;
            Activated += OnFirstActivated;
        }

        private bool _reasserted;
        private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
        {
            if (_reasserted) return;
            _reasserted = true;
            // Re-assert borderless now the window is shown — setting it pre-activation leaves a
            // residual caption strip (visible as a gray line when the window is inactive).
            if (AppWindow.Presenter is OverlappedPresenter op) op.SetBorderAndTitleBar(false, false);
        }

        private string _subTrack = "no";
        private bool _subApplied;

        /// <summary>Start playback in this window at the given position. <paramref name="subTrack"/> is the
        /// main player's subtitle selection ("no" or an sid) applied once PiP's tracks load, so captions
        /// stay in sync between the two players.</summary>
        public void PlayPip(string url, double startSeconds, string title, string subTrack)
        {
            TitleText.Text = title ?? "";
            _subTrack = subTrack;
            PipPlayer.TracksChanged += OnPipTracks;
            PipPlayer.Play(url, startSeconds);
            ShowBar();
        }

        private void OnPipTracks(System.Collections.Generic.List<FreeFlume.Models.MediaTrack> tracks)
        {
            if (_subApplied) return;   // apply the inherited subtitle selection just once
            _subApplied = true;
            PipPlayer.SetSubTrack(_subTrack);
        }

        private void OnPaused(bool paused)
        {
            PlayIcon.Glyph = ((char)(paused ? 0xE768 : 0xE769)).ToString();
            KeepAwake.Set(!paused);
            if (paused) { Bar.Visibility = Visibility.Visible; _hideBar.Stop(); }
            else { _hideBar.Stop(); _hideBar.Start(); }
        }

        private void OnClosed(object sender, WindowEventArgs args)
        {
            if (_closed) return;
            _closed = true;
            double pos = PipPlayer.Position;
            if (Settings.Shared.RememberPipSize && AppWindow.Size.Width > 0)
            {
                Settings.Shared.PipWidth = AppWindow.Size.Width;
                Settings.Shared.Save();
            }
            UnhookResize();
            KeepAwake.Set(false);
            try { PipPlayer.Dispose(); } catch { }
            Returned?.Invoke(pos);
        }

        private void OnPlayPause(object sender, RoutedEventArgs e) => PipPlayer.TogglePause();
        private void OnReturn(object sender, RoutedEventArgs e) => Close();

        // ---- auto-hiding control bar ----
        private void OnPointerMoved(object sender, PointerRoutedEventArgs e) => ShowBar();
        private void OnBarEnter(object sender, PointerRoutedEventArgs e) { _overBar = true; ShowBar(); }
        private void OnBarExit(object sender, PointerRoutedEventArgs e) { _overBar = false; _hideBar.Stop(); _hideBar.Start(); }
        private void ShowBar() { Bar.Visibility = Visibility.Visible; _hideBar.Stop(); _hideBar.Start(); }

        // ---- drag the borderless window by the video ----
        private bool _dragging;
        private POINT _dragCursor0;
        private int _dragX0, _dragY0;
        private UIElement? _dragEl;

        private void OnDragStart(object sender, PointerRoutedEventArgs e)
        {
            if (!GetCursorPos(out _dragCursor0)) return;
            _dragX0 = AppWindow.Position.X; _dragY0 = AppWindow.Position.Y;
            _dragging = true;
            _dragEl = sender as UIElement;
            _dragEl?.CapturePointer(e.Pointer);
        }

        private void OnDragMove(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging || !GetCursorPos(out var cur)) return;
            AppWindow.Move(new Windows.Graphics.PointInt32(
                _dragX0 + (cur.X - _dragCursor0.X), _dragY0 + (cur.Y - _dragCursor0.Y)));
        }

        private void OnDragEnd(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            _dragEl?.ReleasePointerCapture(e.Pointer);
            _dragEl = null;
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        // ---- aspect-locked resize (WM_SIZING subclass on this window) ----
        private const int GWLP_WNDPROC = -4;
        private const uint WM_SIZING = 0x0214;
        private const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4, WMSZ_TOPRIGHT = 5, WMSZ_BOTTOM = 6;

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        private WndProcDelegate? _proc;
        private IntPtr _oldProc;

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr_Delegate(IntPtr hwnd, int index, WndProcDelegate newProc);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr_Ptr(IntPtr hwnd, int index, IntPtr newProc);
        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProcW(IntPtr prev, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        private void HookResize() { _proc = WndProc; _oldProc = SetWindowLongPtr_Delegate(_hwnd, GWLP_WNDPROC, _proc); }
        private void UnhookResize() { if (_oldProc != IntPtr.Zero) { SetWindowLongPtr_Ptr(_hwnd, GWLP_WNDPROC, _oldProc); _oldProc = IntPtr.Zero; _proc = null; } }

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_SIZING && _aspect > 0)
            {
                var rc = Marshal.PtrToStructure<RECT>(lParam);
                int edge = (int)wParam;
                int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
                if (edge == WMSZ_LEFT || edge == WMSZ_RIGHT) rc.Bottom = rc.Top + (int)Math.Round(w / _aspect);
                else if (edge == WMSZ_TOP || edge == WMSZ_BOTTOM) rc.Right = rc.Left + (int)Math.Round(h * _aspect);
                else if (edge == WMSZ_TOPLEFT || edge == WMSZ_TOPRIGHT) rc.Top = rc.Bottom - (int)Math.Round(w / _aspect);
                else rc.Bottom = rc.Top + (int)Math.Round(w / _aspect);
                Marshal.StructureToPtr(rc, lParam, false);
                return (IntPtr)1;
            }
            return CallWindowProcW(_oldProc, hwnd, msg, wParam, lParam);
        }
    }
}
