package com.seadrop.app.transfer

import android.content.Context
import android.net.Uri
import android.os.Environment
import android.provider.DocumentsContract
import android.provider.MediaStore
import android.util.Log
import androidx.core.content.FileProvider
import java.io.File
import java.io.FileOutputStream
import java.io.InputStream
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * FileHelper — SAF URI resolution, file read/write operations.
 * Handles file system integration for saving received files and sharing outgoing files.
 */
class FileHelper(private val context: Context) {

    companion object {
        private const val DATE_FORMAT = "yyyyMMdd_HHmmss"
        private const val SEA_DROP_DIR_NAME = "SeaDrop"
    }

    /**
     * Resolves a SAF URI to a File object for reading.
     * Handles content:// URIs from other apps.
     */
    fun uriToFile(uri: Uri): File? {
        return try {
            when {
                uri.scheme == "file" -> File(uri.path)
                DocumentsContract.isDocumentUri(context, uri) -> {
                    // Handle SAF URIs
                    when (uri.authority) {
                        "com.android.externalstorage.documents" -> {
                            val docId = DocumentsContract.getDocumentId(uri)
                            val split = docId.split(":")
                            val type = split[0]
                            val relPath = split[1]
                            when (type) {
                                "primary" -> File(Environment.getExternalStorageDirectory(), relPath)
                                else -> null
                            }
                        }
                        "com.android.providers.downloads.documents" -> {
                            val id = DocumentsContract.getDocumentId(uri)
                            val contentUri = Uri.withAppendedPath(
                                Uri.parse("content://downloads/public_downloads"), id
                            )
                            uriToFile(contentUri)
                        }
                        "com.android.providers.media.documents" -> {
                            val docId = DocumentsContract.getDocumentId(uri)
                            val split = docId.split(":")
                            val type = split[0]
                            val id = split[1]
                            val contentUri = when (type) {
                                "image" -> MediaStore.Images.Media.EXTERNAL_CONTENT_URI
                                "video" -> MediaStore.Video.Media.EXTERNAL_CONTENT_URI
                                "audio" -> MediaStore.Audio.Media.EXTERNAL_CONTENT_URI
                                else -> null
                            } ?: return null
                            val selection = "_id=?"
                            val selectionArgs = arrayOf(id)
                            uriToFile(contentUri, selection, selectionArgs)
                        }
                        else -> null
                    }
                }
                else -> {
                    // Handle regular content URIs
                    uriToFile(uri, null, null)
                }
            }
        } catch (e: Exception) {
            Log.e("FileHelper", "uriToFile failed: ${e.message}")
            null
        }
    }

    private fun uriToFile(uri: Uri, selection: String?, selectionArgs: Array<String>?): File? {
        val cursor = context.contentResolver.query(uri, null, selection, selectionArgs, null) ?: return null
        return cursor.use {
            if (!it.moveToFirst()) return null
            val dataColumnIndex = it.getColumnIndex(MediaStore.MediaColumns.DISPLAY_NAME)
            if (dataColumnIndex < 0) return null
            val displayName = it.getString(dataColumnIndex)
            // For simplicity, we'll save to app's cache dir
            // In a production app, we might want to let the user choose the location
            File(context.cacheDir, displayName)
        }
    }

    /**
     * Creates a unique file name in the SeaDrop directory for saving received files.
     * @param originalName The original filename (may contain path)
     * @return A unique File object in the SeaDrop directory
     */
    fun createUniqueReceiveFile(originalName: String): File {
        val seaDropDir = getSeaDropDirectory()
        val simpleName = originalName.substringAfterLast('/').substringAfterLast('\\')
        val nameWithoutExt = simpleName.substringBeforeLast('.')
        val extension = simpleName.substringAfterLast('.').takeIf { it.isNotEmpty() } ?: ""

        var file = File(seaDropDir, simpleName)
        var counter = 1

        while (file.exists()) {
            val newName = if (extension.isEmpty())
                "$nameWithoutExt ($counter)"
                else
                "$nameWithoutExt ($counter).$extension"
            file = File(seaDropDir, newName)
            counter++
        }

        return file
    }

    /**
     * Gets the SeaDrop directory in external storage for received files.
     * Creates the directory if it doesn't exist.
     * @return The SeaDrop directory File object
     */
    fun getSeaDropDirectory(): File {
        val seaDropDir = File(
            Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS),
            SEA_DROP_DIR_NAME
        )
        seaDropDir.mkdirs()
        return seaDropDir
    }

    /**
     * Generates a timestamped filename for sharing.
     * @param prefix Optional prefix for the filename
     * @return A timestamped filename string
     */
    fun generateTimestampedFilename(prefix: String = "seadrop"): String {
        val timestamp = SimpleDateFormat(DATE_FORMAT, Locale.US).format(Date())
        return "${prefix}_$timestamp"
    }

    /**
     * Copies content from an InputStream to a File.
     * @param input The input stream to read from
     * @param destination The file to write to
     * @return True if successful, false otherwise
     */
    fun copyInputStreamToFile(input: InputStream, destination: File): Boolean {
        return try {
            destination.parentFile?.mkdirs()
            FileOutputStream(destination).use { output ->
                input.copyTo(output)
            }
            true
        } catch (e: Exception) {
            Log.e("FileHelper", "copyInputStreamToFile failed: ${e.message}")
            false
        }
    }
}