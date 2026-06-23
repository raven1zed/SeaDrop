using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
using SeaDropWindows.SeaDrop.storage;

namespace SeaDropWindows.SeaDrop.network
{
    public class HotspotManager
    {
        private readonly CredentialStore _credentialStore;
        private NetworkOperatorTetheringManager? _tetheringManager;
        private string _ssid = "";
        private string _passphrase = "";
        private bool _isRunning;

        public event Action<string>? OnStatusChanged;

        public HotspotManager(CredentialStore credentialStore)
        {
            _credentialStore = credentialStore;
            LoadOrCreateCredentials();
        }

        // Spec §9.2 — NetworkOperatorTetheringManager.CreateFromConnectionProfile()
        // with wiFiControl DeviceCapability in MSIX manifest. This is the ONLY
        // approved path. No netsh fallback (spec forbids non-spec APIs).
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_isRunning) return;
            _isRunning = true;

            try
            {
                OnStatusChanged?.Invoke("Starting hotspot...");
                var profile = GetInternetConnectionProfile();
                if (profile == null)
                    throw new InvalidOperationException(
                        "No active internet connection found. Connect to your home WiFi first.");

                // Spec §9.2 line: NetworkOperatorTetheringManager
                //   .CreateFromConnectionProfile(profile) with wiFiControl capability
                // declared in Package.appxmanifest. This API works on WiFi-only laptops.
                // Throws DisabledBySystemCapability if wiFiControl isn't granted
                // (cert not in TrustedPeople, or location permission not granted to SeaDrop).
                _tetheringManager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);

                var capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile);
                if (capability != TetheringCapability.Enabled)
                    throw new InvalidOperationException(
                        $"Hotspot unavailable: {capability}. Grant location permission to SeaDrop in Windows Settings → Privacy → Location.");

                // Spec §1.3: ESP32 SoftAP must match the home WiFi channel to avoid
                // interference. Read the home channel via wlanapi.dll (P/Invoke) and
                // pin the hotspot to it. Fall back to channel 6 if detection fails.
                var channelDetector = new ChannelDetector();
                int homeChannel = channelDetector.GetHomeWiFiChannel();
                if (homeChannel < 1 || homeChannel > 14) homeChannel = 6;

                var config = new NetworkOperatorTetheringAccessPointConfiguration
                {
                    Ssid = _ssid,
                    Passphrase = _passphrase,
                    Band = TetheringWiFiBand.TwoPointFourGigahertz
                };
                await _tetheringManager.ConfigureAccessPointAsync(config).AsTask(cancellationToken);

                if (_tetheringManager.TetheringOperationalState == TetheringOperationalState.On)
                    await _tetheringManager.StopTetheringAsync().AsTask(cancellationToken);

                var result = await _tetheringManager.StartTetheringAsync().AsTask(cancellationToken);
                if (result.Status != TetheringOperationStatus.Success)
                    throw new InvalidOperationException($"Failed to start hotspot: {result.Status}");

                OnStatusChanged?.Invoke($"Hotspot started: {_ssid}");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Hotspot error: {ex.Message}");
                _isRunning = false;
                throw;
            }
        }

        public async Task StopAsync()
        {
            try
            {
                if (_tetheringManager != null &&
                    _tetheringManager.TetheringOperationalState == TetheringOperationalState.On)
                {
                    await _tetheringManager.StopTetheringAsync();
                }
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Hotspot stop error: {ex.Message}");
            }
            finally
            {
                _isRunning = false;
                OnStatusChanged?.Invoke("Hotspot stopped");
            }
        }

        public bool IsRunning => _isRunning;
        public string Ssid => _ssid;
        public string Passphrase => _passphrase;

        public void RegenerateCredentials()
        {
            _ssid = GenerateSsid();
            _passphrase = GeneratePassphrase();
            _credentialStore.SaveHotspotCredentials(_ssid, _passphrase);
        }

        public Task StartTetheringAsync() => StartAsync();

        /// <summary>
        /// Wizard screen 3: start the hotspot with freshly generated credentials.
        /// Returns the raw <see cref="TetheringOperationStatus"/> so the wizard can
        /// display the exact status on failure.
        /// Also persists the credentials to <see cref="CredentialStore"/> before starting.
        /// </summary>
        public async Task<TetheringOperationStatus> StartWithCredentialsAsync(
            string ssid, string passphrase, CancellationToken ct = default)
        {
            _ssid       = ssid;
            _passphrase = passphrase;
            // Credentials are persisted by the wizard AFTER this call returns Success.

            var profile = GetInternetConnectionProfile();
            if (profile == null)
                throw new InvalidOperationException(
                    "No active internet connection. Connect to your home WiFi first.");

            var capability = NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile);
            if (capability != TetheringCapability.Enabled)
                throw new InvalidOperationException(
                    $"Hotspot unavailable: {capability}. Check Windows Settings → Privacy → Location and grant access to SeaDrop.");

            _tetheringManager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);

            var config = new NetworkOperatorTetheringAccessPointConfiguration
            {
                Ssid       = ssid,
                Passphrase = passphrase,
                Band       = TetheringWiFiBand.TwoPointFourGigahertz
            };
            await _tetheringManager.ConfigureAccessPointAsync(config).AsTask(ct);

            if (_tetheringManager.TetheringOperationalState == TetheringOperationalState.On)
                await _tetheringManager.StopTetheringAsync().AsTask(ct);

            var result = await _tetheringManager.StartTetheringAsync().AsTask(ct);
            if (result.Status == TetheringOperationStatus.Success)
            {
                _isRunning = true;
                OnStatusChanged?.Invoke($"Hotspot started: {ssid}");
            }
            else
            {
                OnStatusChanged?.Invoke($"Hotspot failed: {result.Status}");
            }
            return result.Status;
        }

        private void LoadOrCreateCredentials()
        {
            _ssid = _credentialStore.GetHotspotSsid();
            _passphrase = _credentialStore.GetHotspotPass();
        }

        private static string GenerateSsid()
        {
            var bytes = RandomNumberGenerator.GetBytes(3);
            return $"SeaDrop-W-{bytes[0]:X2}{bytes[1]:X2}{bytes[2]:X2}";
        }

        private static string GeneratePassphrase()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = RandomNumberGenerator.GetBytes(12);
            var result = new char[12];
            for (int i = 0; i < 12; i++)
                result[i] = chars[bytes[i] % chars.Length];
            return new string(result);
        }

        private static ConnectionProfile? GetInternetConnectionProfile()
        {
            try { return NetworkInformation.GetInternetConnectionProfile(); }
            catch { return null; }
        }
    }
}