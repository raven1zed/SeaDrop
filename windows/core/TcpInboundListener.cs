using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SeaDropWindows.SeaDrop.storage;
using SeaDropWindows.SeaDrop.stp;

namespace SeaDropWindows.SeaDrop.core
{
    public class TcpInboundListener
    {
        private const int Port = 4242;
        private const int SocketTimeoutMs = 15000;

        private readonly CredentialStore _credentialStore;
        private System.Net.Sockets.TcpListener? _listener;
        private CancellationTokenSource _cts = new();
        private bool _isRunning;
        private System.Net.Sockets.TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        public bool IsConnected { get; private set; }
        public event Action<string>? OnStatusChanged;
        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<string, long>? OnNotifyReceived;
        public event Action<string, long>? OnFileSent;
        public event Action<string, string>? OnFileReceived;
        public event Action<string, string>? OnHelloReceived;
        /// <summary>Fires when HELLO_ACK &lt;session_id&gt; is received from the ESP32.</summary>
        public event Action<string>? OnSessionIdReceived;
        /// <summary>Fires when the ESP32 includes a firmware version in its HELLO_ACK response.</summary>
        public event Action<string>? OnFirmwareVersionReceived;

        public TcpInboundListener(CredentialStore credentialStore)
        {
            _credentialStore = credentialStore;
        }

        public async Task StartAsync(CancellationToken externalToken = default)
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);

            try
            {
                _listener = new System.Net.Sockets.TcpListener(IPAddress.Any, Port);
                _listener.Server.ReceiveTimeout = SocketTimeoutMs;
                _listener.Server.SendTimeout = SocketTimeoutMs;
                _listener.Start();

                _ = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
                OnStatusChanged?.Invoke("Listening on 0.0.0.0:4242...");
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Listener error: {ex.Message}");
                _isRunning = false;
            }
        }

        public async Task StopAsync()
        {
            if (!_isRunning) return;
            _isRunning = false;
            _cts.Cancel();
            Disconnect();
            _listener?.Stop();
            _listener = null;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (_isRunning && !ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => HandleClientAsync(client, ct), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    OnStatusChanged?.Invoke($"Accept error: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(System.Net.Sockets.TcpClient client, CancellationToken ct)
        {
            NetworkStream? stream = null;
            StreamReader? reader = null;
            StreamWriter? writer = null;

            try
            {
                client.ReceiveTimeout = SocketTimeoutMs;
                client.SendTimeout = SocketTimeoutMs;
                client.NoDelay = true;

                stream = client.GetStream();
                reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                lock (this)
                {
                    _tcpClient = client;
                    _stream = stream;
                    _reader = reader;
                    _writer = writer;
                }

                IsConnected = true;
                OnConnected?.Invoke();

                string? line;
                while (_isRunning && !ct.IsCancellationRequested && (line = await reader.ReadLineAsync()) != null)
                {
                    var cmd = StpParser.Parse(line);
                    if (cmd == null) continue;

                    switch (cmd)
                    {
                        case StpCommandHello hello:
                            OnHelloReceived?.Invoke(hello.Platform, hello.DeviceName);
                            // Spec: HELLO_ACK <session_id>  — session ID is a short UUID
                            var sessionId = Guid.NewGuid().ToString("N")[..8];
                            await writer.WriteLineAsync($"HELLO_ACK {sessionId}");
                            OnSessionIdReceived?.Invoke(sessionId);
                            // Parse firmware version if ESP32 includes it: HELLO WINDOWS name token v1.5.0
                            if (hello is StpCommandHelloVersioned hv)
                                OnFirmwareVersionReceived?.Invoke(hv.FirmwareVersion);
                            break;
                        case StpCommandPing:
                            await writer.WriteLineAsync("PONG");
                            break;
                        case StpCommandNotify notify:
                            OnNotifyReceived?.Invoke(notify.FileName, notify.FileSize);
                            break;
                        case StpCommandPull pull:
                            await writer.WriteLineAsync("PULL_ACK");
                            break;
                        case StpCommandPullData pullData:
                            await ReceiveFileAsync(stream, pullData.FileSize, ct);
                            break;
                        case StpCommandReject reject:
                            OnStatusChanged?.Invoke($"Rejected: {reject.Reason}");
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Client error: {ex.Message}");
            }
            finally
            {
                lock (this)
                {
                    _reader = null;
                    _writer = null;
                    _stream = null;
                    _tcpClient = null;
                }
                writer?.Dispose();
                reader?.Dispose();
                stream?.Dispose();
                client?.Close();
                IsConnected = false;
                OnDisconnected?.Invoke();
            }
        }

        public async Task SendLineAsync(string line)
        {
            if (_writer == null) throw new InvalidOperationException("Not connected");
            await _writer.WriteLineAsync(line);
        }

        public async Task<string?> ReadLineAsync()
        {
            if (_reader == null) throw new InvalidOperationException("Not connected");
            return await _reader.ReadLineAsync();
        }

        public async Task<StpCommand?> ReadCommandAsync()
        {
            var line = await ReadLineAsync();
            if (line == null) return null;
            return StpParser.Parse(line);
        }

        public async Task PullFileAsync(string fileName)
        {
            await SendLineAsync($"PULL {fileName}");
            var pullData = await ReadCommandAsync() as StpCommandPullData;
            if (pullData != null)
            {
                await ReceiveFileAsync(_stream!, pullData.FileSize, _cts.Token);
            }
        }

        private async Task ReceiveFileAsync(NetworkStream stream, long fileSize, CancellationToken ct)
        {
            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"seadrop_{Guid.NewGuid():N}.tmp");
                const int bufferSize = 8192;
                var buffer = new byte[bufferSize];
                long totalReceived = 0;

                using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
                while (totalReceived < fileSize && !ct.IsCancellationRequested)
                {
                    int bytesToRead = (int)Math.Min(bufferSize, fileSize - totalReceived);
                    int bytesRead = await stream.ReadAsync(buffer, 0, bytesToRead, ct);
                    if (bytesRead <= 0) break;

                    await fs.WriteAsync(buffer, 0, bytesRead, ct);
                    totalReceived += bytesRead;
                }

                var downloadsPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads", "SeaDrop");
                Directory.CreateDirectory(downloadsPath);
                var finalPath = Path.Combine(downloadsPath, $"received_{DateTime.Now:yyyyMMdd_HHmmss}.dat");
                File.Move(tempPath, finalPath);

                OnFileReceived?.Invoke("received_file", finalPath);
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Receive failed: {ex.Message}");
            }
        }

        public async Task SendFileAsync(string filePath)
        {
            if (_stream == null || _tcpClient?.Connected != true) return;

            try
            {
                var fileInfo = new FileInfo(filePath);
                var fileName = fileInfo.Name;
                var fileSize = fileInfo.Length;
                var crc32 = CalculateCrc32(filePath);

                await SendLineAsync($"SEND {fileName} {fileSize} {crc32} STREAM");

                var response = await ReadLineAsync();
                if (response != "SEND_ACK") throw new IOException($"Expected SEND_ACK, got: {response}");

                await SendFileDataAsync(filePath, fileSize);
                await SendLineAsync("SEND_DONE");

                var finalAck = await ReadLineAsync();
                if (finalAck == "ACK")
                {
                    OnFileSent?.Invoke(fileName, fileSize);
                    OnStatusChanged?.Invoke($"Sent: {fileName}");
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

                await _stream!.WriteAsync(buffer, 0, bytesRead, _cts.Token);
                totalSent += bytesRead;

                int pct = (int)(totalSent * 100 / fileSize);
                OnStatusChanged?.Invoke($"Sending {Path.GetFileName(filePath)} {pct}%");
            }
            await _stream!.FlushAsync(_cts.Token);
        }

        private void Disconnect()
        {
            _reader?.Dispose();
            _writer?.Dispose();
            _stream?.Dispose();
            _tcpClient?.Close();
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
    }
}