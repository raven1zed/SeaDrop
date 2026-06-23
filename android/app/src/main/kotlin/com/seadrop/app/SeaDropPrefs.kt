package com.seadrop.app

import android.content.Context
import android.content.SharedPreferences
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

object SeaDropPrefs {

    private const val PREFS_NAME = "seadrop_prefs"
    private const val SEC_PREFS_NAME = "seadrop_secure"

    private const val KEY_DEVICE_NAME = "device_name"
    private const val KEY_SEADROP_SSID = "seadrop_ssid"
    private const val KEY_SEADROP_PASS = "seadrop_pass"
    private const val KEY_REG_DONE = "reg_done"
    private const val KEY_TOKEN = "auth_token"

    private lateinit var prefs: SharedPreferences
    private lateinit var secPrefs: SharedPreferences

    fun init(context: Context) {
        prefs = context.applicationContext
            .getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

        val masterKey = MasterKey.Builder(context.applicationContext)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()

        secPrefs = EncryptedSharedPreferences.create(
            context.applicationContext,
            SEC_PREFS_NAME,
            masterKey,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
        )
    }

    var deviceName: String
        get() = prefs.getString(KEY_DEVICE_NAME, android.os.Build.MODEL) ?: android.os.Build.MODEL
        set(v) = prefs.edit().putString(KEY_DEVICE_NAME, v).apply()

    var seadropSsid: String
        get() = prefs.getString(KEY_SEADROP_SSID, "") ?: ""
        set(v) = prefs.edit().putString(KEY_SEADROP_SSID, v).apply()

    var seadropPass: String
        get() = secPrefs.getString(KEY_SEADROP_PASS, "") ?: ""
        set(v) = secPrefs.edit().putString(KEY_SEADROP_PASS, v).apply()

    var regDone: Boolean
        get() = prefs.getBoolean(KEY_REG_DONE, false)
        set(v) = prefs.edit().putBoolean(KEY_REG_DONE, v).apply()

    var authToken: String
        get() = secPrefs.getString(KEY_TOKEN, "") ?: ""
        set(v) = secPrefs.edit().putString(KEY_TOKEN, v).apply()

    val isRegistered: Boolean
        get() = regDone && authToken.isNotEmpty() && seadropSsid.isNotEmpty()
}