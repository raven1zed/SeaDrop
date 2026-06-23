package com.seadrop.app

import android.app.Activity
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.IBinder
import android.provider.OpenableColumns
import android.util.Log
import android.widget.Toast
import com.seadrop.app.storage.SecurePrefs

class ShareActivity : Activity() {

    companion object {
        private const val TAG = "SeaDrop:Share"
        private const val MAX_CACHE_SIZE = 100 * 1024 * 1024 // 100 MB
    }

    private var service: SeaDropService? = null
    private var serviceBound = false

    private val connection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName, binder: IBinder) {
            service = (binder as SeaDropService.LocalBinder).getService()
            serviceBound = true
            processIntent()
        }
        override fun onServiceDisconnected(name: ComponentName) {
            service = null
            serviceBound = false
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val securePrefs = SecurePrefs(this)
        if (!securePrefs.hasToken()) {
            Toast.makeText(this, "SeaDrop not set up. Open the SeaDrop app first.", Toast.LENGTH_LONG).show()
            finish()
            return
        }

        // Ensure service is running
        val svcIntent = Intent(this, SeaDropService::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(svcIntent)
        } else {
            startService(svcIntent)
        }
        bindService(svcIntent, connection, Context.BIND_AUTO_CREATE)
    }

    override fun onDestroy() {
        if (serviceBound) {
            unbindService(connection)
            serviceBound = false
        }
        super.onDestroy()
    }

    private fun processIntent() {
        val svc = service ?: run { finish(); return }
        val uris = collectUris()

        if (uris.isEmpty()) {
            Toast.makeText(this, "SeaDrop: no supported file found", Toast.LENGTH_SHORT).show()
            finish()
            return
        }

        var queued = 0
        for (uri in uris) {
            try {
                val (path, name, mime) = copyToCache(uri) ?: continue
                svc.enqueue(path, name, mime)
                queued++
            } catch (e: Exception) {
                Log.e(TAG, "Failed to process URI $uri: ${e.message}")
            }
        }

        val msg = when {
            queued == 0 -> "SeaDrop: failed to read file"
            queued == 1 -> "SeaDrop: sending…"
            else -> "SeaDrop: sending $queued files…"
        }
        Toast.makeText(this, msg, Toast.LENGTH_SHORT).show()
        finish()
    }

    private fun collectUris(): List<Uri> {
        return when (intent.action) {
            Intent.ACTION_SEND -> {
                val uri = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
                } else {
                    @Suppress("DEPRECATION")
                    intent.getParcelableExtra(Intent.EXTRA_STREAM)
                }
                listOfNotNull(uri)
            }
            Intent.ACTION_SEND_MULTIPLE -> {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                    intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java) ?: emptyList()
                } else {
                    @Suppress("DEPRECATION")
                    intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM) ?: emptyList()
                }
            }
            else -> emptyList()
        }
    }

    private fun copyToCache(uri: Uri): Triple<String, String, String>? {
        val mime = contentResolver.getType(uri) ?: "application/octet-stream"
        val filename = queryFilename(uri) ?: "seadrop_${System.currentTimeMillis()}"
        val dest = java.io.File(cacheDir, "seadrop_share_${System.currentTimeMillis()}_$filename")

        // Check cache size before copying
        val cacheSize = getCacheDirSize()
        if (cacheSize > MAX_CACHE_SIZE) {
            cleanupOldCacheFiles()
        }

        return try {
            contentResolver.openInputStream(uri)?.use { input ->
                dest.outputStream().use { output -> input.copyTo(output) }
            }
            Triple(dest.absolutePath, filename, mime)
        } catch (e: Exception) {
            Log.e(TAG, "copyToCache failed: ${e.message}")
            null
        }
    }

    private fun queryFilename(uri: Uri): String? {
        if (uri.scheme == "file") return uri.lastPathSegment
        return try {
            contentResolver.query(uri, null, null, null, null)?.use { cursor ->
                if (cursor.moveToFirst()) {
                    val idx = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    if (idx >= 0) cursor.getString(idx) else null
                } else null
            }
        } catch (_: Exception) { null }
    }

    private fun getCacheDirSize(): Long {
        return cacheDir.listFiles()?.sumOf { it.length() } ?: 0L
    }

    private fun cleanupOldCacheFiles() {
        cacheDir.listFiles()
            ?.filter { it.name.startsWith("seadrop_share_") }
            ?.sortedBy { it.lastModified() }
            ?.forEach { it.delete() }
    }
}