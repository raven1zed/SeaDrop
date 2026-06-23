package com.seadrop.app.stp

/**
 * StpCommand — STP command data classes.
 * Represents the structure of SeaDrop Transfer Protocol commands.
 */
sealed class StpCommand {

    /** Client → PTD: HELLO ANDROID <deviceName> <token> */
    data class Hello(val deviceName: String, val token: String) : StpCommand()

    /** PTD → Client: HELLO_ACK */
    data object HelloAck : StpCommand()

    /** Client → PTD: PING (keepalive) */
    data object Ping : StpCommand()

    /** PTD → Client: PONG (keepalive response) */
    data object Pong : StpCommand()

    /** PTD → Client: NOTIFY <filename> <bytes> */
    data class Notify(val fileName: String, val fileSize: Long) : StpCommand()

    /** PTD → Client: REJECT <reason> */
    data class Reject(val reason: String) : StpCommand()

    /** Client → PTD: SEND <filename> <bytes> <crc32> */
    data class Send(val fileName: String, val fileSize: Long, val crc32: Long) : StpCommand()

    /** PTD → Client: SEND_ACK */
    data object SendAck : StpCommand()

    /** Client → PTD: SEND_DONE */
    data object SendDone : StpCommand()

    /** Client → PTD: PULL */
    data object Pull : StpCommand()

    /** PTD → Client: PULL_DATA <bytes> */
    data class PullData(val fileSize: Long) : StpCommand()

    /** PTD → Client: PULL_DONE */
    data object PullDone : StpCommand()

    /** PTD → Client: ACK */
    data object Ack : StpCommand()

    /** Client → PTD: REG ANDROID <deviceName> */
    data class Registration(val deviceName: String) : StpCommand()

    /** Client → PTD: REG WINDOWS <name> <hotspot_ssid> <hotspot_pass> */
    data class RegistrationWindows(
        val deviceName: String,
        val hotspotSsid: String,
        val hotspotPass: String
    ) : StpCommand()

    /** PTD → Client: TOKEN <hex> */
    data class Token(val token: String) : StpCommand()

    /** Serialize this command to a raw STP protocol line. */
    val raw: String
        get() = when (this) {
            is Hello -> "HELLO ANDROID $deviceName $token"
            is HelloAck -> "HELLO_ACK"
            is Ping -> "PING"
            is Pong -> "PONG"
            is Notify -> "NOTIFY $fileName $fileSize"
            is Reject -> "REJECT $reason"
            is Send -> "SEND $fileName $fileSize $crc32"
            is SendAck -> "SEND_ACK"
            is SendDone -> "SEND_DONE"
            is Pull -> "PULL"
            is PullData -> "PULL_DATA $fileSize"
            is PullDone -> "PULL_DONE"
            is Ack -> "ACK"
            is Registration -> "REG ANDROID $deviceName"
            is RegistrationWindows -> "REG WINDOWS $deviceName $hotspotSsid $hotspotPass"
            is Token -> "TOKEN $token"
        }
}