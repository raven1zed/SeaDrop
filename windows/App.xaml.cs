using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using WinRT.Interop;
using SeaDropWindows.SeaDrop.core;

namespace SeaDropWindows
{
    // ── Tray icon state ──────────────────────────────────────────────────────
    internal enum TrayState
    {
        Grey,        // disconnected / startup
        Amber,       // connecting / hotspot starting
        Green,       // ESP32 TCP connected
        OrangeRing,  // transfer in progress
    }

    public partial class App : Application
    {
        internal readonly SeaDropService _seaDropService;
        private Window? _mainWindow;
        private bool _isExiting;
        private readonly DispatcherQueue _dispatcherQueue;

        // ── Win32 tray icon ──────────────────────────────────────────────────
        private const int WM_TRAYICON     = 0x8000;
        private const int WM_LBUTTONDOWN  = 0x0201;
        private const int WM_RBUTTONDOWN  = 0x0204;
        private const int NIM_ADD         = 0;
        private const int NIM_MODIFY      = 1;
        private const int NIM_DELETE      = 2;
        private const int NIF_MESSAGE     = 1;
        private const int NIF_ICON        = 2;
        private const int NIF_TIP         = 4;

        private NativeWindow? _trayWindow;
        private NOTIFYICONDATAW _nid;
        private TrayState _trayState = TrayState.Grey;
        private TrayState _priorState = TrayState.Grey; // state before a transfer

        [DllImport("gdi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern int AddFontResourceEx(string lpszFilename, uint fl, IntPtr pdv);
        private const uint FR_PRIVATE = 0x10;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIconW(int cmd, ref NOTIFYICONDATAW data);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadImageW(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public int    cbSize;
            public IntPtr hWnd;
            public int    uID;
            public int    uFlags;
            public int    uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int    dwState;
            public int    dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public int    uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int    dwInfoFlags;
            public Guid   guidItem;
            public IntPtr hBalloonIcon;
        }

        public App()
        {
            InitializeComponent();
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            RegisterCustomFonts();
            _seaDropService = new SeaDropService();
        }

        private void RegisterCustomFonts()
        {
            foreach (var name in new[] { "Inter-Regular.ttf", "Inter-SemiBold.ttf", "YoungSerif-Regular.ttf" })
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", name);
                if (File.Exists(path))
                    AddFontResourceEx(path, FR_PRIVATE, IntPtr.Zero);
            }
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // ── Step 1: tray icon in grey state — ALWAYS first, before anything else ──
            CreateTrayIcon(TrayState.Grey);

            // Wire service events before starting
            _seaDropService.OnStatusChanged += s =>
            {
                Debug.WriteLine($"[SeaDrop] {s}");
                // While service is starting/connecting, tray stays amber
                if (_trayState != TrayState.Green && _trayState != TrayState.OrangeRing)
                    SetTrayState(TrayState.Amber, s);
            };
            _seaDropService.OnConnectionChanged += connected =>
            {
                Debug.WriteLine($"[SeaDrop] {(connected ? "Connected" : "Disconnected")}");
                SetTrayState(
                    connected ? TrayState.Green : TrayState.Grey,
                    connected ? "SeaDrop connected" : "SeaDrop offline");
            };
            _seaDropService.OnFileSent += (name, size) =>
            {
                _priorState = _trayState;
                SetTrayState(TrayState.OrangeRing, $"Sending {name}…");
            };
            _seaDropService.OnFileReceived += (name, path) =>
            {
                Debug.WriteLine($"[SeaDrop] Received {name} → {path}");
                // Restore state after transfer
                SetTrayState(_priorState == TrayState.OrangeRing ? TrayState.Green : _priorState,
                    "SeaDrop connected");
            };
            _seaDropService.OnPermissionDenied += () =>
            {
                Debug.WriteLine("[SeaDrop] WiFi permission denied");
                SetTrayState(TrayState.Grey, "SeaDrop: location permission needed");
                _dispatcherQueue.TryEnqueue(async () =>
                {
                    if (_mainWindow is MainWindow mw)
                        await mw.ShowPermissionBannerAsync();
                });
            };
            _seaDropService.OnSeaDropAvailable += available =>
            {
                Debug.WriteLine($"[SeaDrop] ESP32 {(available ? "online" : "offline")}");
                if (!available)
                    SetTrayState(TrayState.Grey, "SeaDrop offline — searching…");
            };

            // ── Steps 2-7: run on a background task; never block the UI thread ──
            _ = InitializeAsync(args?.Arguments ?? "");
        }

        private async Task InitializeAsync(string arguments)
        {
            // SeaDropService.StartAsync internally enforces the mandatory gate sequence:
            //   2. WiFiAdapter.RequestAccessAsync          → OnPermissionDenied if denied
            //   3. HotspotManager.StartAsync              → only if wizard completed
            //   4. BleWatcher.Start
            //   5. TcpInboundListener.StartAsync          → listens on hotspot IF
            //   6. HELLO_ACK received → CHANNEL <n> sent  → OnConnectionChanged(true)
            //   7. OutboundQueue resumed
            await _seaDropService.StartAsync();

            // Show window on launch only if not yet registered (first run wizard).
            // On subsequent launches: no window — app lives in the tray.
            var registered = _seaDropService.IsRegistered();
            var wizardDone = _seaDropService.IsWizardCompleted();

            if (!registered || !wizardDone)
            {
                _dispatcherQueue.TryEnqueue(ShowWizard);
            }
            // else: no window — user must right-click tray → Settings

            if (!string.IsNullOrEmpty(arguments))
            {
                var filePath = arguments.Split(' ')[0].Trim('"');
                if (File.Exists(filePath) || Directory.Exists(filePath))
                    _seaDropService.EnqueueFileForSend(filePath);
            }
        }

        // ── Tray state management ─────────────────────────────────────────────

        internal void SetTrayState(TrayState state, string? tooltip = null)
        {
            _trayState = state;
            _nid.hIcon   = BuildTrayIcon(state);
            _nid.szTip   = tooltip ?? StateTooltip(state);
            _nid.cbSize  = Marshal.SizeOf<NOTIFYICONDATAW>();
            Shell_NotifyIconW(NIM_MODIFY, ref _nid);
        }

        private static string StateTooltip(TrayState s) => s switch
        {
            TrayState.Grey       => "SeaDrop — not connected",
            TrayState.Amber      => "SeaDrop — connecting…",
            TrayState.Green      => "SeaDrop — connected",
            TrayState.OrangeRing => "SeaDrop — transferring…",
            _                    => "SeaDrop",
        };

        // ── Tray icon creation ────────────────────────────────────────────────

        private void CreateTrayIcon(TrayState initialState)
        {
            _trayWindow = new NativeWindow(OnTrayMessage);

            _nid = new NOTIFYICONDATAW
            {
                cbSize          = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd            = _trayWindow.Handle,
                uID             = 0,
                uFlags          = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage= WM_TRAYICON,
                hIcon           = BuildTrayIcon(initialState),
                szTip           = StateTooltip(initialState),
            };

            Shell_NotifyIconW(NIM_ADD, ref _nid);
        }

        /// <summary>
        /// Builds a 16×16 GDI icon for each tray state programmatically.
        /// Grey  = #808080, Amber = #FFC000, Green = #22C55E, OrangeRing = #E85D00 (ring).
        /// Falls back to the bundled .ico if GDI fails.
        /// </summary>
        private IntPtr BuildTrayIcon(TrayState state)
        {
            // Try bundled .ico first for Grey (default) — gives proper multi-res icon
            if (state == TrayState.Grey)
            {
                var icoPath = Path.Combine(AppContext.BaseDirectory, "SeaDrop.ico");
                if (File.Exists(icoPath))
                {
                    const uint LR_LOADFROMFILE = 0x0010;
                    var h = LoadImageW(IntPtr.Zero, icoPath, 1, 16, 16, LR_LOADFROMFILE);
                    if (h != IntPtr.Zero) return h;
                }
            }

            return _trayWindow!.CreateColorIcon(state switch
            {
                TrayState.Grey       => (0x80, 0x80, 0x80),
                TrayState.Amber      => (0xFF, 0xC0, 0x00),
                TrayState.Green      => (0x22, 0xC5, 0x5E),
                TrayState.OrangeRing => (0xE8, 0x5D, 0x00),
                _                    => (0x80, 0x80, 0x80),
            }, ring: state == TrayState.OrangeRing);
        }

        // ── Tray message handler ─────────────────────────────────────────────

        private IntPtr OnTrayMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_TRAYICON)
            {
                switch ((int)lParam & 0xFFFF)
                {
                    case WM_LBUTTONDOWN:
                        _dispatcherQueue.TryEnqueue(ToggleMainWindow);
                        break;
                    case WM_RBUTTONDOWN:
                        _dispatcherQueue.TryEnqueue(ShowContextMenu);
                        break;
                }
            }
            return IntPtr.Zero;
        }

        private void ShowContextMenu()
        {
            var ssid    = _seaDropService.GetHotspotSsid();
            var version = _seaDropService.AppVersion;

            var menu = new TrayContextMenu();
            menu.AddItem("Send a file…",     () => _ = OpenSendFilePickerAsync());
            menu.AddSeparator();
            menu.AddDisabledItem($"SeaDrop connected — v{version}");
            menu.AddSeparator();
            menu.AddItem("Settings",          () => ShowMainWindow());
            menu.AddItem("Quit",              () => ExitApp());
            menu.Show(_trayWindow!.Handle);
        }

        private async Task OpenSendFilePickerAsync()
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _trayWindow!.Handle);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add("*");
            var file = await picker.PickSingleFileAsync();
            if (file != null)
                _seaDropService.EnqueueFileForSend(file.Path);
        }

        // ── Window management ─────────────────────────────────────────────────

        private void ShowWizard()
        {
            var wizard = new WizardWindow(_seaDropService);
            var hwnd   = WindowNative.GetWindowHandle(wizard);
            var id     = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var aw     = AppWindow.GetFromWindowId(id);

            aw.Closing += (s, e) =>
            {
                if (!_isExiting) { e.Cancel = true; s.Hide(); }
            };
            wizard.Activate();
        }

        private void ToggleMainWindow()
        {
            if (_mainWindow != null && IsWindowVisible())
                HideMainWindow();
            else
                ShowMainWindow();
        }

        internal void ShowMainWindow()
        {
            if (_mainWindow == null)
            {
                _mainWindow = new MainWindow(_seaDropService);
                var hwnd = WindowNative.GetWindowHandle(_mainWindow);
                var id   = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                var aw   = AppWindow.GetFromWindowId(id);

                aw.Closing += (s, e) =>
                {
                    if (!_isExiting) { e.Cancel = true; s.Hide(); }
                };
            }

            _mainWindow.Activate();
            var h2  = WindowNative.GetWindowHandle(_mainWindow);
            var id2 = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(h2);
            var aw2 = AppWindow.GetFromWindowId(id2);
            if (!aw2.IsVisible) aw2.Show(true);
        }

        internal void HideMainWindow()
        {
            if (_mainWindow == null) return;
            try
            {
                var hwnd = WindowNative.GetWindowHandle(_mainWindow);
                var id   = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                AppWindow.GetFromWindowId(id).Hide();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] HideMainWindow: {ex.Message}");
            }
        }

        private bool IsWindowVisible()
        {
            try
            {
                if (_mainWindow == null) return false;
                var hwnd = WindowNative.GetWindowHandle(_mainWindow);
                var id   = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                return AppWindow.GetFromWindowId(id).IsVisible;
            }
            catch { return false; }
        }

        // ── Exit ─────────────────────────────────────────────────────────────

        private async void ExitApp()
        {
            _isExiting = true;
            _nid.cbSize = Marshal.SizeOf<NOTIFYICONDATAW>();
            Shell_NotifyIconW(NIM_DELETE, ref _nid);
            _trayWindow?.Dispose();
            _mainWindow?.Close();
            await _seaDropService.StopAsync();
            Current.Exit();
        }

        public static void EnqueueFile(string filePath)
        {
            if (Current is App app)
                app._seaDropService.EnqueueFileForSend(filePath);
        }
    }

    // ── Native helper: hidden Win32 message-only window ──────────────────────

    internal class NativeWindow : IDisposable
    {
        public IntPtr Handle { get; private set; }
        private readonly Func<uint, IntPtr, IntPtr, IntPtr> _wndProc;
        private readonly WndProcDelegate _nativeWndProc;

        public NativeWindow(Func<uint, IntPtr, IntPtr, IntPtr> wndProc)
        {
            _wndProc       = wndProc;
            _nativeWndProc = WndProc;

            var hInstance = Process.GetCurrentProcess().Handle;
            var className = "SeaDropTray_" + Guid.NewGuid().ToString("N");

            var wc = new WNDCLASSW
            {
                lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_nativeWndProc),
                hInstance      = hInstance,
                lpszClassName  = className,
                hbrBackground  = IntPtr.Zero,
            };
            RegisterClassW(ref wc);

            Handle = CreateWindowExW(0, className, "SeaDrop",
                0, 0, 0, 0, 0,
                new IntPtr(-3), IntPtr.Zero, hInstance, IntPtr.Zero);
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
            => _wndProc(msg, wParam, lParam);

        // ── Icon factory ──────────────────────────────────────────────────────

        /// <summary>
        /// Creates a 16×16 solid-colour icon, or a ring shape when <paramref name="ring"/> is true.
        /// Colours are passed as (R, G, B) bytes.
        /// </summary>
        public IntPtr CreateColorIcon((byte r, byte g, byte b) color, bool ring = false)
        {
            const int W = 16, H = 16;
            var hdcScreen = GetDC(IntPtr.Zero);
            var hdcMem    = CreateCompatibleDC(hdcScreen);
            var hBitmap   = CreateCompatibleBitmap(hdcScreen, W, H);
            SelectObject(hdcMem, hBitmap);

            // Fill background black (transparent in mask)
            var bgBrush = CreateSolidBrush(0x00000000);
            var bgRect  = new RECT { left = 0, top = 0, right = W, bottom = H };
            FillRect(hdcMem, ref bgRect, bgBrush);
            DeleteObject(bgBrush);

            int gdiColor = (color.r) | (color.g << 8) | (color.b << 16);
            var fgBrush  = CreateSolidBrush(gdiColor);

            if (!ring)
            {
                // Solid circle: centre 8×8, radius 6
                var ellipseRect = new RECT { left = 1, top = 1, right = W - 1, bottom = H - 1 };
                SelectObject(hdcMem, fgBrush);
                var nullPen = GetStockObject(5); // NULL_PEN
                SelectObject(hdcMem, nullPen);
                Ellipse(hdcMem, 1, 1, W - 1, H - 1);
            }
            else
            {
                // Ring: draw thick arc (outer 14px circle minus inner 8px circle)
                SelectObject(hdcMem, fgBrush);
                var nullPen = GetStockObject(5);
                SelectObject(hdcMem, nullPen);
                Ellipse(hdcMem, 1, 1, W - 1, H - 1);

                // Punch a hole in the centre with black
                var holeBrush = CreateSolidBrush(0x00000000);
                SelectObject(hdcMem, holeBrush);
                Ellipse(hdcMem, 4, 4, W - 4, H - 4);
                DeleteObject(holeBrush);
            }

            DeleteObject(fgBrush);

            // Build monochrome mask (all zeros = fully opaque icon)
            var mask  = new byte[((W + 15) / 16) * 2 * H];
            var hMask = CreateBitmap(W, H, 1, 1, mask);

            var info = new ICONINFO { fIcon = true, hbmColor = hBitmap, hbmMask = hMask };
            var icon = CreateIconIndirect(ref info);

            DeleteObject(hBitmap);
            DeleteObject(hMask);
            DeleteDC(hdcMem);
            ReleaseDC(IntPtr.Zero, hdcScreen);

            return icon;
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) { DestroyWindow(Handle); Handle = IntPtr.Zero; }
        }

        // ── P/Invoke declarations ─────────────────────────────────────────────
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
            IntPtr hInstance, IntPtr lpParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassW(ref WNDCLASSW wc);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateSolidBrush(int color);
        [DllImport("user32.dll")]
        private static extern bool FillRect(IntPtr hDC, ref RECT lprc, IntPtr hBrush);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, byte[]? lpvBits);
        [DllImport("user32.dll")]
        private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);
        [DllImport("gdi32.dll")]
        private static extern bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")]
        private static extern IntPtr GetStockObject(int fnObject);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSW { public int style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground; public string lpszMenuName; public string lpszClassName; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left; public int top; public int right; public int bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }
    }

    // ── Context menu wrapper ──────────────────────────────────────────────────

    internal class TrayContextMenu
    {
        private readonly System.Collections.Generic.List<(string text, int id, bool isSeparator, bool disabled)> _items = new();
        private readonly System.Collections.Generic.Dictionary<int, Action> _actions = new();
        private int _nextId = 1000;

        public void AddItem(string text, Action action)
        {
            var id = _nextId++;
            _items.Add((text, id, false, false));
            _actions[id] = action;
        }

        public void AddDisabledItem(string text)
        {
            _items.Add((text, _nextId++, false, true));
        }

        public void AddSeparator() => _items.Add(("", 0, true, false));

        public void Show(IntPtr hWnd)
        {
            var hMenu = CreatePopupMenu();
            foreach (var item in _items)
            {
                if (item.isSeparator)
                    AppendMenuW(hMenu, 0x0800, 0, "-");
                else if (item.disabled)
                    AppendMenuW(hMenu, 0x0001 /* MF_GRAYED */, item.id, item.text);
                else
                    AppendMenuW(hMenu, 0, item.id, item.text);
            }

            GetCursorPos(out var pt);
            SetForegroundWindow(hWnd);
            var cmdId = TrackPopupMenu(hMenu, 0x0100, pt.x, pt.y, 0, hWnd, IntPtr.Zero);
            if (_actions.TryGetValue(cmdId, out var act)) act();
            DestroyMenu(hMenu);
        }

        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr hMenu, int uFlags, int uIDNewItem, string lpNewItem);
        [DllImport("user32.dll")] private static extern int TrackPopupMenu(IntPtr hMenu, int uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);
        [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr hMenu);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }
    }
}
