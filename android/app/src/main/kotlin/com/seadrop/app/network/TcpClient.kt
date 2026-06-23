package com.seadrop.app.network

import android.content.Context
import android.os.PowerManager
import android.util.Log
import com.seadrop.app.storage.SecurePrefs
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.Socket
import java.nio.charset.Charset
import java.util.zip.CRC32

class TcpClient(
    private val context: Context,
    private val scope: CoroutineScope,
    private val securePrefs: SecurePrefs,
    private val onStatusUpdate: (String) -> Unit,
    private val onIncomingNotify: (String) -> Unit,
    private val onPullRequest: (String, Long) -> Unit,
    private val onFileSent: (String, Long) -> Unit,
    private val onFileReceived: (String, java.io.File) -> Unit
) {

    companion object {
        const val PING_INTERVAL_MS = 10_000L
        const val SOCKET_TIMEOUT_MS = 15_000
    }

    private var pingJob: Job? = null
    private var readJob: Job? = null
    private var socket: Socket? = null
    private var socketOut: OutputStream? = null
    private var socketIn: InputStream? = null
    private val sendLock = Any()
    @Volatile private var isRunning = false
    @Volatile var statusText: String = "Searching..."
        private set

    private val outboundQueue = java.util.ArrayDeque<QueuedFile>()
    private val queueLock = Any()
    private var wakeLock: PowerManager.WakeLock? = null

    val isConnected: Boolean get() = socket?.isConnected == true

    data class QueuedFile(val path: String, val filename: String, val mimeType: String)

    fun startBoundSocket(boundSocket: Socket) {
        socket = boundSocket
        socketOut = boundSocket.getOutputStream()
        socketIn = boundSocket.getInputStream()
        isRunning = true

        scope.launch(Dispatchers.IO) {
            try {
                // HELLO handshake with auth token and version 1.5.0
                val token = securePrefs.getToken()
                val deviceName = securePrefs.getString("device_name") ?: "Android"
                val version = "1.5.0"
                sendLine("HELLO ANDROID $deviceName $token $version")

                val helloAck = readLine(5000) ?: throw IOException("No HELLO_ACK")
                if (helloAck.startsWith("REJECT VERSION_MISMATCH")) {
                    throw IOException("Version mismatch: SeaDrop firmware needs updating")
                }
                if (!helloAck.startsWith("HELLO_ACK")) {
                    throw IOException("Authentication failed: $helloAck")
                }

                // Parse session ID and firmware version from HELLO_ACK <session_id> <version>
                val parts = helloAck.split(" ")
                if (parts.size >= 3) {
                    val sessionId = parts[1]
                    val fwVersion = parts[2]
                    Log.i("TcpClient", "Handshake success. Session: $sessionId, FW Version: $fwVersion")
                }

                setStatus("Connected")
                acquireWakeLock()

                // Start ping loop
                pingJob?.cancel()
                pingJob = scope.launch(Dispatchers.IO) {
                    while (isRunning) {
                        delay(PING_INTERVAL_MS)
                        try {
                            sendLine("PING")
                        } catch (e: IOException) {
                            Log.e("TcpClient", "Ping failed: ${e.message}")
                            break
                        }
                    }
                }

                // Read loop
                readJob?.cancel()
                readJob = scope.launch(Dispatchers.IO) {
                    readLoop()
                }

                // Process pending file queue
                drainQueue()

            } catch (e: Exception) {
                Log.e("TcpClient", "Handshake failed: ${e.message}")
                stop()
            }
        }
    }

    fun stop() {
        if (!isRunning) return
        isRunning = false
        pingJob?.cancel()
        readJob?.cancel()
        try {
            socket?.close()
        } catch (e: IOException) {
            Log.e("TcpClient", "Error closing socket: ${e.message}")
        }
        socket = null
        socketOut = null
        socketIn = null
        releaseWakeLock()
        setStatus("Searching...")
    }

    fun enqueue(path: String, filename: String, mimeType: String) {
        synchronized(queueLock) {
            outboundQueue.addLast(QueuedFile(path, filename, mimeType))
        }
        if (isConnected) {
            scope.launch(Dispatchers.IO) { drainQueue() }
        }
    }

    fun sendCancel() {
        scope.launch(Dispatchers.IO) {
            try {
                sendLine("CANCEL")
            } catch (e: IOException) {
                Log.e("TcpClient", "Failed to send CANCEL: ${e.message}")
            }
        }
    }

    suspend fun awaitDisconnection() {
        while (isRunning && socket?.isConnected == true) {
            delay(500)
        }
    }

    private suspend fun readLoop() {
        while (isRunning) {
            try {
                val line = readLine()
                if (line == null) {
                    Log.w("TcpClient", "Connection closed by peer")
                    break
                }
                Log.d("TcpClient", "Received: $line")
                when {
                    line.startsWith("NOTIFY") -> onIncomingNotify(line)
                    line == "PONG" -> { /* Keepalive acknowledged */ }
                    line.startsWith("REJECT") -> {
                        setStatus("Rejected: ${line.substringAfter("REJECT ")}")
                    }
                    line.startsWith("PULL_DATA") -> {
                        val parts = line.split(" ")
                        if (parts.size >= 2) {
                            val fileSize = parts[1].toLongOrNull() ?: 0L
                            val fileName = "received_file"
                            onPullRequest(fileName, fileSize)
                        }
                    }
                    line == "CANCEL" -> {
                        setStatus("Transfer cancelled")
                    }
                }
            } catch (e: IOException) {
                Log.e("TcpClient", "Read error: ${e.message}")
                break
            }
        }
        stop()
    }

    private fun sendLine(line: String) {
        synchronized(sendLock) {
            val out = socketOut ?: throw IOException("Not connected")
            out.write("$line\n".toByteArray(Charset.forName("UTF-8")))
            out.flush()
        }
    }

    private fun readLine(timeoutMs: Int = -1): String? {
        val input = socketIn ?: return null
        if (timeoutMs > 0) {
            try {
                socket?.soTimeout = timeoutMs
            } catch (e: Exception) {
                Log.e("TcpClient", "Error setting socket timeout: ${e.message}")
            }
        } else {
            try {
                socket?.soTimeout = SOCKET_TIMEOUT_MS
            } catch (e: Exception) {
                Log.e("TcpClient", "Error resetting socket timeout: ${e.message}")
            }
        }
        val sb = java.lang.StringBuilder()
        while (true) {
            val b = input.read()
            if (b < 0) return null
            if (b.toChar() == '\n') return sb.toString().trimEnd('\r')
            sb.append(b.toChar())
        }
    }

    private suspend fun drainQueue() {
        while (isRunning) {
            val item = synchronized(queueLock) {
                if (outboundQueue.isEmpty()) null else outboundQueue.first()
            } ?: break

            try {
                sendFile(item)
                synchronized(queueLock) { outboundQueue.removeFirst() }
            } catch (e: IOException) {
                Log.e("TcpClient", "Queue drain failed: ${e.message}")
                break
            }
        }
    }

    private suspend fun sendFile(item: QueuedFile) {
        val javaFile = java.io.File(item.path)
        if (!javaFile.exists()) return
        val size = javaFile.length()
        val crc32 = computeCrc32(javaFile)

        setStatus("Sending ${item.filename}…")
        try {
            sendLine("SEND ${item.filename} $size $crc32 STREAM")

            val ack = readLine(5000) ?: throw IOException("No SEND_ACK")
            if (!ack.startsWith("SEND_ACK")) throw IOException("Expected SEND_ACK, got $ack")

            val buf = ByteArray(8192)
            var sent = 0L
            javaFile.inputStream().use { fis ->
                while (true) {
                    val n = fis.read(buf)
                    if (n < 0) break
                    synchronized(sendLock) {
                        val out = socketOut ?: throw IOException("Stream closed during send")
                        out.write(buf, 0, n)
                    }
                    sent += n
                    val pct = (sent * 100 / size).toInt()
                    setStatus("Sending ${item.filename} $pct%")
                }
            }
            synchronized(sendLock) {
                socketOut?.flush()
            }
            sendLine("SEND_DONE")

            val result = readLine(10000)
            if (result == "ACK") {
                setStatus("Ready")
                onFileSent(item.filename, size)
            } else {
                throw IOException("Transfer rejected: $result")
            }
        } finally {
            releaseWakeLock()
        }
    }

    suspend fun pullFile(fileName: String, fileSize: Long): java.io.File? {
        return withContext(Dispatchers.IO) {
            try {
                setStatus("Receiving $fileName…")
                sendLine("PULL $fileName")

                val pullDataLine = readLine(5000) ?: throw IOException("No PULL_DATA")
                if (!pullDataLine.startsWith("PULL_DATA")) {
                    throw IOException("Expected PULL_DATA, got $pullDataLine")
                }

                val cacheFile = java.io.File(context.cacheDir, "seadrop_${System.currentTimeMillis()}_$fileName")
                val buf = ByteArray(8192)
                var received = 0L

                cacheFile.outputStream().use { fos ->
                    while (received < fileSize) {
                        val toRead = (fileSize - received).coerceAtMost(buf.size.toLong()).toInt()
                        val n = socketIn!!.read(buf, 0, toRead)
                        if (n <= 0) throw IOException("Connection closed during pull")
                        fos.write(buf, 0, n)
                        received += n
                        val pct = (received * 100 / fileSize).toInt()
                        setStatus("Receiving $fileName $pct%")
                    }
                }

                sendLine("PULL_DONE")
                val ack = readLine(5000)
                if (ack == "ACK") {
                    setStatus("Ready")
                    cacheFile
                } else {
                    null
                }
            } catch (e: IOException) {
                Log.e("TcpClient", "Pull file failed: ${e.message}")
                setStatus("Receive failed: ${e.message}")
                null
            }
        }
    }

    private fun computeCrc32(file: java.io.File): Long {
        val crc = CRC32()
        file.inputStream().use { fis ->
            val buf = ByteArray(8192)
            while (true) {
                val n = fis.read(buf); if (n < 0) break
                crc.update(buf, 0, n)
            }
        }
        return crc.value
    }

    private fun setStatus(text: String) {
        statusText = text
        onStatusUpdate(text)
    }

    private fun acquireWakeLock() {
        val pm = context.getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "SeaDrop::TcpClient::WakeLock")
        wakeLock?.acquire()
    }

    private fun releaseWakeLock() {
        wakeLock?.release()
        wakeLock = null
    }
}