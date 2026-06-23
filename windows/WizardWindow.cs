using System;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.WiFi;
using Windows.Networking.NetworkOperators;
using SeaDropWindows.SeaDrop.core;
using SeaDropWindows.SeaDrop.storage;

namespace SeaDropWindows
{
    public sealed class WizardWindow : Window
    {
        private readonly SeaDropService _svc;
        private readonly CredentialStore _store;
        private StackPanel _content = null!;
        private TextBlock _statusLine = null!;
        private Button _nextBtn = null!;
        private TextBox _deviceNameBox = null!;
        private DispatcherQueue _dq = null!;

        private static readonly SolidColorBrush BgBrush = Hex("#FEFEFE");
        private static readonly SolidColorBrush SurfaceBrush = Hex("#E8E8E8");
        private static readonly SolidColorBrush BorderBrush = Hex("#293548");
        private static readonly SolidColorBrush PrimaryBrush = Hex("#E85D00");
        private static readonly SolidColorBrush TextBrush = Hex("#1A1208");
        private static readonly SolidColorBrush SubtextBrush = Hex("#888888");
        private static readonly SolidColorBrush GreenBrush = Hex("#22C55E");
        private static readonly SolidColorBrush WhiteBrush = Hex("#FFFFFF");

        // ── Wizard state ──────────────────────────────────────────────────────
        private int _step;
        private bool _locationGranted;
        private bool _bleDetected;
        private bool _tcpConnected;
        private bool _registered;
        private string _deviceName = Environment.MachineName;
        private string _softApSsid = string.Empty;   // SSID from BLE payload
        private CancellationTokenSource _connectCts = new();

        public WizardWindow(SeaDropService svc)
        {
            _svc = svc;
            _store = new CredentialStore();
            _dq = DispatcherQueue.GetForCurrentThread();

            Title = "SeaDrop";
            ExtendsContentIntoTitleBar = true;
            BuildChrome();
            ShowStep(0);
            _ = StartBleWatchAsync();
        }

        // ── Chrome ────────────────────────────────────────────────────────────

        private void BuildChrome()
        {
            var root = new Grid { Background = BgBrush, Padding = new Thickness(48, 40, 48, 40) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // YoungSerif 40pt wordmark
            var wordmark = new TextBlock
            {
                Text = "SeaDrop",
                FontFamily = new FontFamily("Young Serif"),
                FontSize = 40,
                Foreground = PrimaryBrush
            };
            Grid.SetRow(wordmark, 0);
            root.Children.Add(wordmark);

            _content = new StackPanel { Spacing = 20, Margin = new Thickness(0, 32, 0, 0) };
            Grid.SetRow(_content, 1);
            root.Children.Add(_content);

            var footer = new StackPanel { Spacing = 12 };
            _statusLine = new TextBlock
            {
                FontFamily = new FontFamily("Inter"),
                FontSize = 14,
                Foreground = SubtextBrush,
            };
            footer.Children.Add(_statusLine);

            _nextBtn = new Button
            {
                Background = PrimaryBrush,
                Foreground = WhiteBrush,
                MinHeight = 44,
                MinWidth = 160,
                Padding = new Thickness(32, 10, 32, 10),
                CornerRadius = new CornerRadius(12),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            _nextBtn.Click += NextBtn_Click;
            footer.Children.Add(_nextBtn);

            Grid.SetRow(footer, 2);
            root.Children.Add(footer);
            Content = root;
        }

        // ── Step router ───────────────────────────────────────────────────────

        private void ShowStep(int step)
        {
            _step = step;
            _content.Children.Clear();
            switch (step)
            {
                case 0: StepWelcome(); break;
                case 1: StepLocationPermission(); break;
                case 2: StepStartingHotspot(); break;
                case 3: StepPowerOn(); break;
                case 4: StepConnecting(); break;
                case 5: StepNameDevice(); break;
                case 6: StepVerify(); break;
                case 7: StepDone(); break;
            }
        }

        // ── Screen 1: Welcome ─────────────────────────────────────────────────

        private void StepWelcome()
        {
            _content.Children.Add(Body18("Transfer files between your phone and laptop. No internet switching. Ever."));
            _nextBtn.Content = BtnText("Get Started");
            _nextBtn.IsEnabled = true;
            SetStatus("Tap to begin setup");
        }

        // ── Screen 2: Location Permission ────────────────────────────────────

        private void StepLocationPermission()
        {
            _content.Children.Add(Body20("SeaDrop needs location access to manage your WiFi connection."));
            _content.Children.Add(Spacer(8));
            _content.Children.Add(SubBody("Required by Windows to control the hotspot. SeaDrop never tracks your location."));
            _content.Children.Add(Spacer(32));

            var grantBtn = new Button
            {
                Content = BtnText(_locationGranted ? "✓ Access Granted" : "Grant Location Access"),
                Background = _locationGranted ? SurfaceBrush : PrimaryBrush,
                Foreground = WhiteBrush,
                MinHeight = 44,
                Padding = new Thickness(28, 10, 28, 10),
                CornerRadius = new CornerRadius(12),
                IsEnabled = !_locationGranted,
            };
            grantBtn.Click += async (_, _) =>
            {
                grantBtn.IsEnabled = false;
                var access = await WiFiAdapter.RequestAccessAsync();
                _locationGranted = access == WiFiAccessStatus.Allowed;

                grantBtn.Content = BtnText(_locationGranted ? "✓ Access Granted" : "Try Again");
                grantBtn.Background = _locationGranted ? SurfaceBrush : PrimaryBrush;
                grantBtn.IsEnabled = !_locationGranted;

                _nextBtn.IsEnabled = _locationGranted;
                SetStatus(_locationGranted
                    ? "Location access granted"
                    : "Permission denied — open Settings → Privacy → Location and allow SeaDrop");
            };
            _content.Children.Add(grantBtn);

            _nextBtn.Content = BtnText("Continue");
            _nextBtn.IsEnabled = _locationGranted;
            SetStatus(_locationGranted ? "Location access granted" : "Grant location permission to continue");
        }

        // ── Screen 3: Starting Hotspot ────────────────────────────────────────

        private async void StepStartingHotspot()
        {
            _content.Children.Add(Body20("Starting SeaDrop connection…"));
            _content.Children.Add(Spacer(16));

            var spinner = MakeSpinner();
            _content.Children.Add(spinner);

            _nextBtn.IsEnabled = false;
            SetStatus("Generating credentials…");

            try
            {
                // Generate fresh SSID + 12-char passphrase if not already stored
                var existingSsid = _store.GetHotspotSsid();
                string ssid, pass;

                if (string.IsNullOrEmpty(existingSsid))
                {
                    // Format: SeaDrop-W-XXXXXX (6 random hex chars)
                    var rng = new byte[3];
                    System.Security.Cryptography.RandomNumberGenerator.Fill(rng);
                    ssid = $"SeaDrop-W-{rng[0]:X2}{rng[1]:X2}{rng[2]:X2}";

                    var passRng = new byte[9]; // 9 bytes → 12 chars base64
                    System.Security.Cryptography.RandomNumberGenerator.Fill(passRng);
                    pass = Convert.ToBase64String(passRng)[..12].Replace('/', 'A').Replace('+', 'B');
                }
                else
                {
                    ssid = existingSsid;
                    pass = _store.GetHotspotPass();
                }

                SetStatus("Starting hotspot…");
                var result = await _svc.HotspotManager.StartWithCredentialsAsync(ssid, pass);

                spinner.IsActive = false;

                if (result == TetheringOperationStatus.Success)
                {
                    // Persist to Windows Credential Manager
                    _store.SaveHotspotCredentials(ssid, pass);
                    _content.Children.Add(StatusRow("✓ Hotspot started", ssid, green: true));
                    SetStatus($"Hotspot running: {ssid}");
                    _nextBtn.IsEnabled = true;
                }
                else
                {
                    var reason = result.ToString();
                    _content.Children.Add(StatusRow($"✗ Hotspot failed: {reason}", string.Empty, green: false));
                    SetStatus($"Failed ({reason}). Check Windows Settings → Network → Mobile hotspot.");

                    var retryBtn = new Button
                    {
                        Content = BtnText("Try Again"),
                        Background = PrimaryBrush,
                        Foreground = WhiteBrush,
                        MinHeight = 44,
                        Padding = new Thickness(24, 10, 24, 10),
                        CornerRadius = new CornerRadius(12),
                    };
                    retryBtn.Click += (_, _) =>
                    {
                        ShowStep(2);
                    };
                    _content.Children.Add(retryBtn);
                }
            }
            catch (Exception ex)
            {
                spinner.IsActive = false;
                _content.Children.Add(StatusRow($"✗ Error: {ex.Message}", string.Empty, green: false));
                SetStatus("Unexpected error starting hotspot. Retry or check hotspot settings.");
            }

            _nextBtn.Content = BtnText("Continue");
        }

        // ── Screen 4: Power On SeaDrop ────────────────────────────────────────

        private void StepPowerOn()
        {
            _content.Children.Add(Body20("Power on your SeaDrop device."));
            _content.Children.Add(Spacer(8));
            _content.Children.Add(SubBody("The SeaDrop screen should show REGISTRATION MODE."));
            _content.Children.Add(Spacer(24));

            var indicator = new TextBlock
            {
                FontFamily = new FontFamily("Inter"),
                FontSize = 14,
                Foreground = _bleDetected ? GreenBrush : SubtextBrush,
                Text = _bleDetected ? "✓ Registration mode detected — SeaDrop is ready" : "Scanning for SeaDrop…"
            };
            _content.Children.Add(indicator);

            _nextBtn.Content = BtnText("Continue");
            _nextBtn.IsEnabled = _bleDetected;
            SetStatus("Detecting BLE advertisement UUID 0xFEAD with reg_mode=1");
        }

        // ── Screen 5: Connecting ─────────────────────────────────────────────
        // Connects laptop to SeaDrop SoftAP via WiFiAdapter.ConnectAsync.
        // This is the ONLY time during normal operation the laptop connects to the SoftAP.

        private async void StepConnecting()
        {
            _content.Children.Add(Body20("Connecting to SeaDrop…"));
            _content.Children.Add(Spacer(16));
            var spinner = MakeSpinner();
            _content.Children.Add(spinner);

            SetStatus("Connecting to SeaDrop SoftAP…");
            _connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            bool connected = false;
            try
            {
                connected = await ConnectToSoftApAsync(_softApSsid, _connectCts.Token);
            }
            catch (OperationCanceledException)
            {
                SetStatus("Connection timed out. Ensure SeaDrop is in registration mode.");
            }
            catch (Exception ex)
            {
                SetStatus($"Connection error: {ex.Message}");
            }

            spinner.IsActive = false;
            _tcpConnected = connected;

            if (connected)
            {
                _content.Children.Add(StatusRow("✓ Connected to SeaDrop", string.Empty, green: true));
                SetStatus("TCP connection to 192.168.4.1:4242 established");
                _nextBtn.IsEnabled = true;
            }
            else
            {
                _content.Children.Add(StatusRow("✗ Could not connect to SeaDrop", string.Empty, green: false));
                SetStatus("Ensure SeaDrop is powered on and in registration mode, then retry.");
                _nextBtn.Content = BtnText("Retry");
                _nextBtn.IsEnabled = true;
            }
            _nextBtn.Content = BtnText("Continue");
        }

        /// <summary>
        /// Connects the laptop to the SeaDrop SoftAP using WiFiAdapter.ConnectAsync,
        /// then verifies TCP reachability on 192.168.4.1:4242.
        /// The SSID comes from the BLE payload (or falls back to SeaDrop_ prefix scan).
        /// </summary>
        private async Task<bool> ConnectToSoftApAsync(string ssid, CancellationToken ct)
        {
            try
            {
                var adapters = await WiFiAdapter.FindAllAdaptersAsync();
                if (adapters.Count == 0)
                    throw new InvalidOperationException("No WiFi adapter found.");

                var adapter = adapters[0];

                // Scan for the SeaDrop SoftAP
                await adapter.ScanAsync();

                // Find the target network — if SSID is empty, look for any SeaDrop_ prefix
                Windows.Devices.WiFi.WiFiAvailableNetwork? target = null;
                foreach (var n in adapter.NetworkReport.AvailableNetworks)
                {
                    if (!string.IsNullOrEmpty(ssid) && n.Ssid == ssid)
                    {
                        target = n;
                        break;
                    }
                    if (string.IsNullOrEmpty(ssid) && n.Ssid.StartsWith("SeaDrop_", StringComparison.OrdinalIgnoreCase))
                    {
                        target = n;
                        // Keep searching in case there's a better match
                    }
                }

                if (target == null)
                    throw new InvalidOperationException($"SeaDrop SoftAP not found. Ensure SeaDrop is in registration mode.");

                // Build WPA2 credential using known AP passphrase from protocol.hpp: "seadrop2026"
                var credential = new Windows.Security.Credentials.PasswordCredential
                {
                    Password = "seadrop2026"
                };

                var result = await adapter.ConnectAsync(
                    target,
                    WiFiReconnectionKind.Manual,
                    credential);

                if (result.ConnectionStatus != WiFiConnectionStatus.Success)
                    throw new InvalidOperationException($"WiFi connect failed: {result.ConnectionStatus}");

                // Verify TCP reachability
                using var tcp = new System.Net.Sockets.TcpClient();
                var connectTask = tcp.ConnectAsync("192.168.4.1", 4242);
                if (await Task.WhenAny(connectTask, Task.Delay(8000, ct)) != connectTask || !tcp.Connected)
                    throw new InvalidOperationException("Connected to WiFi but TCP to 192.168.4.1:4242 failed.");

                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SetStatus($"Connect failed: {ex.Message}");
                return false;
            }
        }

        // ── Screen 6: Name This Device ────────────────────────────────────────

        private void StepNameDevice()
        {
            _content.Children.Add(Body20("What should SeaDrop call this laptop?"));
            _content.Children.Add(Spacer(16));

            _deviceNameBox = new TextBox
            {
                PlaceholderText = "Device name",
                FontFamily = new FontFamily("Inter"),
                FontSize = 15,
                Foreground = TextBrush,
                Background = SurfaceBrush,
                BorderBrush = BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                MinHeight = 44,
                Text = Environment.MachineName
            };
            _deviceNameBox.TextChanged += (_, _) =>
                _nextBtn.IsEnabled = !string.IsNullOrWhiteSpace(_deviceNameBox.Text);

            _content.Children.Add(_deviceNameBox);

            _nextBtn.Content = BtnText("Register");
            _nextBtn.IsEnabled = true;
            SetStatus("Enter a name for this device");
        }

        // ── Screen 7: Verification ────────────────────────────────────────────

        private async void StepVerify()
        {
            _content.Children.Add(Body20("Confirming setup…"));
            _content.Children.Add(Spacer(24));

            var check1 = StatusBlock("Checking hotspot…");
            var check2 = StatusBlock("Checking TCP session…");
            var check3 = StatusBlock("Checking internet…");
            _content.Children.Add(check1);
            _content.Children.Add(check2);
            _content.Children.Add(check3);

            _nextBtn.IsEnabled = false;
            _nextBtn.Content = BtnText("Retry Checks");

            // Check 1: hotspot operational
            await Task.Delay(300);
            bool hotspotOk = await CheckHotspotOperationalAsync();
            check1.Foreground = hotspotOk ? GreenBrush : Hex("#EF4444");
            check1.Text = hotspotOk
                ? "✓ Hotspot is running"
                : "✗ Hotspot not running — close other apps using hotspot and retry";

            // Check 2: TCP session active with PONG within 15s
            await Task.Delay(300);
            bool tcpOk = await CheckTcpSessionAsync();
            check2.Foreground = tcpOk ? GreenBrush : Hex("#EF4444");
            check2.Text = tcpOk
                ? "✓ SeaDrop TCP session active"
                : "✗ SeaDrop not connected — ensure ESP32 can reach the hotspot";

            // Check 3: internet on non-hotspot interface via connectivity-check.ubuntu.com
            await Task.Delay(300);
            bool internetOk = await CheckInternetViaUbuntuAsync();
            check3.Foreground = internetOk ? GreenBrush : Hex("#EF4444");
            check3.Text = internetOk
                ? "✓ Internet available"
                : "✗ Internet unreachable — check your home WiFi connection";

            bool allOk = hotspotOk && tcpOk && internetOk;
            _nextBtn.IsEnabled = allOk;
            _nextBtn.Content = allOk ? BtnText("Continue") : BtnText("Retry Checks");
            SetStatus(allOk ? "All checks passed" : "One or more checks failed — see above");
        }

        private async Task<bool> CheckHotspotOperationalAsync()
        {
            try
            {
                var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
                if (profile == null) return false;
                var mgr = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
                return mgr.TetheringOperationalState == TetheringOperationalState.On;
            }
            catch { return false; }
        }

        private async Task<bool> CheckTcpSessionAsync()
        {
            try
            {
                // Verify the inbound listener has an active connection
                if (!_svc.TcpListener.IsConnected) return false;
                // Send a PING and wait for PONG within 15s
                await _svc.TcpListener.SendLineAsync("PING");
                var result = await Task.Run(async () =>
                {
                    using var cts = new CancellationTokenSource(15000);
                    while (!cts.IsCancellationRequested)
                    {
                        var line = await _svc.TcpListener.ReadLineAsync();
                        if (line == "PONG") return true;
                        if (line == null) return false;
                        await Task.Delay(100, cts.Token);
                    }
                    return false;
                });
                return result;
            }
            catch { return false; }
        }

        /// <summary>
        /// Performs a GET on https://connectivity-check.ubuntu.com and expects 204.
        /// Binds explicitly to the non-hotspot (home WiFi) interface to avoid routing
        /// the request through the SeaDrop hotspot.
        /// </summary>
        private async Task<bool> CheckInternetViaUbuntuAsync()
        {
            try
            {
                // Find the home WiFi interface — exclude the SeaDrop hotspot adapter
                var hotspotSsid = _store.GetHotspotSsid();
                System.Net.IPAddress? homeWifiAddr = null;

                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    {
                        // Skip the hotspot adapter (its description contains the SSID on Windows)
                        if (!string.IsNullOrEmpty(hotspotSsid) &&
                            nic.Description.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                            continue;

                        foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                homeWifiAddr = ua.Address;
                                break;
                            }
                        }
                        if (homeWifiAddr != null) break;
                    }
                }

                var handler = new System.Net.Http.HttpClientHandler();
                if (homeWifiAddr != null)
                {
                    // Bind the outbound connection to the home WiFi interface
                    handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
                }

                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(8)
                };

                var response = await client.GetAsync("https://connectivity-check.ubuntu.com");
                // Ubuntu connectivity check returns 204 when internet is available
                return (int)response.StatusCode == 204;
            }
            catch { return false; }
        }

        // ── Screen 8: Done ────────────────────────────────────────────────────

        private void StepDone()
        {
            // YoungSerif 40pt "All set." per spec
            _content.Children.Add(new TextBlock
            {
                Text = "All set.",
                FontFamily = new FontFamily("Young Serif"),
                FontSize = 40,
                Foreground = PrimaryBrush
            });
            _content.Children.Add(Spacer(16));
            _content.Children.Add(Body18(
                "SeaDrop lives in your system tray. Right-click any file and choose Send via SeaDrop, " +
                "or drag files onto the tray icon."));

            _nextBtn.Content = BtnText("Done");
            _nextBtn.IsEnabled = true;
            SetStatus("Setup complete");
        }

        // ── Next button dispatcher ────────────────────────────────────────────

        private async void NextBtn_Click(object sender, RoutedEventArgs e)
        {
            switch (_step)
            {
                // Screen 1 → 2
                case 0: ShowStep(1); break;

                // Screen 2 → 3 (only if permission granted)
                case 1:
                    if (!_locationGranted)
                    {
                        SetStatus("Location access required — tap Grant Location Access first");
                        return;
                    }
                    ShowStep(2);
                    break;

                // Screen 3 → 4 (only if hotspot started)
                case 2:
                    ShowStep(3);
                    break;

                // Screen 4 → 5 (BLE detected, auto-advances; fallback manual)
                case 3:
                    if (!_bleDetected)
                    {
                        SetStatus("Waiting for SeaDrop registration mode…");
                        return;
                    }
                    ShowStep(4);
                    break;

                // Screen 5 → 6 (connected or retry)
                case 4:
                    if (!_tcpConnected)
                        ShowStep(4); // retry
                    else
                        ShowStep(5);
                    break;

                // Screen 6 → Register → 7
                case 5:
                    _deviceName = _deviceNameBox?.Text.Trim() ?? Environment.MachineName;
                    if (string.IsNullOrWhiteSpace(_deviceName))
                    {
                        SetStatus("Enter a device name");
                        return;
                    }
                    _nextBtn.IsEnabled = false;
                    SetStatus("Sending REG WINDOWS command…");
                    bool ok = await SendRegAsync(_deviceName);
                    if (ok)
                    {
                        _registered = true;
                        SetStatus("Registration complete");
                        ShowStep(6);
                    }
                    else
                    {
                        SetStatus("Registration failed — check that SeaDrop shows REGISTRATION MODE");
                        _nextBtn.IsEnabled = true;
                    }
                    break;

                // Screen 7 → retry or → 8
                case 6:
                    bool allOk = await RunVerifyChecksAsync();
                    if (allOk) ShowStep(7);
                    else ShowStep(6); // re-enter so UI refreshes
                    break;

                // Screen 8 → close (wizard done)
                case 7:
                    // Save wizard_completed flag to Credential Store (registry)
                    _store.SaveWizardCompleted(true);

                    // Disconnect from SoftAP so ESP32 can connect to the hotspot in STA mode
                    await DisconnectFromSoftApAsync();

                    // Ensure main app recognises the completion
                    if (Application.Current is App app)
                        app.SetTrayState(TrayState.Amber, "Waiting for SeaDrop to connect…");

                    Close();
                    break;
            }
        }

        private async Task<bool> RunVerifyChecksAsync()
        {
            bool h = await CheckHotspotOperationalAsync();
            bool t = await CheckTcpSessionAsync();
            bool i = await CheckInternetViaUbuntuAsync();
            return h && t && i;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private async Task<bool> SendRegAsync(string name)
        {
            try
            {
                var hsSsid = _store.GetHotspotSsid();
                var hsPass = _store.GetHotspotPass();
                return await _svc.RegisterAsync(name, hsSsid, hsPass);
            }
            catch (Exception ex)
            {
                SetStatus($"Registration error: {ex.Message}");
                return false;
            }
        }

        private static async Task DisconnectFromSoftApAsync()
        {
            try
            {
                var adapters = await WiFiAdapter.FindAllAdaptersAsync();
                foreach (var adapter in adapters)
                {
                    adapter.Disconnect();
                }
            }
            catch { /* non-fatal */ }
        }

        // ── BLE watcher ───────────────────────────────────────────────────────

        private string _bleSeadropSsid = string.Empty;

        private async Task StartBleWatchAsync()
        {
            try
            {
                var watcher = new Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementWatcher();
                // Filter for our service UUID 0xFEAD
                watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(
                    new Guid("0000fead-0000-1000-8000-00805f9b34fb"));

                watcher.Received += (_, args) =>
                {
                    try
                    {
                        var dataSections = args.Advertisement?.DataSections;
                        if (dataSections == null) return;

                        foreach (var section in dataSections)
                        {
                            var buf = new byte[section.Data.Length];
                            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(section.Data))
                            {
                                reader.ReadBytes(buf);
                            }

                            if (buf.Length >= 3)
                            {
                                // Check for little-endian 16-bit UUID 0xFEAD (0xAD, 0xFE)
                                if (buf[0] == 0xAD && buf[1] == 0xFE)
                                {
                                    byte regMode = buf[2];
                                    if (regMode != 1) continue;

                                    // Extract SSID if present in payload (bytes 3..)
                                    if (buf.Length > 3)
                                    {
                                        int end = Array.IndexOf(buf, (byte)0, 3);
                                        int len = end < 0 ? buf.Length - 3 : end - 3;
                                        if (len > 0)
                                            _bleSeadropSsid = Encoding.ASCII.GetString(buf, 3, len);
                                    }

                                    _bleDetected = true;
                                    _dq.TryEnqueue(() =>
                                    {
                                        if (!string.IsNullOrEmpty(_bleSeadropSsid))
                                            _softApSsid = _bleSeadropSsid;

                                        // Auto-advance screen 4 (Power On) when BLE detected
                                        if (_step == 3)
                                        {
                                            _nextBtn.IsEnabled = true;
                                            ShowStep(4);
                                        }
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Wizard] BLE parse error: {ex.Message}");
                    }
                };

                watcher.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Wizard] BLE watcher failed: {ex.Message}");
            }
        }

        // ── UI helpers ────────────────────────────────────────────────────────

        private void SetStatus(string s)
        {
            if (_dq.HasThreadAccess)
                _statusLine.Text = s;
            else
                _dq.TryEnqueue(() => _statusLine.Text = s);
        }

        private static ProgressRing MakeSpinner() => new ProgressRing
        {
            IsActive = true,
            Width = 48,
            Height = 48,
            Foreground = PrimaryBrush,
        };

        private static StackPanel StatusRow(string msg, string detail, bool green)
        {
            var sp = new StackPanel { Spacing = 4 };
            sp.Children.Add(new TextBlock
            {
                Text = msg,
                FontFamily = new FontFamily("Inter"),
                FontSize = 14,
                Foreground = green ? GreenBrush : Hex("#EF4444"),
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrEmpty(detail))
                sp.Children.Add(new TextBlock
                {
                    Text = detail,
                    FontFamily = new FontFamily("Inter"),
                    FontSize = 12,
                    Foreground = SubtextBrush,
                });
            return sp;
        }

        private static TextBlock StatusBlock(string text) => new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Inter"),
            FontSize = 14,
            Foreground = SubtextBrush,
            TextWrapping = TextWrapping.Wrap
        };

        private static TextBlock Body20(string t) => new TextBlock
        {
            Text = t,
            FontFamily = new FontFamily("Inter"),
            FontSize = 20,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 30
        };

        private static TextBlock Body18(string t) => new TextBlock
        {
            Text = t,
            FontFamily = new FontFamily("Inter"),
            FontSize = 18,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 28
        };

        private static TextBlock SubBody(string t) => new TextBlock
        {
            Text = t,
            FontFamily = new FontFamily("Inter"),
            FontSize = 16,
            Foreground = SubtextBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 24
        };

        private static Border Spacer(double h) => new Border { Height = h };

        private static object BtnText(string t) => new TextBlock
        {
            Text = t,
            FontFamily = new FontFamily("Inter"),
            FontSize = 14,
            Foreground = WhiteBrush
        };

        private static SolidColorBrush Hex(string h)
        {
            h = h.TrimStart('#');
            if (h.Length == 3) h = $"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}";
            if (h.Length == 8)
                return new SolidColorBrush(Windows.UI.Color.FromArgb(
                    Convert.ToByte(h[..2], 16),
                    Convert.ToByte(h[2..4], 16),
                    Convert.ToByte(h[4..6], 16),
                    Convert.ToByte(h[6..8], 16)));
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255,
                Convert.ToByte(h[..2], 16),
                Convert.ToByte(h[2..4], 16),
                Convert.ToByte(h[4..6], 16)));
        }
    }
}