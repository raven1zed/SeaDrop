package com.seadrop.app.network

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

class NetworkBinder(private val context: Context) {

    private val cm by lazy { context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager }
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var networkCallback: ConnectivityManager.NetworkCallback? = null
    private var monitorJob: Job? = null

    fun bindToNetwork(network: Network?) {
        if (network == null) {
            cm.bindProcessToNetwork(null)
            Log.d("NetworkBinder", "Unbound from network")
        } else {
            cm.bindProcessToNetwork(network)
            Log.i("NetworkBinder", "Bound to network: $network")
        }
    }

    fun startMonitoring(callback: (Network?) -> Unit) {
        monitorJob?.cancel()
        monitorJob = scope.launch {
            val request = NetworkRequest.Builder()
                .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
                .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
                .build()

            networkCallback = object : ConnectivityManager.NetworkCallback() {
                override fun onAvailable(network: Network) {
                    Log.i("NetworkBinder", "SeaDrop network available: $network")
                    bindToNetwork(network)
                    callback(network)
                }

                override fun onLost(network: Network) {
                    Log.w("NetworkBinder", "SeaDrop network lost")
                    bindToNetwork(null)
                    callback(null)
                }
            }

            cm.requestNetwork(request, networkCallback!!)
        }
    }

    fun stopMonitoring() {
        monitorJob?.cancel()
        networkCallback?.let { cm.unregisterNetworkCallback(it) }
        networkCallback = null
    }

    /**
     * Bind the process to a WiFi network whose SSID starts with "SeaDrop".
     * Used after BLE 0xFEAD detection to switch the app's traffic onto the
     * SeaDrop SoftAP without losing the user's home WiFi association.
     */
    fun bindToSeaDropNetwork() {
        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .setNetworkSpecifier("SSID:SeaDrop*")
            .build()

        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                Log.i("NetworkBinder", "Bound to SeaDrop network: $network")
                bindToNetwork(network)
            }
        }
        cm.requestNetwork(request, cb)
    }
}