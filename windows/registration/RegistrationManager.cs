using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SeaDropWindows.SeaDrop.storage;
using NetTcpClient = System.Net.Sockets.TcpClient;

namespace SeaDropWindows.SeaDrop.registration
{
    public class RegistrationManager
    {
        private readonly CredentialStore _credentialStore;

        public event Action<string>? OnStatusChanged;

        public RegistrationManager(CredentialStore credentialStore)
        {
            _credentialStore = credentialStore;
        }

        public async Task<bool> RegisterWindowsAsync(
            string deviceName,
            string hotspotSsid,
            string hotspotPass,
            CancellationToken cancellationToken = default)
        {
            try
            {
                OnStatusChanged?.Invoke("Connecting to SeaDrop SoftAP...");
                
                using var client = new NetTcpClient();
                var connectTask = client.ConnectAsync("192.168.4.1", 4242);
                var timeoutTask = Task.Delay(8000, cancellationToken);
                
                if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                {
                    throw new TimeoutException("Could not connect to SeaDrop");
                }
                
                OnStatusChanged?.Invoke("Sending registration...");
                
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                
                await writer.WriteLineAsync($"REG WINDOWS {deviceName} {hotspotSsid} {hotspotPass}");
                
                var response = await reader.ReadLineAsync();
                if (response == null || !response.StartsWith("TOKEN "))
                {
                    throw new InvalidOperationException($"Registration failed: {response ?? "no response"}");
                }
                
                var token = response.Substring(6).Trim();
                
                _credentialStore.SaveAuthToken(token);
                _credentialStore.SaveDeviceName(deviceName);
                _credentialStore.SaveHotspotCredentials(hotspotSsid, hotspotPass);
                
                OnStatusChanged?.Invoke("Registration complete!");
                return true;
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Registration failed: {ex.Message}");
                return false;
            }
        }

        public bool IsRegistered() =>
            !string.IsNullOrEmpty(_credentialStore.GetAuthToken()) &&
            !string.IsNullOrEmpty(_credentialStore.GetDeviceName());
    }
}