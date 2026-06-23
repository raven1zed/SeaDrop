package com.seadrop.app.transfer

import android.content.Context
import android.net.Uri
import android.os.Environment
import android.util.Log
import androidx.core.content.FileProvider
import com.seadrop.app.network.TcpClient
import com.seadrop.app.storage.SecurePrefs
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import java.io.File

class TransferManager(
    private val context: Context,
    private val tcpClient: TcpClient,
    private val securePrefs: SecurePrefs,
    private val onStatusUpdate: (String) -> Unit,
    private val onFileSent: (String, Long) -> Unit,
    private val onFileReceived: (String, File) -> Unit
) {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var pendingTransferInfo: String? = null
    private var pendingFileName: String? = null
    private var pendingFileSize: Long = 0

    fun start() {
        // No-op, TcpClient manages its own loops
    }

    fun stop() {
        scope.cancel()
    }

    fun sendFile(path: String, filename: String, mimeType: String) {
        val file = File(path)
        if (!file.exists()) {
            Log.w("TransferManager", "File does not exist: $path")
            return
        }
        tcpClient.enqueue(path, filename, mimeType)
    }

    fun handlePullRequest(fileName: String, fileSize: Long) {
        scope.launch {
            val receivedFile = tcpClient.pullFile(fileName, fileSize)
            receivedFile?.let { file ->
                val savedFile = saveReceivedFile(file, fileName)
                onFileReceived(fileName, savedFile)
            }
        }
    }

    fun acceptIncoming(transferInfo: String) {
        val parts = transferInfo.split(" ")
        if (parts.size >= 3) {
            pendingFileName = parts[1]
            pendingFileSize = parts[2].toLongOrNull() ?: 0L
            onStatusUpdate("Accepted: $pendingFileName")
            handlePullRequest(pendingFileName!!, pendingFileSize)
        }
    }

    fun declineIncoming(transferInfo: String) {
        val parts = transferInfo.split(" ")
        if (parts.size >= 3) {
            val fileName = parts[1]
            onStatusUpdate("Declined: $fileName")
            // Send CANCEL to PTD
            // tcpClient.sendLine("CANCEL") - would need to be added to TcpClient
        }
    }

    private suspend fun saveReceivedFile(tempFile: File, originalName: String): File {
        val seaDropDir = File(
            Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS),
            "SeaDrop"
        )
        seaDropDir.mkdirs()

        val destFile = File(seaDropDir, originalName)
        var counter = 1
        var finalDest = destFile
        while (finalDest.exists()) {
            val name = originalName.substringBeforeLast('.')
            val ext = originalName.substringAfterLast('.')
            finalDest = File(seaDropDir, if (ext.isNotEmpty()) "$name ($counter).$ext" else "$name ($counter)")
            counter++
        }

        tempFile.renameTo(finalDest)
        return finalDest
    }

    fun getPendingTransferInfo(): Triple<String?, String?, Long> {
        return Triple(pendingFileName, pendingTransferInfo, pendingFileSize)
    }
}