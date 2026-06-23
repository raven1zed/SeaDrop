using System;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.Advertisement;

namespace SeaDropWindows.SeaDrop.network
{
    public class BleWatcher
    {
        private BluetoothLEAdvertisementWatcher? _watcher;
        private bool _isRunning;

        public event Action<string, int>? OnDeviceFound;
        public event Action<string>? OnProximityChanged;
        public event Action? OnRegistrationModeDetected;

        public BleWatcher() { }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            StartWatcher();
        }

        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;
            StopWatcher();
        }

        private void StartWatcher()
        {
            try
            {
                _watcher = new BluetoothLEAdvertisementWatcher
                {
                    ScanningMode = BluetoothLEScanningMode.Active
                };

                _watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(
                    Guid.Parse("0000FEAD-0000-1000-8000-00805F9B34FB"));

                _watcher.Received += OnAdvertisementReceived;
                _watcher.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BLE watcher start error: {ex.Message}");
            }
        }

        private void StopWatcher()
        {
            try
            {
                _watcher?.Stop();
                _watcher = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BLE watcher stop error: {ex.Message}");
            }
        }

        private void OnAdvertisementReceived(
            BluetoothLEAdvertisementWatcher sender,
            BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var deviceName = args.Advertisement?.LocalName ?? "Unknown";
            var rssi = args.RawSignalStrengthInDBm;

            if (deviceName.StartsWith("SeaDrop"))
            {
                OnDeviceFound?.Invoke(deviceName, rssi);

                var tier = ClassifyRssi(rssi);
                OnProximityChanged?.Invoke(tier);
            }

            var regMode = ParseRegistrationMode(args);
            if (regMode)
            {
                OnRegistrationModeDetected?.Invoke();
            }
        }

        private static bool ParseRegistrationMode(BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var data = args.Advertisement?.DataSections;
            if (data == null) return false;

            foreach (var section in data)
            {
                var sectionData = section.Data;
                if (sectionData.Length >= 3)
                {
                    var reader = Windows.Storage.Streams.DataReader.FromBuffer(sectionData);
                    var uuidLow = reader.ReadByte();
                    var uuidHigh = reader.ReadByte();
                    if (uuidLow == 0xAD && uuidHigh == 0xFE)
                    {
                        var regMode = reader.ReadByte();
                        return regMode == 1;
                    }
                }
            }
            return false;
        }

        private static string ClassifyRssi(int rssi)
        {
            if (rssi > -55) return "CLOSE";
            if (rssi >= -70) return "MEDIUM";
            return "FAR";
        }
    }
}