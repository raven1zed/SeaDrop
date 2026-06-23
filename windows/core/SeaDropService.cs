using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.WiFi;
using SeaDropWindows.SeaDrop.network;
using SeaDropWindows.SeaDrop.transfer;
using SeaDropWindows.SeaDrop.stp;
using SeaDropWindows.SeaDrop.notification;
using SeaDropWindows.SeaDrop.registration;
using SeaDropWindows.SeaDrop.storage;

namespace SeaDropWindows.SeaDrop.core
{
    public class SeaDropService
    {
        // ── Sub-services ───────────────────────────────────────────────────────
        private readonly HotspotManager        _hotspotManager;
        private readonly BleWatcher            _bleWatcher;
        private readonly TcpInboundListener    _tcpListener;
        private readonly TransferManager       _transferManager;
        private readonly ToastHelper           _notificationHelper;
        private readonly RegistrationManager   _registrationManager;
        private readonly CredentialStore       _credentialStore;

        // ── Runtime state ──────────────────────────────────────────────────────
        private bool _isRunning;
        private CancellationTokenSource _cts = new();
        private CancellationTokenSource _failoverCts = new();

        // ── Session data from HELLO_ACK ────────────────────────────────────────
        /// <summary>Session ID assigned by the ESP32 in HELLO_ACK &lt;session_id&gt;.</summary>
        public string SessionId      { get; private set; } = string.Empty;
        /// <summary>Firmware version string, if the ESP32 sends it in HELLO_ACK.</summary>
        public string FirmwareVersion { get; private set; } = "—";
        /// <summary>Last connected Android device name from HELLO ANDROID &lt;name&gt;.</summary>
        public string AndroidDeviceName { get; private set; } = "—";

        /// <summary>Assembly version of this Windows app.</summary>
        public string AppVersion { get; } =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.5.0";

        // ── Events ─────────────────────────────────────────────────────────────
        public event Action<string>?       OnStatusChanged;
        public event Action<string, long>? OnFileSent;
        public event Action<string, string>? OnFileReceived;
        public event Action<bool>?         OnConnectionChanged;
        public event Action?               OnPermissionDenied;
        public event Action<bool>?         OnInternetAvailable;
        public event Action<bool>?         OnSeaDropAvailable;

        public SeaDropService()
        {
            _credentialStore      = new CredentialStore();
            _hotspotManager       = new HotspotManager(_credentialStore);
            _bleWatcher           = new BleWatcher();
            _tcpListener          = new TcpInboundListener(_credentialStore);
            _transferManager      = new TransferManager(_tcpListener);
            _notificationHelper   = new ToastHelper();
            _registrationManager  = new RegistrationManager(_credentialStore);

            WireEvents();
        }

        private void WireEvents()
        {
            _hotspotManager.OnStatusChanged  += s => OnStatusChanged?.Invoke(s);

            _bleWatcher.OnDeviceFound        += (name, rssi) =>
                OnStatusChanged?.Invoke($"BLE: {name} RSSI={rssi}");
            _bleWatcher.OnProximityChanged   += tier =>
                OnStatusChanged?.Invoke($"Proximity: {tier}");
            _bleWatcher.OnRegistrationModeDetected += () =>
                OnStatusChanged?.Invoke("Registration mode detected");

            _tcpListener.OnStatusChanged     += s => OnStatusChanged?.Invoke(s);
            _tcpListener.OnConnected         += OnTcpConnected;
            _tcpListener.OnDisconnected      += OnTcpDisconnected;
            _tcpListener.OnHelloReceived     += (platform, name) =>
            {
                if (platform.Equals("ANDROID", StringComparison.OrdinalIgnoreCase))
                    AndroidDeviceName = name;
            };
            _tcpListener.OnSessionIdReceived += id =>
            {
                SessionId = id;
                Debug.WriteLine($"[SeaDrop] Session ID: {id}");
            };
            _tcpListener.OnFirmwareVersionReceived += ver =>
            {
                FirmwareVersion = ver;
                Debug.WriteLine($"[SeaDrop] Firmware: {ver}");
            };
            _tcpListener.OnNotifyReceived    += (fileName, fileSize) =>
            {
                _notificationHelper.ShowIncomingFile(fileName, fileSize);
                OnFileReceived?.Invoke(fileName, $"Downloads\\SeaDrop\\{fileName}");
            };
            _tcpListener.OnFileSent          += (name, size) => OnFileSent?.Invoke(name, size);
            _tcpListener.OnFileReceived      += (name, path) => OnFileReceived?.Invoke(name, path);

            _transferManager.OnStatusChanged += s => OnStatusChanged?.Invoke(s);
            _transferManager.OnFileSent      += (name, size) => OnFileSent?.Invoke(name, size);
            _transferManager.OnFileReceived  += (name, path) => OnFileReceived?.Invoke(name, path);

            _registrationManager.OnStatusChanged += s => OnStatusChanged?.Invoke(s);
        }

        // ── After TCP connects: send CHANNEL <n> ───────────────────────────────
        private async void OnTcpConnected()
        {
            OnConnectionChanged?.Invoke(true);

            // Send CHANNEL command so ESP32 can match SoftAP to home WiFi channel
            try
            {
                var detector = new ChannelDetector();
                int ch = detector.GetHomeWiFiChannel();
                if (ch < 1 || ch > 14) ch = 6;
                await _tcpListener.SendLineAsync($"CHANNEL {ch}");
                Debug.WriteLine($"[SeaDrop] Sent CHANNEL {ch}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SeaDrop] CHANNEL send failed: {ex.Message}");
                OnStatusChanged?.Invoke($"CHANNEL send failed: {ex.Message}");
            }

            // Resume any pending outbound transfers
            _transferManager.Start();
        }

        private void OnTcpDisconnected()
        {
            OnConnectionChanged?.Invoke(false);
            OnStatusChanged?.Invoke("SeaDrop offline — restarting hotspot…");

            // Spec: on disconnect → StopTetheringAsync, resume BLE scan
            _ = HandleDisconnectAsync();
        }

        private async Task HandleDisconnectAsync()
        {
            try
            {
                await _hotspotManager.StopAsync();
                _bleWatcher.Start();
                OnStatusChanged?.Invoke("Searching for SeaDrop…");

                // Restart hotspot so ESP32 can reconnect when it appears
                if (_isRunning && _credentialStore.GetWizardCompleted())
                {
                    await Task.Delay(2000, _cts.Token);
                    await _hotspotManager.StartAsync(_cts.Token);
                    OnStatusChanged?.Invoke("Hotspot restarted — waiting for SeaDrop…");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SeaDrop] Reconnect error: {ex.Message}");
                OnStatusChanged?.Invoke($"Reconnect error: {ex.Message}");
            }
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        public async Task StartAsync()
        {
            if (_isRunning) return;

            // 1. OnLaunched: Initialize system tray icon in grey state (done in App.xaml.cs)

            // 2. Call WiFiAdapter.RequestAccessAsync() on a background thread.
            var access = await WiFiAdapter.RequestAccessAsync();
            if (access != WiFiAccessStatus.Allowed)
            {
                // If result is not Allowed: show Windows toast notification, set tray icon grey, stop startup.
                _notificationHelper.ShowNotification("SeaDrop needs location access", "Open Settings > Privacy > Location and allow SeaDrop");
                OnPermissionDenied?.Invoke();
                return; // Do not proceed to step 3
            }

            _isRunning = true;
            _cts = new CancellationTokenSource();

            // 3. Call HotspotManager.StartTetheringAsync() with SSID and passphrase loaded from Windows Credential Manager.
            // If Credential Manager entry does not exist (first launch): open the setup wizard window instead.
            var ssid = _credentialStore.GetHotspotSsid();
            if (string.IsNullOrEmpty(ssid))
            {
                OnStatusChanged?.Invoke("First launch — credential entry does not exist. Opening wizard.");
                return;
            }

            try
            {
                OnStatusChanged?.Invoke("Starting hotspot…");
                await _hotspotManager.StartAsync(_cts.Token);
            }
            catch (Exception ex)
            {
                _isRunning = false;
                _notificationHelper.ShowNotification("Hotspot could not start", "Check that WiFi is enabled and try again");
                OnStatusChanged?.Invoke($"Hotspot failed: {ex.Message}");
                return;
            }

            // 4. Start BluetoothLEAdvertisementWatcher filtering for service UUID 0000FEAD-0000-1000-8000-00805F9B34FB.
            OnStatusChanged?.Invoke("Starting BLE watcher…");
            _bleWatcher.Start();

            // 5. Start TcpListener on the hotspot network interface IP, port 4242. Wait for ESP32 to connect inbound.
            OnStatusChanged?.Invoke("Listening on 0.0.0.0:4242…");
            await _tcpListener.StartAsync(_cts.Token);

            // Failover loop
            _failoverCts = new CancellationTokenSource();
            _ = RunFailoverLoopAsync(_failoverCts.Token);

            // Named pipe server (shell extension → enqueue file)
            _ = RunShellPipeServerAsync();
        }

        public async Task StopAsync()
        {
            if (!_isRunning) return;
            _isRunning = false;

            _cts.Cancel();
            _failoverCts.Cancel();
            _transferManager.Stop();
            await _tcpListener.StopAsync();
            _bleWatcher.Stop();
            await _hotspotManager.StopAsync();
        }

        // ── Named pipe server — receives paths from shell extension ─────────────
        private const string ShellPipeName = "SeaDropShell";

        private async Task RunShellPipeServerAsync()
        {
            while (_isRunning && !_cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new System.IO.Pipes.NamedPipeServerStream(
                        ShellPipeName,
                        System.IO.Pipes.PipeDirection.In,
                        1,
                        System.IO.Pipes.PipeTransmissionMode.Byte,
                        System.IO.Pipes.PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(_cts.Token);

                    using var reader = new System.IO.StreamReader(server, System.Text.Encoding.UTF8);
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        foreach (var path in line.Split('\n'))
                        {
                            var p = path.Trim();
                            if (System.IO.File.Exists(p))
                            {
                                EnqueueFileForSend(p);
                                OnStatusChanged?.Invoke($"Queued {System.IO.Path.GetFileName(p)}");
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SeaDrop] ShellPipe error: {ex.Message}");
                    try { await Task.Delay(500, _cts.Token); } catch { break; }
                }
            }
        }

        // ── Failover / internet monitor ────────────────────────────────────────
        private async Task RunFailoverLoopAsync(CancellationToken ct)
        {
            bool wasOnSeaDrop = false;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    bool onSeaDrop = _tcpListener.IsConnected;
                    bool onInternet = await CheckInternetAsync();

                    OnInternetAvailable?.Invoke(onInternet);

                    if (wasOnSeaDrop && !onSeaDrop)
                    {
                        OnStatusChanged?.Invoke("SeaDrop offline — switched to primary WiFi");
                        OnSeaDropAvailable?.Invoke(false);
                    }
                    else if (!wasOnSeaDrop && onSeaDrop)
                    {
                        OnStatusChanged?.Invoke("SeaDrop back online");
                        OnSeaDropAvailable?.Invoke(true);
                    }
                    wasOnSeaDrop = onSeaDrop;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SeaDrop] Failover loop: {ex.Message}");
                }

                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (TaskCanceledException) { break; }
            }
        }

        private static async Task<bool> CheckInternetAsync()
        {
            try
            {
                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var resp = await client.GetAsync("https://connectivity-check.ubuntu.com");
                return (int)resp.StatusCode == 204;
            }
            catch { return false; }
        }

        // ── Public API ─────────────────────────────────────────────────────────

        public void EnqueueFileForSend(string filePath) =>
            _transferManager.EnqueueFile(filePath);

        public bool IsRegistered() => _registrationManager.IsRegistered();

        public bool IsWizardCompleted() => _credentialStore.GetWizardCompleted();

        public async Task<bool> RegisterAsync(string deviceName, string hotspotSsid, string hotspotPass) =>
            await _registrationManager.RegisterWindowsAsync(deviceName, hotspotSsid, hotspotPass);

        public string GetHotspotSsid()   => _credentialStore.GetHotspotSsid();
        public string GetHotspotPass()   => _credentialStore.GetHotspotPass();
        public string GetDeviceName()    => _credentialStore.GetDeviceName();
        public void SaveDeviceName(string name) => _credentialStore.SaveDeviceName(name);
        public string GetAuthToken()     => _credentialStore.GetAuthToken();

        public HotspotManager HotspotManager => _hotspotManager;
        public TcpInboundListener TcpListener => _tcpListener;
    }
}