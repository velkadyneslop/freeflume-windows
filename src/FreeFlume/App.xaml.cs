using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Navigation;

namespace FreeFlume
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window window = Window.Current;

        /// <summary>The app's main window (for window-level operations like fullscreen).</summary>
        public static Window? MainWindow { get; private set; }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
        }

        /// <summary>App version as "Major.Minor.Patch" (from the assembly / csproj &lt;Version&gt;).</summary>
        public static string Version
        {
            get
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v is null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            // Log unhandled exceptions so crashes leave a trace we can read.
            UnhandledException += (_, ex) =>
            {
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(Services.AppPaths.DataDir(), "crash.log"),
                        $"{System.DateTimeOffset.Now:u}\n{ex.Exception}\n\n");
                }
                catch { }
                // Keep the app alive: a logged glitch is far better for testers than a crash to desktop.
                // The exception is still recorded above for diagnosis.
                ex.Handled = true;
            };

            // Keep yt-dlp current (YouTube breaks it often): seed the writable copy, then auto-check daily.
            Services.YtDlp.EnsureLocalCopy();
            Services.YtDlp.MaybeAutoUpdate();

            window ??= new Window();
            MainWindow = window;
            window.Title = $"FreeFlume {Version}";
            window.ExtendsContentIntoTitleBar = false;

            // Title-bar/taskbar icon (the .ico is also embedded as the exe icon via ApplicationIcon).
            try
            {
                var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "FreeFlume.ico");
                if (System.IO.File.Exists(icoPath)) window.AppWindow.SetIcon(icoPath);
            }
            catch { /* fall back to the embedded exe icon */ }

            // Size to 1100x720 *logical* px (the Linux default), DPI-aware so it isn't
            // tiny on high-DPI displays. AppWindow.Resize takes physical pixels.
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            uint dpi = GetDpiForWindow(hwnd);
            double scale = dpi == 0 ? 1.0 : dpi / 96.0;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1100 * scale), (int)(720 * scale)));

            if (window.Content is not Frame rootFrame)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                window.Content = rootFrame;
            }

            // Keep the (Win10) title bar in sync with the actual theme.
            rootFrame.ActualThemeChanged += (s, _) => SetTitleBarDark(s.ActualTheme == ElementTheme.Dark);

            _ = rootFrame.Navigate(typeof(ShellPage), e.Arguments);
            ApplyTheme();
            InstallKeyboardHook();
            window.Activate();
            HandleCliArgs();
        }

        /// <summary>Apply command-line arguments: --tab &lt;i&gt;, --play &lt;url&gt;, or trailing text = search query.</summary>
        private static void HandleCliArgs()
        {
            var shell = Views.ShellPage.Current;
            if (shell is null) return;
            var args = Environment.GetCommandLineArgs();   // [0] = exe path
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (a == "--tab" && i + 1 < args.Length && int.TryParse(args[i + 1], out var t)) { shell.SelectTab(t); i++; }
                else if (a == "--play" && i + 1 < args.Length) { shell.PlayUrl(args[i + 1]); i++; }
                else if (!a.StartsWith("--")) { shell.SearchFor(string.Join(" ", args[i..])); break; }
            }
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        /// <summary>Apply the saved color scheme to the app root + the window title bar.</summary>
        public static void ApplyTheme()
        {
            if (MainWindow?.Content is not FrameworkElement root) return;
            root.RequestedTheme = Services.Settings.Shared.ColorScheme switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
            SetTitleBarDark(root.ActualTheme == ElementTheme.Dark);
        }

        /// <summary>Win10-compatible dark title bar via the DWM immersive-dark-mode attribute.</summary>
        private static void SetTitleBarDark(bool dark)
        {
            if (MainWindow is null) return;
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow);
                int v = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref v, sizeof(int));
                RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_FRAME | RDW_INVALIDATE | RDW_UPDATENOW);
                // Simulate a deactivate/activate cycle — that's what actually makes Windows
                // recompute the caption-button glyph colors (the only thing minimize/restore does).
                SendMessageW(hwnd, WM_NCACTIVATE, IntPtr.Zero, IntPtr.Zero);
                SendMessageW(hwnd, WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero);
            }
            catch { /* unsupported -> leave default */ }
        }

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const uint RDW_INVALIDATE = 0x0001, RDW_UPDATENOW = 0x0100, RDW_FRAME = 0x0400;
        private const uint WM_NCACTIVATE = 0x0086;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        // ---- Low-level keyboard hook: player shortcuts that work regardless of XAML focus ----
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        private HookProc? _keyboardProc;   // keep the delegate alive
        private IntPtr _keyboardHook;

        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

        private void InstallKeyboardHook()
        {
            _keyboardProc = KeyboardHookProc;
            _keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, _keyboardProc, GetModuleHandleW(null), 0);
        }

        private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            int msg = (int)wParam;
            if (nCode >= 0 && (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN))
            {
                var player = Views.PlayerPage.Active;
                if (player is not null && MainWindow is not null
                    && GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(MainWindow)
                    && !IsTextInputFocused())   // never steal keystrokes from a text field (search box, etc.)
                {
                    int vk = Marshal.ReadInt32(lParam);   // KBDLLHOOKSTRUCT.vkCode is the first field
                    if (player.TryHandleVk(vk)) return (IntPtr)1;   // consume the key
                }
            }
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        /// <summary>True when a text-editing control currently has keyboard focus, so the player's
        /// global shortcut hook leaves typing/paste (Ctrl+V) alone in the search box, rebind dialog,
        /// subtitle-font box, etc. (an AutoSuggestBox focuses its inner TextBox).</summary>
        private static bool IsTextInputFocused()
        {
            try
            {
                var root = MainWindow?.Content?.XamlRoot;
                if (root is null) return false;
                var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root);
                // An AutoSuggestBox/NumberBox focuses its inner TextBox, so `is TextBox` covers those too.
                return focused is Microsoft.UI.Xaml.Controls.TextBox
                    or Microsoft.UI.Xaml.Controls.AutoSuggestBox
                    or Microsoft.UI.Xaml.Controls.RichEditBox
                    or Microsoft.UI.Xaml.Controls.PasswordBox;
            }
            catch { return false; }
        }
    }
}
