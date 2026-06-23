using System;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace SeaDropWindows.SeaDrop.notification
{
    public class ToastHelper
    {
        private readonly ToastNotifier _notifier;

        public ToastHelper()
        {
            _notifier = ToastNotificationManager.CreateToastNotifier("SeaDrop");
        }

        public void ShowIncomingFile(string fileName, long fileSize)
        {
            var sizeStr = FormatBytes(fileSize);
            var xml = $@"
<toast activationType='protocol' launch='seadrop://accept/{Uri.EscapeDataString(fileName)}'>
  <visual>
    <binding template='ToastGeneric'>
      <text>SeaDrop — Incoming File</text>
      <text>{fileName} ({sizeStr})</text>
    </binding>
  </visual>
  <actions>
    <action activationType='protocol' content='Accept' arguments='seadrop://accept/{Uri.EscapeDataString(fileName)}'/>
    <action activationType='protocol' content='Decline' arguments='seadrop://decline/{Uri.EscapeDataString(fileName)}'/>
  </actions>
</toast>";

            ShowToast(xml);
        }

        public void ShowFileSent(string fileName, long fileSize)
        {
            var sizeStr = FormatBytes(fileSize);
            var xml = $@"
<toast>
  <visual>
    <binding template='ToastGeneric'>
      <text>SeaDrop — File Sent</text>
      <text>{fileName} ({sizeStr})</text>
    </binding>
  </visual>
</toast>";

            ShowToast(xml);
        }

        public void ShowFileReceived(string fileName, string filePath)
        {
            var xml = $@"
<toast activationType='protocol' launch='seadrop://open/{Uri.EscapeDataString(filePath)}'>
  <visual>
    <binding template='ToastGeneric'>
      <text>SeaDrop — File Received</text>
      <text>{fileName}</text>
    </binding>
  </visual>
  <actions>
    <action activationType='protocol' content='Open' arguments='seadrop://open/{Uri.EscapeDataString(filePath)}'/>
  </actions>
</toast>";

            ShowToast(xml);
        }

        public void ShowStatus(string status)
        {
            var xml = $@"
<toast>
  <visual>
    <binding template='ToastGeneric'>
      <text>SeaDrop</text>
      <text>{System.Security.SecurityElement.Escape(status)}</text>
    </binding>
  </visual>
</toast>";

            ShowToast(xml);
        }

        public void ShowNotification(string title, string body)
        {
            var xml = $@"
<toast>
  <visual>
    <binding template='ToastGeneric'>
      <text>{System.Security.SecurityElement.Escape(title)}</text>
      <text>{System.Security.SecurityElement.Escape(body)}</text>
    </binding>
  </visual>
</toast>";

            ShowToast(xml);
        }

        private void ShowToast(string xmlContent)
        {
            try
            {
                var xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(xmlContent);
                var toast = new ToastNotification(xmlDoc)
                {
                    ExpirationTime = DateTimeOffset.Now.AddSeconds(10)
                };
                _notifier.Show(toast);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Toast error: {ex.Message}");
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1048576) return $"{bytes / 1024.0:0.##} KB";
            if (bytes < 1073741824) return $"{bytes / 1048576.0:0.##} MB";
            return $"{bytes / 1073741824.0:0.##} GB";
        }
    }
}