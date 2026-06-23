using System;
using SeaDropWindows.SeaDrop.stp;

namespace SeaDropWindows.SeaDrop.stp
{
    public static class StpParser
    {
        public static StpCommand? Parse(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return null;

            return parts[0] switch
            {
                "HELLO_ACK" => ParseHelloAck(parts),
                "PING"      => new StpCommandPing(),
                "PONG"      => new StpCommandPong(),
                "NOTIFY"    => ParseNotify(parts),
                "REJECT"    => ParseReject(parts),
                "SEND_ACK"  => new StpCommandSendAck(),
                "SEND_DONE" => new StpCommandSendDone(),
                "PULL"      => ParsePull(parts),
                "PULL_DATA" => ParsePullData(parts),
                "PULL_DONE" => new StpCommandPullDone(),
                "ACK"       => new StpCommandAck(),
                "TOKEN"     => ParseToken(parts),
                "HELLO"     => ParseHello(parts),
                "REG"       => ParseReg(parts),
                "CANCEL"    => new StpCommandCancel(),
                "CHANNEL"   => ParseChannel(parts),
                _           => null
            };
        }

        private static StpCommandNotify? ParseNotify(string[] parts)
        {
            if (parts.Length < 3) return null;
            if (!long.TryParse(parts[2], out long fileSize)) return null;
            return new StpCommandNotify(parts[1], fileSize);
        }

        private static StpCommandReject? ParseReject(string[] parts)
        {
            if (parts.Length < 2) return null;
            var reason = string.Join(" ", parts, 1, parts.Length - 1);
            return new StpCommandReject(reason);
        }

        private static StpCommandPull? ParsePull(string[] parts)
        {
            if (parts.Length < 2) return null;
            return new StpCommandPull(parts[1]);
        }

        private static StpCommandPullData? ParsePullData(string[] parts)
        {
            if (parts.Length < 2) return null;
            if (!long.TryParse(parts[1], out long fileSize)) return null;
            return new StpCommandPullData(fileSize);
        }

        private static StpCommandToken? ParseToken(string[] parts)
        {
            if (parts.Length < 2) return null;
            var token = string.Join(" ", parts, 1, parts.Length - 1);
            return new StpCommandToken(token);
        }

        private static StpCommandHelloAck ParseHelloAck(string[] parts)
        {
            var sid = parts.Length >= 2 ? parts[1] : string.Empty;
            return new StpCommandHelloAck(sid);
        }

        private static StpCommand? ParseHello(string[] parts)
        {
            // HELLO <platform> <name> <token>            (4 parts)
            // HELLO <platform> <name> <token> <version>  (5 parts — firmware version)
            if (parts.Length < 4) return null;
            if (parts.Length >= 5)
                return new StpCommandHelloVersioned(parts[1], parts[2], parts[3], parts[4]);
            return new StpCommandHello(parts[1], parts[2], parts[3]);
        }

        private static StpCommandChannel? ParseChannel(string[] parts)
        {
            if (parts.Length < 2) return null;
            return int.TryParse(parts[1], out int ch) ? new StpCommandChannel(ch) : null;
        }

        private static StpCommand? ParseReg(string[] parts)
        {
            if (parts.Length < 3) return null;
            return parts[1] switch
            {
                "WINDOWS" => ParseRegWindows(parts),
                "ANDROID" => ParseRegAndroid(parts),
                _ => null
            };
        }

        private static StpCommandRegWindows? ParseRegWindows(string[] parts)
        {
            if (parts.Length < 5) return null;
            return new StpCommandRegWindows(parts[2], parts[3], parts[4]);
        }

        private static StpCommandRegAndroid? ParseRegAndroid(string[] parts)
        {
            if (parts.Length < 3) return null;
            return new StpCommandRegAndroid(parts[2]);
        }

        public static string BuildCommand(StpCommand command) => command.ToRawString();

        public static string BuildWindowsRegistration(string deviceName, string hotspotSsid, string hotspotPass) =>
            new StpCommandRegWindows(deviceName, hotspotSsid, hotspotPass).ToRawString();

        public static string BuildAndroidRegistration(string deviceName) =>
            new StpCommandRegAndroid(deviceName).ToRawString();

        public static string BuildHello(string platform, string deviceName, string token) =>
            new StpCommandHello(platform, deviceName, token).ToRawString();

        public static string BuildSend(string fileName, long fileSize, uint crc32) =>
            new StpCommandSend(fileName, fileSize, crc32).ToRawString();

        public static string BuildPull(string fileName) => new StpCommandPull(fileName).ToRawString();
    }
}