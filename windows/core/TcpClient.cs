using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SeaDropWindows.SeaDrop.storage;
using SeaDropWindows.SeaDrop.stp;

namespace SeaDropWindows.SeaDrop.core
{
    public class TcpClient
    {
        private const string Esp32Ip = "192.168.4.1";
        private const int Port = 4242;
        private readonly int _reconnectDelayMs = 3000;
        private readonly int _pingIntervalMs = 8000;
        private readonly int _socketTimeoutMs = 15000;

        private bool _isRunning;
        private CancellationTokenSource _cts = new();
        private System.Net.Sockets.TcpClient? _client;
        private NetworkStream? _stream;
        private StreamReader? _reader;
        private StreamWriter? _writer;
        private readonly CredentialStore _credentialStore;
        private readonly object _sendLock = new();

        public bool IsConnected => _client?.Connected == true;

        public event Action<string>? OnStatusChanged;
        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<string, long>? OnNotifyReceived;
        public event Action<string, long>? OnFileSent;
        public event Action<string, string>? OnFileReceived;

        public TcpClient(CredentialStore credentialStore)
        {
            _credentialStore = credentialStore;
        }

        public async Task StartAsync(CancellationToken externalToken = default)
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            await ConnectionLoopAsync();
        }

        public async Task StopAsync()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _cts.Cancel();
            Disconnect();
            await Task.CompletedTask;
        }

        private async Task ConnectionLoopAsync()
        {
            while (_isRunning && !_cts.IsCancellationRequested)
            {
                try
                {
                    OnStatusChanged?.Invoke("Connecting to SeaDrop...");
                    await ConnectAsync();
                    OnStatusChanged?.Invoke("Connected");
                    OnConnected?.Invoke();

                    _ = Task.Run(() => ReadLoopAsync(), _cts.Token);
                    await KeepAliveLoopAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnStatusChanged?.Invoke($"Connection error: {ex.Message}");
                }
                finally
                {
                    Disconnect();
                    OnDisconnected?.Invoke();
                }

                if (_isRunning && !_cts.IsCancellationRequested)
                {
                    OnStatusChanged?.Invoke($"Reconnecting in {_reconnectDelayMs / 1000}s...");
                    await Task.Delay(_reconnectDelayMs, _cts.Token);
                }
            }
        }

        private async Task ConnectAsync()
        {
            _client = new System.Net.Sockets.TcpClient
            {
                ReceiveTimeout = _socketTimeoutMs,
                SendTimeout = _socketTimeoutMs,
                NoDelay = true
            };
            await _client.ConnectAsync(Esp32Ip, Port, _cts.Token);

            _stream = _client.GetStream();
            _reader = new StreamReader(_stream, Encoding.UTF8, leaveOpen: true);
            _writer = new StreamWriter(_stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

            var token = _credentialStore.GetAuthToken();
            var deviceName = _credentialStore.GetDeviceName();
            await SendLineAsync($"HELLO WINDOWS {deviceName} {token}");

            var response = await ReadLineAsync();
            if (response == null || !response.StartsWith("HELLO_ACK"))
            {
                throw new InvalidOperationException($"Authentication failed: {response ?? "no response"}");
            }
        }

        public async Task SendLineAsync(string line)
        {
            if (_writer == null) throw new InvalidOperationException("Not connected");
            lock (_sendLock)
            {
                _writer.WriteLine(line);
                _writer.Flush();
            }
        }

        public async Task<string?> ReadLineAsync(int timeoutMs = -1)
        {
            if (_reader == null) throw new InvalidOperationException("Not connected");

            if (timeoutMs > 0)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(timeoutMs);
                try { return await _reader.ReadLineAsync(cts.Token); }
                catch (OperationCanceledException) { return null; }
            }
            return await _reader.ReadLineAsync(_cts.Token);
        }

        public async Task<StpCommand?> ReadCommandAsync(int timeoutMs = -1)
        {
            var line = await ReadLineAsync(timeoutMs);
            if (line == null) return null;
            return StpParser.Parse(line);
        }

        private async Task KeepAliveLoopAsync()
        {
            while (_isRunning && !_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_pingIntervalMs, _cts.Token);
                    await SendLineAsync("PING");

                    var response = await ReadLineAsync(5000);
                    if (response != "PONG")
                    {
                        throw new IOException("Lost connection to ESP32 (PONG timeout)");
                    }
                }
                catch
                {
                    break;
                }
            }
        }

        private async Task ReadLoopAsync()
        {
            while (_isRunning && !_cts.IsCancellationRequested)
            {
                try
                {
                    var line = await ReadLineAsync();
                    if (line == null) break;

                    var cmd = StpParser.Parse(line);
                    if (cmd == null) continue;

                    switch (cmd)
                    {
                        case StpCommandNotify notify:
                            OnNotifyReceived?.Invoke(notify.FileName, notify.FileSize);
                            break;
                        case StpCommandSendAck:
                            // Ready to send binary data
                            break;
                        case StpCommandAck:
                            // Transfer complete acknowledged
                            break;
                        case StpCommandPullData pullData:
                            _ = Task.Run(() => ReceiveFileAsync(pullData.FileSize), _cts.Token);
                            break;
                        case StpCommandReject reject:
                            OnStatusChanged?.Invoke($"Rejected: {reject.Reason}");
                            break;
                        case StpCommandPong:
                            // Handled in keepalive
                            break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }
                catch (Exception ex)
                {
                    OnStatusChanged?.Invoke($"Read error: {ex.Message}");
                }
            }
        }

        public async Task SendFileAsync(string filePath)
        {
            if (_stream == null || !_client?.Connected == true) return;

            try
            {
                var fileInfo = new FileInfo(filePath);
                var fileName = fileInfo.Name;
                var fileSize = fileInfo.Length;
                var crc32 = CalculateCrc32(filePath);

                await SendLineAsync($"SEND {fileName} {fileSize} {crc32} STREAM");

                var ack = await ReadLineAsync(5000);
                if (ack != "SEND_ACK") throw new IOException($"Expected SEND_ACK, got: {ack}");

                await SendFileDataAsync(filePath, fileSize);

                await SendLineAsync("SEND_DONE");

                var finalAck = await ReadLineAsync(10000);
                if (finalAck == "ACK")
                {
                    OnFileSent?.Invoke(fileName, fileSize);
                    OnStatusChanged?.Invoke($"Sent: {fileName}");
                }
                else
                {
                    throw new IOException($"Transfer failed: {finalAck}");
                }
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Send failed: {ex.Message}");
            }
        }

        private async Task SendFileDataAsync(string filePath, long fileSize)
        {
            const int bufferSize = 8192;
            var buffer = new byte[bufferSize];
            long totalSent = 0;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
            while (totalSent < fileSize)
            {
                int bytesToRead = (int)Math.Min(bufferSize, fileSize - totalSent);
                int bytesRead = await fs.ReadAsync(buffer, 0, bytesToRead, _cts.Token);
                if (bytesRead <= 0) break;

                lock (_sendLock)
                {
                    _stream!.Write(buffer, 0, bytesRead);
                }
                totalSent += bytesRead;

                int pct = (int)(totalSent * 100 / fileSize);
                OnStatusChanged?.Invoke($"Sending {Path.GetFileName(filePath)} {pct}%");
            }
            await _stream!.FlushAsync(_cts.Token);
        }

        private async Task ReceiveFileAsync(long fileSize)
        {
            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"seadrop_{Guid.NewGuid():N}.tmp");
                const int bufferSize = 8192;
                var buffer = new byte[bufferSize];
                long totalReceived = 0;

                using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
                while (totalReceived < fileSize)
                {
                    int bytesToRead = (int)Math.Min(bufferSize, fileSize - totalReceived);
                    int bytesRead = await _stream!.ReadAsync(buffer, 0, bytesToRead, _cts.Token);
                    if (bytesRead <= 0) break;

                    await fs.WriteAsync(buffer, 0, bytesRead, _cts.Token);
                    totalReceived += bytesRead;

                    int pct = (int)(totalReceived * 100 / fileSize);
                    OnStatusChanged?.Invoke($"Receiving {pct}%");
                }

                var downloadsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads", "SeaDrop");
                Directory.CreateDirectory(downloadsPath);
                var finalPath = Path.Combine(downloadsPath, $"received_{DateTime.Now:yyyyMMdd_HHmmss}.dat");
                File.Move(tempPath, finalPath);

                OnFileReceived?.Invoke("received_file", finalPath);
                await SendLineAsync("ACK");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Receive failed: {ex.Message}");
            }
        }

        public async Task PullFileAsync(string transferId)
        {
            await SendLineAsync($"PULL {transferId}");

            var pullData = await ReadCommandAsync(5000) as StpCommandPullData;
            if (pullData == null) throw new IOException("No PULL_DATA response");

            await ReceiveFileAsync(pullData.FileSize);
        }

        private static uint CalculateCrc32(string filePath)
        {
            uint crc = 0xFFFFFFFF;
            byte[] buffer = new byte[8192];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int bytesRead;
            while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < bytesRead; i++)
                {
                    crc ^= buffer[i];
                    for (int j = 0; j < 8; j++)
                    {
                        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : (crc >> 1);
                    }
                }
            }
            return crc ^ 0xFFFFFFFF;
        }

        public void Disconnect()
        {
            _reader?.Dispose(); _reader = null;
            _writer?.Dispose(); _writer = null;
            _stream?.Dispose(); _stream = null;
            _client?.Close(); _client = null;
        }
    }
}