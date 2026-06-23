package com.seadrop.app.notification

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.graphics.drawable.Icon
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.FileProvider
import com.seadrop.app.MainActivity
import com.seadrop.app.R
import com.seadrop.app.SeaDropService

class NotificationHelper(private val context: Context) {

    companion object {
        const val CHANNEL_ID_STATUS = "seadrop_status"
        private const val CHANNEL_ID_TRANSFER = "seadrop_transfer"
        private const val NOTIF_ID_STATUS = 1
        private const val NOTIF_ID_INCOMING = 2
    }

    init {
        createNotificationChannels()
    }

    private fun createNotificationChannels() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val statusChannel = NotificationChannel(
                CHANNEL_ID_STATUS,
                "SeaDrop Status",
                NotificationManager.IMPORTANCE_LOW
            ).apply { description = "SeaDrop connection status" }

            val transferChannel = NotificationChannel(
                CHANNEL_ID_TRANSFER,
                "SeaDrop Transfers",
                NotificationManager.IMPORTANCE_HIGH
            ).apply { description = "Incoming file notifications" }

            val notificationManager = context.getSystemService(NotificationManager::class.java)
            notificationManager.createNotificationChannel(statusChannel)
            notificationManager.createNotificationChannel(transferChannel)
        }
    }

    fun buildStatusNotification(text: String, contentIntent: PendingIntent? = null): Notification {
        val openIntent = contentIntent ?: PendingIntent.getActivity(
            context,
            0,
            Intent(context, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        return NotificationCompat.Builder(context, CHANNEL_ID_STATUS)
            .setSmallIcon(R.drawable.ic_seadrop)
            .setContentTitle("SeaDrop")
            .setContentText(text)
            .setContentIntent(openIntent)
            .setOngoing(true)
            .setSilent(true)
            .build()
    }

    fun updateStatusNotification(text: String) {
        val nm = context.getSystemService(NotificationManager::class.java)
        nm.notify(NOTIF_ID_STATUS, buildStatusNotification(text))
    }

    fun showIncomingNotification(fileName: String, fileSize: Long, info: String) {
        val acceptIntent = PendingIntent.getService(
            context,
            0,
            Intent(context, SeaDropService::class.java).apply {
                action = SeaDropService.ACTION_ACCEPT_INCOMING
                putExtra(SeaDropService.EXTRA_TRANSFER_INFO, info)
            },
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        val declineIntent = PendingIntent.getService(
            context,
            1,
            Intent(context, SeaDropService::class.java).apply {
                action = SeaDropService.ACTION_DECLINE_INCOMING
            },
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        val sizeStr = humanReadableSize(fileSize)
        val notif = NotificationCompat.Builder(context, CHANNEL_ID_TRANSFER)
            .setSmallIcon(R.drawable.ic_seadrop_download)
            .setContentTitle("Incoming: $fileName")
            .setContentText("$sizeStr — tap to save")
            .addAction(R.drawable.ic_save, "Save", acceptIntent)
            .addAction(R.drawable.ic_cancel, "Decline", declineIntent)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setAutoCancel(true)
            .build()

        NotificationManagerCompat.from(context).notify(NOTIF_ID_INCOMING, notif)
    }

    fun showReceivedNotification(fileName: String, file: java.io.File) {
        val openIntent = PendingIntent.getActivity(
            context,
            0,
            Intent(Intent.ACTION_VIEW).apply {
                setDataAndType(
                    FileProvider.getUriForFile(
                        context,
                        "${context.packageName}.provider",
                        file
                    ),
                    "*/*"
                )
                flags = Intent.FLAG_GRANT_READ_URI_PERMISSION
            },
            PendingIntent.FLAG_IMMUTABLE
        )
        val notif = NotificationCompat.Builder(context, CHANNEL_ID_TRANSFER)
            .setSmallIcon(R.drawable.ic_seadrop_done)
            .setContentTitle("SeaDrop — Received")
            .setContentText(fileName)
            .setContentIntent(openIntent)
            .setAutoCancel(true)
            .build()

        NotificationManagerCompat.from(context).notify(NOTIF_ID_INCOMING + 1, notif)
    }

    private fun humanReadableSize(bytes: Long): String = when {
        bytes < 1_024L -> "$bytes B"
        bytes < 1_048_576L -> "%.1f KB".format(bytes / 1_024.0)
        bytes < 1_073_741_824L -> "%.1f MB".format(bytes / 1_048_576.0)
        else -> "%.1f GB".format(bytes / 1_073_741_824.0)
    }
}