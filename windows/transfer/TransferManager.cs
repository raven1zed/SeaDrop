using System;
using System.IO;
using System.Threading.Tasks;
using SeaDropWindows.SeaDrop.core;

namespace SeaDropWindows.SeaDrop.transfer
{
    public class TransferManager
    {
        private readonly TcpInboundListener _tcpListener;
        private readonly OutboundQueue _outboundQueue;
        private readonly FileHelper _fileHelper;
        private bool _isRunning;

        public event Action<string>? OnStatusChanged;
        public event Action<string, long>? OnFileSent;
        public event Action<string, string>? OnFileReceived;

        public TransferManager(TcpInboundListener tcpListener)
        {
            _tcpListener = tcpListener;
            _outboundQueue = new OutboundQueue();
            _fileHelper = new FileHelper();

            _tcpListener.OnNotifyReceived += async (fileName, fileSize) =>
            {
                OnStatusChanged?.Invoke($"Incoming: {fileName} ({fileSize} bytes)");
                await _tcpListener.PullFileAsync(fileName);
            };
            _tcpListener.OnFileSent += (name, size) => OnFileSent?.Invoke(name, size);
            _tcpListener.OnFileReceived += (name, path) => OnFileReceived?.Invoke(name, path);
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _ = ProcessQueueAsync();
        }

        public void Stop()
        {
            _isRunning = false;
        }

        public void EnqueueFile(string filePath)
        {
            if (_fileHelper.IsValidFilePath(filePath))
                _outboundQueue.Enqueue(filePath);
        }

        private async Task ProcessQueueAsync()
        {
            while (_isRunning)
            {
                try
                {
                    var filePath = _outboundQueue.Dequeue();
                    if (filePath != null)
                    {
                        await SendFileAsync(filePath);
                    }
                    else
                    {
                        await Task.Delay(100);
                    }
                }
                catch (Exception ex)
                {
                    OnStatusChanged?.Invoke($"Queue error: {ex.Message}");
                    await Task.Delay(1000);
                }
            }
        }

        private async Task SendFileAsync(string filePath)
        {
            try
            {
                if (!_fileHelper.IsValidFilePath(filePath))
                {
                    OnStatusChanged?.Invoke($"Invalid path: {filePath}");
                    return;
                }

                var fileInfo = new FileInfo(filePath);
                var fileName = fileInfo.Name;
                var fileSize = fileInfo.Length;
                var crc = CalculateCrc32(filePath);

                OnStatusChanged?.Invoke($"Sending {fileName}...");
                await _tcpListener.SendFileAsync(filePath);
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Send failed: {ex.Message}");
                _outboundQueue.Enqueue(filePath);
            }
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