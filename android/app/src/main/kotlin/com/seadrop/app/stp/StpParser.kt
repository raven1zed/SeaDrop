package com.seadrop.app.stp

/**
 * StpParser — Command parser and builder.
 * Parses incoming STP commands and builds outgoing ones.
 */
class StpParser {

    /**
     * Parses a raw STP command line into a StpCommand object.
     * Returns null if the command is malformed or unknown.
     */
    fun parseCommand(line: String): StpCommand? {
        val parts = line.trim().split(" ")
        if (parts.isEmpty()) return null

        return when (parts[0]) {
            "HELLO_ACK" -> StpCommand.HelloAck
            "PING" -> StpCommand.Ping
            "PONG" -> StpCommand.Pong
            "NOTIFY" -> {
                if (parts.size < 3) null
                else StpCommand.Notify(
                    parts[1],
                    parts[2].toLongOrNull() ?: return null
                )
            }
            "REJECT" -> {
                if (parts.size < 2) null
                else StpCommand.Reject(
                    parts.drop(1).joinToString(" ")
                )
            }
            "SEND_ACK" -> StpCommand.SendAck
            "SEND_DONE" -> StpCommand.SendDone
            "PULL" -> StpCommand.Pull
            "PULL_DATA" -> {
                if (parts.size < 2) null
                else StpCommand.PullData(
                    parts[1].toLongOrNull() ?: return null
                )
            }
            "PULL_DONE" -> StpCommand.PullDone
            "ACK" -> StpCommand.Ack
            "TOKEN" -> {
                if (parts.size < 2) null
                else StpCommand.Token(
                    parts.drop(1).joinToString(" ")
                )
            }
            else -> null
        }
    }

    /**
     * Builds a raw STP command line from a StpCommand object.
     */
    fun buildCommand(command: StpCommand): String {
        return command.raw
    }

    /**
     * Builds a registration command for Android device.
     */
    fun buildAndroidRegistration(deviceName: String): String {
        return StpCommand.Registration(deviceName).raw
    }

    /**
     * Builds a registration command for Windows device.
     */
    fun buildWindowsRegistration(deviceName: String, hotspotSsid: String, hotspotPass: String): String {
        return StpCommand.RegistrationWindows(deviceName, hotspotSsid, hotspotPass).raw
    }

    /**
     * Builds a HELLO command for authenticated connection.
     */
    fun buildHello(deviceName: String, token: String): String {
        return StpCommand.Hello(deviceName, token).raw
    }

    /**
     * Builds a SEND command for file transmission.
     */
    fun buildSend(fileName: String, fileSize: Long, crc32: Long): String {
        return StpCommand.Send(fileName, fileSize, crc32).raw
    }

    /**
     * Builds a PULL command for file reception.
     */
    fun buildPull(): String {
        return StpCommand.Pull.raw
    }
}