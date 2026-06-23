package com.seadrop.app.registration

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiManager
import android.net.wifi.WifiNetworkSpecifier
import android.net.wifi.WifiNetworkSuggestion
import android.os.Build
import android.util.Log
import androidx.core.content.ContextCompat
import com.seadrop.app.storage.SecurePrefs
import com.seadrop.app.SeaDropPrefs
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.charset.Charset

class RegistrationManager(
    private val context: Context,
    private val securePrefs: SecurePrefs,
    private val onRegistrationSuccess: () -> Unit,
    private val onRegistrationFailure: (String) -> Unit
) {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var regNetwork: Network? = null
    private var networkCallback: ConnectivityManager.NetworkCallback? = null
    
    private var storedSsid: String = ""
    private var storedPassphrase: String = "seadrop2026"

    fun connectToSoftAp(ssid: String, passphrase: String = "seadrop2026", onConnected: () -> Unit, onFailure: (String) -> Unit) {
        if (ssid.isBlank()) {
            onFailure("SSID cannot be empty")
            return
        }
        storedSsid = ssid
        storedPassphrase = passphrase

        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        cleanupNetwork(cm)

        val specifier = WifiNetworkSpecifier.Builder()
            .setSsid(ssid)
            .setWpa2Passphrase(passphrase)
            .build()

        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .setNetworkSpecifier(specifier)
            .build()

        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                regNetwork = network
                cm.bindProcessToNetwork(network)
                scope.launch(Dispatchers.IO) {
                    try {
                        val socket = Socket()
                        network.bindSocket(socket)
                        socket.connect(InetSocketAddress("192.168.4.1", 4242), 5000)
                        socket.close()
                        scope.launch(Dispatchers.Main) {
                            onConnected()
                        }
                    } catch (e: Exception) {
                        scope.launch(Dispatchers.Main) {
                            onFailure("TCP connection to 192.168.4.1:4242 failed: ${e.message}")
                        }
                    }
                }
            }

            override fun onUnavailable() {
                scope.launch(Dispatchers.Main) {
                    onFailure("Could not connect to $ssid. Check that SeaDrop is in registration mode.")
                }
            }
        }
        networkCallback = cb
        cm.requestNetwork(request, cb)
    }

    fun registerDevice(deviceName: String, onRegistered: () -> Unit, onFailure: (String) -> Unit) {
        val network = regNetwork
        if (network == null) {
            onFailure("Not connected to SeaDrop WiFi")
            return
        }

        scope.launch(Dispatchers.IO) {
            try {
                val socket = Socket().apply {
                    soTimeout = 10000
                }
                network.bindSocket(socket)
                socket.connect(InetSocketAddress("192.168.4.1", 4242), 5000)
                
                socket.use { sock ->
                    val out = sock.getOutputStream()
                    val inp = sock.getInputStream()

                    val regCmd = "REG ANDROID $deviceName\n"
                    out.write(regCmd.toByteArray(Charset.forName("UTF-8")))
                    out.flush()

                    val response = readLine(inp) ?: throw IOException("No response from SeaDrop")
                    if (!response.startsWith("TOKEN ")) {
                        throw IOException("Unexpected response: $response")
                    }

                    val token = response.removePrefix("TOKEN ").trim()

                    persistRegistration(storedSsid, deviceName, storedPassphrase, token)

                    scope.launch(Dispatchers.Main) {
                        onRegistered()
                    }
                }
            } catch (e: Exception) {
                scope.launch(Dispatchers.Main) {
                    onFailure("Registration failed: ${e.message}")
                }
            }
        }
    }

    fun registerWifiSuggestion(onSuccess: () -> Unit, onFailure: (String) -> Unit) {
        val ssid = securePrefs.getString("seadrop_ssid") ?: ""
        val passphrase = securePrefs.getString("seadrop_pass") ?: ""
        if (ssid.isEmpty() || passphrase.isEmpty()) {
            onFailure("SSID or passphrase missing")
            return
        }

        val wm = context.getSystemService(Context.WIFI_SERVICE) as WifiManager
        val existing = wm.networkSuggestions
        val toRemove = existing.filter { it.ssid == ssid }
        if (toRemove.isNotEmpty()) {
            wm.removeNetworkSuggestions(toRemove)
        }

        val suggestion = WifiNetworkSuggestion.Builder()
            .setSsid(ssid)
            .setWpa2Passphrase(passphrase)
            .setIsAppInteractionRequired(false)
            .setPriority(999)
            .build()

        val status = wm.addNetworkSuggestions(listOf(suggestion))
        if (status == WifiManager.STATUS_NETWORK_SUGGESTIONS_SUCCESS ||
            status == WifiManager.STATUS_NETWORK_SUGGESTIONS_ERROR_ADD_DUPLICATE) {
            onSuccess()
        } else {
            onFailure("Failed to add WiFi suggestion: status = $status")
        }
    }

    fun persistRegistration(ssid: String, deviceName: String, passphrase: String, token: String) {
        SeaDropPrefs.deviceName = deviceName
        SeaDropPrefs.seadropSsid = ssid
        SeaDropPrefs.seadropPass = passphrase
        SeaDropPrefs.authToken = token
        SeaDropPrefs.regDone = true

        securePrefs.apply {
            putString("device_name", deviceName)
            putString("seadrop_ssid", ssid)
            putString("seadrop_pass", passphrase)
            putBoolean("reg_done", true)
            setToken(token)
        }
    }

    fun cleanupNetwork(cm: ConnectivityManager) {
        networkCallback?.let {
            try {
                cm.unregisterNetworkCallback(it)
            } catch (e: Exception) {
                Log.d("RegistrationManager", "Error unregistering callback: ${e.message}")
            }
        }
        networkCallback = null
        try {
            cm.bindProcessToNetwork(null)
        } catch (e: Exception) {
            Log.d("RegistrationManager", "Error resetting process network: ${e.message}")
        }
        regNetwork = null
    }

    private fun readLine(input: java.io.InputStream): String? {
        val sb = java.lang.StringBuilder()
        while (true) {
            val b = input.read()
            if (b < 0) return if (sb.isEmpty()) null else sb.toString()
            if (b.toChar() == '\n') return sb.toString().trimEnd('\r')
            sb.append(b.toChar())
        }
    }
}