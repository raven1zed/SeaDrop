using System;

namespace SeaDropWindows.SeaDrop.stp
{
    /// <summary>
    /// StpCommand — STP command record types.
    /// Represents the structure of SeaDrop Transfer Protocol commands.
    /// </summary>
    public abstract class StpCommand
    {
        public abstract string ToRawString();
    }

    /// <summary>
    /// Client → PTD: HELLO <platform> <deviceName> <token>
    /// </summary>
    public class StpCommandHello : StpCommand
    {
        public string Platform { get; } // "WINDOWS" or "ANDROID"
        public string DeviceName { get; }
        public string Token { get; }

        public StpCommandHello(string platform, string deviceName, string token)
        {
            Platform = platform;
            DeviceName = deviceName;
            Token = token;
        }

        public override string ToRawString() =>
            $"HELLO {Platform} {DeviceName} {Token}";
    }

    /// <summary>
    /// PTD → Client: HELLO_ACK &lt;session_id&gt;
    /// </summary>
    public sealed class StpCommandHelloAck : StpCommand
    {
        public string SessionId { get; }
        public StpCommandHelloAck(string sessionId = "") { SessionId = sessionId; }
        public override string ToRawString() =>
            string.IsNullOrEmpty(SessionId) ? "HELLO_ACK" : $"HELLO_ACK {SessionId}";
    }

    /// <summary>
    /// Extended HELLO that also carries a firmware version token.
    /// HELLO <platform> <name> <token> <fw_version>
    /// </summary>
    public sealed class StpCommandHelloVersioned : StpCommandHello
    {
        public string FirmwareVersion { get; }
        
        public StpCommandHelloVersioned(string platform, string deviceName, string token, string firmwareVersion)
            : base(platform, deviceName, token)
        {
            FirmwareVersion = firmwareVersion;
        }
        
        public override string ToRawString() =>
            $"HELLO {Platform} {DeviceName} {Token} {FirmwareVersion}";
    }

    /// <summary>
    /// Client → PTD: PING (keepalive)
    /// </summary>
    public sealed class StpCommandPing : StpCommand
    {
        public override string ToRawString() => "PING";
    }

    /// <summary>
    /// PTD → Client: PONG (keepalive response)
    /// </summary>
    public sealed class StpCommandPong : StpCommand
    {
        public override string ToRawString() => "PONG";
    }

    /// <summary>
    /// PTD → Client: NOTIFY <filename> <bytes>
    /// Windows-originating file ready on SD card
    /// </summary>
    public sealed class StpCommandNotify : StpCommand
    {
        public string FileName { get; }
        public long FileSize { get; }

        public StpCommandNotify(string fileName, long fileSize)
        {
            FileName = fileName;
            FileSize = fileSize;
        }

        public override string ToRawString() =>
            $"NOTIFY {FileName} {FileSize}";
    }

    /// <summary>
    /// PTD → Client: REJECT <reason>
    /// Transfer rejection with reason
    /// </piblic>
    public sealed class StpCommandReject : StpCommand
    {
        public string Reason { get; }

        public StpCommandReject(string reason)
        {
            Reason = reason;
        }

        public override string ToRawString() => $"REJECT {Reason}";
    }

    /// <summary>
    /// Client → PTD: SEND <filename> <bytes> <crc32>
    /// Initiates file send to PTD
    /// </summary>
    public sealed class StpCommandSend : StpCommand
    {
        public string FileName { get; }
        public long FileSize { get; }
        public uint Crc32 { get; }

        public StpCommandSend(string fileName, long fileSize, uint crc32)
        {
            FileName = fileName;
            FileSize = fileSize;
            Crc32 = crc32;
        }

        public override string ToRawString() =>
            $"SEND {FileName} {FileSize} {Crc32}";
    }

    /// <summary>
    /// PTD → Client: SEND_ACK
    /// Ready to receive file data
    /// </summary>
    public sealed class StpCommandSendAck : StpCommand
    {
        public override string ToRawString() => "SEND_ACK";
    }

    /// <summary>
    /// Client → PTD: SEND_DONE
    /// File data transmission complete
    /// </summary>
    public sealed class StpCommandSendDone : StpCommand
    {
        public override string ToRawString() => "SEND_DONE";
    }

    /// <summary>
    /// Client → PTD: PULL <filename>
    /// Request to receive file from PTD
    /// </summary>
    public sealed class StpCommandPull : StpCommand
    {
        public string FileName { get; }

        public StpCommandPull(string fileName)
        {
            FileName = fileName;
        }

        public override string ToRawString() => $"PULL {FileName}";
    }

    /// <summary>
    /// PTD → Client: PULL_DATA <bytes>
    /// Header for incoming file data
    /// </summary>
    public sealed class StpCommandPullData : StpCommand
    {
        public long FileSize { get; }

        public StpCommandPullData(long fileSize)
        {
            FileSize = fileSize;
        }

        public override string ToRawString() =>
            $"PULL_DATA {FileSize}";
    }

    /// <summary>
    /// PTD → Client: PULL_DONE
    /// File data transmission complete
    /// </summary>
    public sealed class StpCommandPullDone : StpCommand
    {
        public override string ToRawString() => "PULL_DONE";
    }

    /// <summary>
    /// PTD → Client: ACK
    /// Generic acknowledgment
    /// </summary>
    public sealed class StpCommandAck : StpCommand
    {
        public override string ToRawString() => "ACK";
    }

    /// <summary>
    /// Client → PTD: REG <platform> <deviceName> [<hotspot_ssid> <hotspot_pass>]
    /// Registration request
    /// </summary>
    public abstract class StpCommandReg : StpCommand
    {
        public string Platform { get; } // "WINDOWS" or "ANDROID"
        public string DeviceName { get; }

        protected StpCommandReg(string platform, string deviceName)
        {
            Platform = platform;
            DeviceName = deviceName;
        }
    }

    /// <summary>
    /// Client → PTD: REG WINDOWS <deviceName> <hotspot_ssid> <hotspot_pass>
    /// Windows registration request
    /// </summary>
    public sealed class StpCommandRegWindows : StpCommandReg
    {
        public string HotspotSsid { get; }
        public string HotspotPass { get; }

        public StpCommandRegWindows(string deviceName, string hotspotSsid, string hotspotPass)
            : base("WINDOWS", deviceName)
        {
            HotspotSsid = hotspotSsid;
            HotspotPass = hotspotPass;
        }

        public override string ToRawString() =>
            $"REG WINDOWS {DeviceName} {HotspotSsid} {HotspotPass}";
    }

    /// <summary>
    /// Client → PTD: REG ANDROID <deviceName>
    /// Android registration request
    /// </summary>
    public sealed class StpCommandRegAndroid : StpCommandReg
    {
        public StpCommandRegAndroid(string deviceName)
            : base("ANDROID", deviceName)
        {
        }

        public override string ToRawString() =>
            $"REG ANDROID {DeviceName}";
    }

    /// <summary>
    /// PTD ↔ Client: TOKEN <hex>
    /// Registration response containing auth token
    /// </summary>
    public sealed class StpCommandToken : StpCommand
    {
        public string Token { get; }
        public StpCommandToken(string token) { Token = token; }
        public override string ToRawString() => $"TOKEN {Token}";
    }

    /// <summary>
    /// Client → PTD: CANCEL
    /// Either party cancels active transfer.
    /// </summary>
    public sealed class StpCommandCancel : StpCommand
    {
        public override string ToRawString() => "CANCEL";
    }

    /// <summary>
    /// Windows → PTD: CHANNEL <n>
    /// Windows reports home WiFi channel so ESP32 matches SoftAP channel.
    /// </summary>
    public sealed class StpCommandChannel : StpCommand
    {
        public int Channel { get; }
        public StpCommandChannel(int channel) { Channel = channel; }
        public override string ToRawString() => $"CHANNEL {Channel}";
    }
}