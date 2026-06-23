package com.seadrop.app

import android.Manifest
import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.Uri
import android.net.wifi.WifiManager
import android.net.wifi.WifiNetworkSuggestion
import android.os.Binder
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.provider.Settings
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import com.seadrop.app.network.TcpClient
import com.seadrop.app.notification.NotificationHelper
import com.seadrop.app.transfer.TransferManager
import com.seadrop.app.ble.BleScanner
import com.seadrop.app.storage.SecurePrefs
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket

class SeaDropService : Service() {

    companion object {
        const val ACTION_ACCEPT_INCOMING = "com.seadrop.app.ACCEPT_INCOMING"
        const val ACTION_DECLINE_INCOMING = "com.seadrop.app.DECLINE_INCOMING"
        const val EXTRA_TRANSFER_INFO = "transfer_info"
        private const val NOTIF_ID_STATUS = 1
    }

    inner class LocalBinder : Binder() {
        fun getService(): SeaDropService = this@SeaDropService
    }

    private val binder = LocalBinder()
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private lateinit var notificationHelper: NotificationHelper
    private lateinit var tcpClient: TcpClient
    private lateinit var transferManager: TransferManager
    private lateinit var bleScanner: BleScanner
    private lateinit var securePrefs: SecurePrefs
    
    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock? = null

    private val networkChannel = Channel<Network>(Channel.CONFLATED)
    private var networkCallback: ConnectivityManager.NetworkCallback? = null

    val statusText: String
        get() = tcpClient.statusText

    override fun onCreate() {
        super.onCreate()

        securePrefs = SecurePrefs(this)
        notificationHelper = NotificationHelper(this)

        tcpClient = TcpClient(
            context = this,
            scope = serviceScope,
            securePrefs = securePrefs,
            onStatusUpdate = { text ->
                notificationHelper.updateStatusNotification(text)
            },
            onIncomingNotify = { line ->
                val parts = line.split(" ")
                if (parts.size >= 3) {
                    val fileName = parts[1]
                    val fileSize = parts[2].toLongOrNull() ?: 0L
                    notificationHelper.showIncomingNotification(fileName, fileSize, line)
                }
            },
            onPullRequest = { fileName, fileSize ->
                serviceScope.launch {
                    val file = tcpClient.pullFile(fileName, fileSize)
                    if (file != null) {
                        notificationHelper.showReceivedNotification(fileName, file)
                    }
                }
            },
            onFileSent = { name, size ->
                notificationHelper.updateStatusNotification("Sent: $name")
            },
            onFileReceived = { name, file ->
                notificationHelper.showReceivedNotification(name, file)
            }
        )

        transferManager = TransferManager(
            context = this,
            tcpClient = tcpClient,
            securePrefs = securePrefs,
            onStatusUpdate = { notificationHelper.updateStatusNotification(it) },
            onFileSent = { name, size -> },
            onFileReceived = { name, file -> notificationHelper.showReceivedNotification(name, file) }
        )

        bleScanner = BleScanner(
            context = this,
            onDeviceFound = { name, rssi ->
                val tier = when {
                    rssi > -55 -> "CLOSE"
                    rssi >= -70 -> "MEDIUM"
                    else -> "FAR"
                }
                notificationHelper.updateStatusNotification("BLE: $name ($tier)")
            },
            onRegistrationModeDetected = { ssid ->
                notificationHelper.updateStatusNotification("SeaDrop $ssid detected, connecting…")
            }
        )
    }

    override fun onBind(intent: Intent?): IBinder = binder

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // 1. startForeground with initial notification "SeaDrop starting..."
        val initialNotif = notificationHelper.buildStatusNotification("SeaDrop starting...")
        startForeground(NOTIF_ID_STATUS, initialNotif)

        // 2. Check ACCESS_FINE_LOCATION granted. If not: update notification & return
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.ACCESS_FINE_LOCATION) != PackageManager.PERMISSION_GRANTED) {
            val pendingIntent = PendingIntent.getActivity(
                this, 0,
                Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                    data = Uri.fromParts("package", packageName, null)
                },
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )
            val notif = notificationHelper.buildStatusNotification("Location permission needed — tap to fix", pendingIntent)
            val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            nm.notify(NOTIF_ID_STATUS, notif)
            return START_STICKY
        }

        // 3. Check NEARBY_WIFI_DEVICES granted on API 33+. Same handling if missing.
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.NEARBY_WIFI_DEVICES) != PackageManager.PERMISSION_GRANTED) {
                val pendingIntent = PendingIntent.getActivity(
                    this, 0,
                    Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                        data = Uri.fromParts("package", packageName, null)
                    },
                    PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
                )
                val notif = notificationHelper.buildStatusNotification("Nearby devices permission needed — tap to fix", pendingIntent)
                val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
                nm.notify(NOTIF_ID_STATUS, notif)
                return START_STICKY
            }
        }

        // 4. Check battery optimization exemption. If not exempt: update notification & return
        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        if (!pm.isIgnoringBatteryOptimizations(packageName)) {
            val pendingIntent = PendingIntent.getActivity(
                this, 0,
                Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
                    data = Uri.parse("package:$packageName")
                },
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )
            val notif = notificationHelper.buildStatusNotification("Battery optimization must be disabled — tap to fix", pendingIntent)
            val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            nm.notify(NOTIF_ID_STATUS, notif)
            return START_STICKY
        }

        // 5. Acquire WifiManager.createWifiLock
        val wm = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        if (wifiLock == null) {
            wifiLock = wm.createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "SeaDrop:WifiLock")
            wifiLock?.acquire()
        }

        // 6. Acquire WakeLock
        if (wakeLock == null) {
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "SeaDrop:WakeLock")
            wakeLock?.acquire()
        }

        // 7. Call wifiManager.addNetworkSuggestions
        val ssid = securePrefs.getString("seadrop_ssid") ?: ""
        val passphrase = securePrefs.getString("seadrop_pass") ?: ""
        if (ssid.isNotEmpty() && passphrase.isNotEmpty()) {
            val suggestion = WifiNetworkSuggestion.Builder()
                .setSsid(ssid)
                .setWpa2Passphrase(passphrase)
                .build()
            val status = wm.addNetworkSuggestions(listOf(suggestion))
            if (status == WifiManager.STATUS_NETWORK_SUGGESTIONS_ERROR_ADD_NOT_ALLOWED) {
                val pendingIntent = PendingIntent.getActivity(
                    this, 0,
                    Intent(Settings.ACTION_WIFI_SETTINGS),
                    PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
                )
                val notif = notificationHelper.buildStatusNotification("WiFi permission revoked — tap to fix", pendingIntent)
                val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
                nm.notify(NOTIF_ID_STATUS, notif)
                return START_STICKY
            }
        }

        // 8. Start BLE scan
        bleScanner.startScanning()

        // Setup Network Monitoring callback for our channel
        setupNetworkCallback()

        // 9. Launch maintainConnection() coroutine on Dispatchers.IO
        serviceScope.launch(Dispatchers.IO) {
            maintainConnection()
        }

        // Process incoming intents
        if (intent != null) {
            when (intent.action) {
                ACTION_ACCEPT_INCOMING -> {
                    val info = intent.getStringExtra(EXTRA_TRANSFER_INFO) ?: ""
                    transferManager.acceptIncoming(info)
                }
                ACTION_DECLINE_INCOMING -> {
                    tcpClient.sendCancel()
                }
            }
        }

        return START_STICKY
    }

    private fun setupNetworkCallback() {
        if (networkCallback != null) return
        val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .build()
            
        networkCallback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                Log.d("SeaDropService", "Network callback: onAvailable: $network")
                networkChannel.trySend(network)
            }
        }
        cm.registerNetworkCallback(request, networkCallback!!)
    }

    private suspend fun waitForSeaDropNetwork(): Network {
        return networkChannel.receive()
    }

    private suspend fun maintainConnection() {
        val connectivityManager = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        while (true) {
            var activeSocket: Socket? = null
            try {
                notificationHelper.updateStatusNotification("Searching for SeaDrop network…")
                val network = waitForSeaDropNetwork()
                
                connectivityManager.bindProcessToNetwork(network)
                activeSocket = Socket()
                network.bindSocket(activeSocket)
                
                activeSocket.keepAlive = true
                activeSocket.soTimeout = 15000
                
                notificationHelper.updateStatusNotification("Connecting to SeaDrop…")
                activeSocket.connect(InetSocketAddress("192.168.4.1", 4242), 5000)
                
                tcpClient.startBoundSocket(activeSocket)
                notificationHelper.updateStatusNotification("SeaDrop connected")
                
                tcpClient.awaitDisconnection()
            } catch (e: IOException) {
                Log.e("SeaDropService", "Connection error in maintainConnection: ${e.message}")
                activeSocket?.close()
                connectivityManager.bindProcessToNetwork(null)
                notificationHelper.updateStatusNotification("SeaDrop not found — searching…")
                delay(3000)
            }
        }
    }

    override fun onDestroy() {
        tcpClient.stop()
        transferManager.stop()
        bleScanner.stopScanning()
        
        val cm = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        networkCallback?.let { cm.unregisterNetworkCallback(it) }
        networkCallback = null
        
        releaseWifiLock()
        releaseWakeLock()
        super.onDestroy()
    }

    private fun releaseWifiLock() {
        wifiLock?.release()
        wifiLock = null
    }

    private fun releaseWakeLock() {
        wakeLock?.release()
        wakeLock = null
    }

    fun enqueue(path: String, filename: String, mimeType: String) {
        transferManager.sendFile(path, filename, mimeType)
    }

    fun isRegistered(): Boolean = securePrefs.hasToken()
    fun getDeviceName(): String = securePrefs.getString("device_name") ?: "Android"
    fun getAuthToken(): String = securePrefs.getToken()
    fun getSeaDropSsid(): String = securePrefs.getString("seadrop_ssid") ?: ""
}
