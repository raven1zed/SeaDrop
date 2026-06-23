package com.seadrop.app.network

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiNetworkSuggestion
import android.util.Log
import com.seadrop.app.storage.SecurePrefs

class WifiManager(
    private val context: Context,
    private val securePrefs: SecurePrefs,
    private val onBlacklisted: (() -> Unit)? = null
) {

    private val wm by lazy { context.getSystemService(Context.WIFI_SERVICE) as android.net.wifi.WifiManager }
    private val cm by lazy { context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager }
    private var networkCallback: ConnectivityManager.NetworkCallback? = null

    private val ssid: String by lazy {
        securePrefs.getString("seadrop_ssid") ?: ""
    }

    private val passphrase: String by lazy {
        securePrefs.getString("seadrop_pass") ?: "seadrop2026"
    }

    fun register() {
        if (ssid.isEmpty()) {
            Log.d("WifiManager", "No SSID stored yet — skipping WiFi suggestion")
            return
        }

        val existing = wm.networkSuggestions
        val toRemove = existing.filter { it.ssid == ssid }
        if (toRemove.isNotEmpty()) wm.removeNetworkSuggestions(toRemove)

        val suggestion = WifiNetworkSuggestion.Builder()
            .setSsid(ssid)
            .setWpa2Passphrase(passphrase)
            .setIsAppInteractionRequired(false)
            .setPriority(999)
            .build()

        val status = wm.addNetworkSuggestions(listOf(suggestion))
        if (status == android.net.wifi.WifiManager.STATUS_NETWORK_SUGGESTIONS_SUCCESS ||
            status == android.net.wifi.WifiManager.STATUS_NETWORK_SUGGESTIONS_ERROR_ADD_DUPLICATE) {
            Log.i("WifiManager", "WiFi suggestion registered for SSID: $ssid")
        } else {
            Log.w("WifiManager", "WiFi suggestion failed: status=$status")
        }

        startBlacklistMonitor()
    }

    private fun startBlacklistMonitor() {
        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .build()

        networkCallback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                Log.i("WifiManager", "SeaDrop network available: $network")
            }

            override fun onLost(network: Network) {
                Log.w("WifiManager", "SeaDrop network lost - possible manual disconnect blacklist")
                onBlacklisted?.invoke()
            }
        }

        cm.registerNetworkCallback(request, networkCallback!!)
    }

    fun stopBlacklistMonitor() {
        networkCallback?.let { cm.unregisterNetworkCallback(it) }
        networkCallback = null
    }

    fun forceReconnect() {
        if (ssid.isNotEmpty()) {
            val specifier = WifiNetworkSuggestion.Builder()
                .setSsid(ssid)
                .setWpa2Passphrase(passphrase)
                .build()
            wm.addNetworkSuggestions(listOf(specifier))
        }
    }

    fun getStoredSsid(): String = ssid
    fun getStoredPassphrase(): String = passphrase
}