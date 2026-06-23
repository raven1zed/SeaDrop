package com.seadrop.app

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.os.Build
import android.util.Log

/**
 * BootReceiver — starts SeaDropService after device reboot or app update.
 *
 * Receives BOOT_COMPLETED and MY_PACKAGE_REPLACED.
 * Only starts the service if the device is registered (regDone + token set).
 * This prevents the wizard from being bypassed on fresh install.
 */
class BootReceiver : BroadcastReceiver() {

    companion object {
        private const val TAG = "SeaDrop:Boot"
    }

    override fun onReceive(context: Context, intent: Intent) {
        val action = intent.action ?: return
        if (action != Intent.ACTION_BOOT_COMPLETED &&
            action != Intent.ACTION_MY_PACKAGE_REPLACED) return

        SeaDropPrefs.init(context)
        if (!SeaDropPrefs.isRegistered) {
            Log.i(TAG, "Boot received but device not registered — skipping service start")
            return
        }

        Log.i(TAG, "Boot received — starting SeaDropService")
        val svc = Intent(context, SeaDropService::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            context.startForegroundService(svc)
        } else {
            context.startService(svc)
        }
    }
}
