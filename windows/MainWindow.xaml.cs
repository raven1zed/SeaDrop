using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;
using SeaDropWindows.SeaDrop.core;
using SeaDropWindows.SeaDrop.storage;

namespace SeaDropWindows
{
    public sealed partial class MainWindow : Window
    {
        private readonly SeaDropService _svc;
        private readonly CredentialStore _store;

        private static readonly SolidColorBrush Bg = Hex("#FEFEFE");
        private static readonly SolidColorBrush Surface = Hex("#FFFFFF");
        private static readonly SolidColorBrush Border = Hex("#E5E7EB");
        private static readonly SolidColorBrush Primary = Hex("#E85D00");
        private static readonly SolidColorBrush Text = Hex("#1F2937");
        private static readonly SolidColorBrush Text2 = Hex("#4B5563");
        private static readonly SolidColorBrush Text3 = Hex("#9CA3AF");
        private static readonly SolidColorBrush Subtle = Hex("#F3F4F6");
        private static readonly SolidColorBrush Green = Hex("#10B981");
        private static readonly SolidColorBrush Red = Hex("#EF4444");

        private TextBlock _statusLabel = null!;
        private TextBlock _wifiInfoLabel = null!;
        private TextBox _deviceNameBox = null!;
        private Button _saveNameBtn = null!;
        private TextBlock _ssidLabel = null!;
        private Button _ssidCopyBtn = null!;
        private TextBlock _passwordLabel = null!;
        private Button _passwordCopyBtn = null!;
        private TextBlock _folderLabel = null!;
        private Button _pickFolderBtn = null!;
        private Button _openFolderBtn = null!;
        private ToggleSwitch _contextMenuToggle = null!;
        private Button _reregisterBtn = null!;
        private TextBlock _versionLabel = null!;
        private Button _quitBtn = null!;

        public MainWindow(SeaDropService svc)
        {
            _svc = svc;
            _store = new CredentialStore();
            this.InitializeComponent();

            Title = "SeaDrop";
            Build();
            Hook();
            Refresh();
        }

        private void Build()
        {
            var root = new Grid
            {
                Background = Bg,
                RowDefinitions =
                {
                    new RowDefinition { Height = GridLength.Auto }, // menu + header
                    new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, // content
                    new RowDefinition { Height = GridLength.Auto }  // footer
                }
            };

            // ── TOP: Menu + Header ──────────────────────────────────
            var top = new Grid();
            top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            top.Background = Surface;

            // MenuBar
            var menu = new MenuBar();
            var fileMenu = new MenuBarItem { Title = "File" };
            var openFolderItem = new MenuFlyoutItem { Text = "Open received files folder" };
            openFolderItem.Click += (_, _) => OpenFolder();
            fileMenu.Items.Add(openFolderItem);
            fileMenu.Items.Add(new MenuFlyoutSeparator());
            var sendFileItem = new MenuFlyoutItem { Text = "Send a file..." };
            sendFileItem.Click += async (_, _) => await OpenSendFilePicker();
            fileMenu.Items.Add(sendFileItem);
            fileMenu.Items.Add(new MenuFlyoutSeparator());
            var quitItem = new MenuFlyoutItem { Text = "Quit" };
            quitItem.Click += (_, _) => Close();
            fileMenu.Items.Add(quitItem);
            menu.Items.Add(fileMenu);

            var editMenu = new MenuBarItem { Title = "Edit" };
            var copySsidItem = new MenuFlyoutItem { Text = "Copy hotspot SSID" };
            copySsidItem.Click += (_, _) => CopyToClipboard(_svc.GetHotspotSsid(), "SSID");
            editMenu.Items.Add(copySsidItem);
            var copyPassItem = new MenuFlyoutItem { Text = "Copy hotspot password" };
            copyPassItem.Click += (_, _) => CopyToClipboard(_svc.GetHotspotPass(), "Password");
            editMenu.Items.Add(copyPassItem);
            menu.Items.Add(editMenu);

            var viewMenu = new MenuBarItem { Title = "View" };
            var refreshItem = new MenuFlyoutItem { Text = "Refresh status" };
            refreshItem.Click += (_, _) => Refresh();
            viewMenu.Items.Add(refreshItem);
            menu.Items.Add(viewMenu);

            var helpMenu = new MenuBarItem { Title = "Help" };
            var aboutItem = new MenuFlyoutItem { Text = "About SeaDrop" };
            aboutItem.Click += async (_, _) => await ShowAboutAsync();
            helpMenu.Items.Add(aboutItem);
            menu.Items.Add(helpMenu);

            Grid.SetRow(menu, 0);
            top.Children.Add(menu);

            // Header
            var header = new Border
            {
                BorderBrush = Border,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(40, 24, 40, 24)
            };
            var headerStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 14,
                VerticalAlignment = VerticalAlignment.Center
            };
            var logo = new Border
            {
                Width = 36, Height = 36,
                CornerRadius = new CornerRadius(10),
                Background = Primary,
                Child = new TextBlock
                {
                    Text = "S",
                    FontFamily = new FontFamily("Young Serif"),
                    FontSize = 18,
                    Foreground = Hex("#FFFFFF"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            headerStack.Children.Add(logo);
            var titleBlock = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            titleBlock.Children.Add(new TextBlock
            {
                Text = "SeaDrop",
                FontFamily = new FontFamily("Young Serif"),
                FontSize = 20,
                Foreground = Text
            });
            _statusLabel = new TextBlock
            {
                Text = "Initializing...",
                FontFamily = new FontFamily("Inter"),
                FontSize = 12,
                Foreground = Text2
            };
            titleBlock.Children.Add(_statusLabel);
            headerStack.Children.Add(titleBlock);
            header.Child = headerStack;
            Grid.SetRow(header, 1);
            top.Children.Add(header);

            Grid.SetRow(top, 0);
            root.Children.Add(top);

            // ── CONTENT ─────────────────────────────────────────────
            var content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Disabled,
                Padding = new Thickness(40, 32, 40, 32)
            };

            var contentStack = new StackPanel { Spacing = 28, MaxWidth = 640 };

            // ── Section: Status ─────────────────────────────────────
            contentStack.Children.Add(SectionLabel("Status"));
            var statusBox = Group();
            _wifiInfoLabel = new TextBlock
            {
                FontFamily = new FontFamily("Inter"),
                FontSize = 13,
                Foreground = Text,
                TextWrapping = TextWrapping.Wrap
            };
            statusBox.Children.Add(_wifiInfoLabel);
            contentStack.Children.Add(Wrap(statusBox));

            // ── Section: This Device ────────────────────────────────
            contentStack.Children.Add(SectionLabel("This device"));
            var deviceBox = Group();

            // Editable device name with Save button
            var nameRow = new Grid();
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            nameRow.ColumnSpacing = 12;
            _deviceNameBox = new TextBox
            {
                Text = _svc.GetDeviceName(),
                FontFamily = new FontFamily("Inter"),
                FontSize = 13,
                Foreground = Text,
                Background = Subtle,
                BorderBrush = Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8)
            };
            Grid.SetColumn(_deviceNameBox, 0);
            nameRow.Children.Add(_deviceNameBox);

            _saveNameBtn = SecondaryBtn("Save");
            _saveNameBtn.Click += (_, _) =>
            {
                var n = _deviceNameBox.Text.Trim();
                if (!string.IsNullOrEmpty(n))
                {
                    _svc.SaveDeviceName(n);
                    _statusLabel.Text = "Saved";
                }
            };
            Grid.SetColumn(_saveNameBtn, 1);
            nameRow.Children.Add(_saveNameBtn);
            deviceBox.Children.Add(nameRow);

            deviceBox.Children.Add(Divider());

            // Hotspot SSID with copy button
            var ssidRow = new Grid();
            ssidRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ssidRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ssidRow.ColumnSpacing = 12;
            _ssidLabel = new TextBlock
            {
                Text = _svc.GetHotspotSsid(),
                FontFamily = new FontFamily("Inter SemiBold"),
                FontSize = 13,
                Foreground = Text,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(_ssidLabel, 0);
            ssidRow.Children.Add(_ssidLabel);
            _ssidCopyBtn = SecondaryBtn("Copy");
            _ssidCopyBtn.Click += (_, _) => CopyToClipboard(_svc.GetHotspotSsid(), "SSID");
            Grid.SetColumn(_ssidCopyBtn, 1);
            ssidRow.Children.Add(_ssidCopyBtn);
            deviceBox.Children.Add(Labeled("Hotspot SSID", ssidRow));

            deviceBox.Children.Add(Divider());

            // Hotspot password with copy button
            var passRow = new Grid();
            passRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            passRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            passRow.ColumnSpacing = 12;
            _passwordLabel = new TextBlock
            {
                Text = _svc.GetHotspotPass(),
                FontFamily = new FontFamily("Inter SemiBold"),
                FontSize = 13,
                Foreground = Text,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_passwordLabel, 0);
            passRow.Children.Add(_passwordLabel);
            _passwordCopyBtn = SecondaryBtn("Copy");
            _passwordCopyBtn.Click += (_, _) => CopyToClipboard(_svc.GetHotspotPass(), "Password");
            Grid.SetColumn(_passwordCopyBtn, 1);
            passRow.Children.Add(_passwordCopyBtn);
            deviceBox.Children.Add(Labeled("Hotspot password", passRow));

            contentStack.Children.Add(Wrap(deviceBox));

            // ── Section: Storage ────────────────────────────────────
            contentStack.Children.Add(SectionLabel("Storage"));
            var storageBox = Group();
            var folderRow = new Grid();
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            folderRow.ColumnSpacing = 12;
            _folderLabel = new TextBlock
            {
                Text = _store.GetReceiveFolder(),
                FontFamily = new FontFamily("Inter SemiBold"),
                FontSize = 13,
                Foreground = Text,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(_folderLabel, 0);
            folderRow.Children.Add(_folderLabel);
            _pickFolderBtn = SecondaryBtn("Change");
            _pickFolderBtn.Click += async (_, _) => await PickFolder();
            Grid.SetColumn(_pickFolderBtn, 1);
            folderRow.Children.Add(_pickFolderBtn);
            storageBox.Children.Add(Labeled("Received files save to", folderRow));

            _openFolderBtn = SecondaryBtn("Open folder");
            _openFolderBtn.HorizontalAlignment = HorizontalAlignment.Left;
            _openFolderBtn.Margin = new Thickness(0, 14, 0, 0);
            _openFolderBtn.Click += (_, _) => OpenFolder();
            storageBox.Children.Add(_openFolderBtn);
            contentStack.Children.Add(Wrap(storageBox));

            // ── Section: Behavior ───────────────────────────────────
            contentStack.Children.Add(SectionLabel("Behavior"));
            var behaviorBox = Group();
            behaviorBox.Children.Add(ToggleRow(
                "Start hotspot on launch",
                "Mobile Hotspot turns on automatically when SeaDrop opens.",
                _store.GetAutostartHotspot(),
                v => _store.SaveAutostartHotspot(v)).Panel);
            behaviorBox.Children.Add(Divider());
            var ctxRow = ToggleRow(
                "Show \"Send via SeaDrop\" in Explorer",
                "Right-click any file in Explorer to send it to your phone.",
                _store.GetContextMenuEnabled(),
                v => _store.SaveContextMenuEnabled(v));
            _contextMenuToggle = ctxRow.Toggle;
            behaviorBox.Children.Add(ctxRow.Panel);
            contentStack.Children.Add(Wrap(behaviorBox));

            // ── Section: Registration ───────────────────────────────
            contentStack.Children.Add(SectionLabel("Registration"));
            var regBox = Group();
            _reregisterBtn = PrimaryBtn("Re-register with SeaDrop");
            _reregisterBtn.HorizontalAlignment = HorizontalAlignment.Left;
            _reregisterBtn.Click += (_, _) => ReRegister();
            regBox.Children.Add(_reregisterBtn);
            contentStack.Children.Add(Wrap(regBox));

            // ── Section: About ──────────────────────────────────────
            contentStack.Children.Add(SectionLabel("About"));
            var aboutBox = Group();
            var aboutStack = new StackPanel { Spacing = 6 };
            aboutStack.Children.Add(new TextBlock
            {
                Text = "SeaDrop",
                FontFamily = new FontFamily("Young Serif"),
                FontSize = 16,
                Foreground = Text
            });
            _versionLabel = new TextBlock
            {
                FontFamily = new FontFamily("Inter"),
                FontSize = 12,
                Foreground = Text2
            };
            aboutStack.Children.Add(_versionLabel);
            aboutStack.Children.Add(new TextBlock
            {
                Text = "Transfers files between your Windows laptop and Android phone over WiFi — no internet required.",
                FontFamily = new FontFamily("Inter"),
                FontSize = 12,
                Foreground = Text2,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
            aboutBox.Children.Add(aboutStack);
            contentStack.Children.Add(Wrap(aboutBox));

            content.Content = contentStack;
            Grid.SetRow(content, 1);
            root.Children.Add(content);

            // ── FOOTER ──────────────────────────────────────────────
            var footer = new Border
            {
                Background = Surface,
                BorderBrush = Border,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(40, 14, 40, 14)
            };
            var footerStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _quitBtn = SecondaryBtn("Quit SeaDrop");
            _quitBtn.Click += (_, _) => Close();
            footerStack.Children.Add(_quitBtn);
            footer.Child = footerStack;
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var aw = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            aw.Resize(new Windows.Graphics.SizeInt32(820, 720));
        }

        private TextBlock SectionLabel(string t) => new TextBlock
        {
            Text = t.ToUpperInvariant(),
            FontFamily = new FontFamily("Inter SemiBold"),
            FontSize = 11,
            Foreground = Text3,
            CharacterSpacing = 80,
            Margin = new Thickness(2, 0, 0, 0)
        };

        private StackPanel Group() => new StackPanel { Spacing = 0 };

        private Border Wrap(StackPanel inner) => new Border
        {
            Background = Surface,
            BorderBrush = Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Child = inner
        };

        private Border Divider() => new Border
        {
            Height = 1, Background = Border, Margin = new Thickness(0, 14, 0, 14)
        };

        private Grid Labeled(string label, FrameworkElement content)
        {
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowSpacing = 4;
            g.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily("Inter"),
                FontSize = 12,
                Foreground = Text2
            });
            Grid.SetRow(content, 1);
            g.Children.Add(content);
            return g;
        }

        private class ToggleRowResult
        {
            public StackPanel Panel { get; set; } = new StackPanel();
            public ToggleSwitch Toggle { get; set; } = new ToggleSwitch();
        }

        private ToggleRowResult ToggleRow(string title, string subtitle, bool initial, Action<bool> onChange)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Stretch };
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 460 };
            text.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = new FontFamily("Inter SemiBold"),
                FontSize = 13,
                Foreground = Text
            });
            text.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontFamily = new FontFamily("Inter"),
                FontSize = 12,
                Foreground = Text2,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            var toggle = new ToggleSwitch { IsOn = initial, OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) };
            toggle.Toggled += (_, _) => onChange(toggle.IsOn);
            panel.Children.Add(text);
            panel.Children.Add(toggle);
            return new ToggleRowResult { Panel = panel, Toggle = toggle };
        }

        private Button PrimaryBtn(string t) => new Button
        {
            Content = TxtBtn(t, Hex("#FFFFFF")),
            Background = Primary,
            MinHeight = 38,
            Padding = new Thickness(20, 8, 20, 8),
            CornerRadius = new CornerRadius(10)
        };

        private Button SecondaryBtn(string t) => new Button
        {
            Content = TxtBtn(t, Text),
            Background = Subtle,
            BorderThickness = new Thickness(0),
            MinHeight = 32,
            Padding = new Thickness(14, 6, 14, 6),
            CornerRadius = new CornerRadius(8)
        };

        private static TextBlock TxtBtn(string t, Brush fg) => new TextBlock
        {
            Text = t,
            FontFamily = new FontFamily("Inter SemiBold"),
            FontSize = 13,
            Foreground = fg
        };

        private void CopyToClipboard(string text, string what)
        {
            try
            {
                var pkg = new DataPackage();
                pkg.SetText(text ?? string.Empty);
                Clipboard.SetContent(pkg);
                _statusLabel.Text = $"{what} copied";
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Copy failed: " + ex.Message;
            }
        }

        private async Task PickFolder()
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
                picker.FileTypeFilter.Add("*");
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null)
                {
                    _folderLabel.Text = folder.Path;
                    _store.SaveReceiveFolder(folder.Path);
                    _statusLabel.Text = "Receive folder updated";
                }
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Folder pick failed: " + ex.Message;
            }
        }

        private void OpenFolder()
        {
            try
            {
                var path = _store.GetReceiveFolder();
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Open folder failed: " + ex.Message;
            }
        }

        private async Task OpenSendFilePicker()
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                picker.FileTypeFilter.Add("*");
                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    _svc.EnqueueFileForSend(file.Path);
                    _statusLabel.Text = $"Queued {file.Name}";
                }
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Send failed: " + ex.Message;
            }
        }

        private void ReRegister()
        {
            try
            {
                var wizard = new WizardWindow(_svc);
                wizard.Activate();
                Close();
            }
            catch (Exception ex)
            {
                _statusLabel.Text = "Re-register failed: " + ex.Message;
            }
        }

        private async Task ShowAboutAsync()
        {
            var dialog = new ContentDialog
            {
                Title = "SeaDrop",
                Content = "Seamless Drop\nVersion 1.5.0\n\nTransfers files between your Windows laptop and Android phone over WiFi — no internet required.\n\nSpecification: SeaDrop v1.5",
                CloseButtonText = "Close",
                XamlRoot = ((FrameworkElement)Content).XamlRoot
            };
            await dialog.ShowAsync();
        }

        private void Hook()
        {
            _svc.OnConnectionChanged += c => DispatcherQueue.TryEnqueue(() =>
            {
                if (_statusLabel == null) return;
                _statusLabel.Text = c ? "Connected" : "Disconnected";
                _statusLabel.Foreground = c ? Green : Red;
            });
            _svc.OnSeaDropAvailable += available => DispatcherQueue.TryEnqueue(() =>
            {
                if (_statusLabel == null) return;
                _statusLabel.Text = available ? "Connected to SeaDrop" : "Using primary WiFi";
                _statusLabel.Foreground = available ? Green : Red;
            });
            _svc.OnPermissionDenied += () => DispatcherQueue.TryEnqueue(() => _ = ShowPermissionBannerAsync());
        }

        public async Task ShowPermissionBannerAsync()
        {
            if (Content is not FrameworkElement fe || fe.XamlRoot == null) return;
            var dialog = new ContentDialog
            {
                Title = "Location Permission Required",
                Content = "SeaDrop needs location access to manage your WiFi connection. Grant permission in Windows Settings → Privacy → Location.",
                CloseButtonText = "OK",
                XamlRoot = fe.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private void Refresh()
        {
            if (_deviceNameBox != null) _deviceNameBox.Text = _svc.GetDeviceName();
            if (_ssidLabel != null) _ssidLabel.Text = _svc.GetHotspotSsid();
            if (_passwordLabel != null) _passwordLabel.Text = _svc.GetHotspotPass();
            if (_wifiInfoLabel != null)
            {
                try
                {
                    var conn = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                    var ssid = conn?.ProfileName ?? "Not connected";
                    _wifiInfoLabel.Text = $"Internet: {ssid}\nHotspot: {(_svc.GetHotspotSsid().Length > 0 ? "configured" : "not configured")}\nReceive folder: {_store.GetReceiveFolder()}";
                }
                catch
                {
                    _wifiInfoLabel.Text = "Internet: unknown";
                }
            }
            if (_versionLabel != null)
            {
                var asm = Assembly.GetExecutingAssembly().GetName();
                _versionLabel.Text = $"Version {asm.Version}";
            }
        }

        private static SolidColorBrush Hex(string h)
        {
            h = h.TrimStart('#');
            if (h.Length == 3) h = $"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}";
            if (h.Length == 8)
                return new SolidColorBrush(Windows.UI.Color.FromArgb(
                    Convert.ToByte(h.Substring(0, 2), 16),
                    Convert.ToByte(h.Substring(2, 2), 16),
                    Convert.ToByte(h.Substring(4, 2), 16),
                    Convert.ToByte(h.Substring(6, 2), 16)));
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255,
                Convert.ToByte(h.Substring(0, 2), 16),
                Convert.ToByte(h.Substring(2, 2), 16),
                Convert.ToByte(h.Substring(4, 2), 16)));
        }
    }
}
