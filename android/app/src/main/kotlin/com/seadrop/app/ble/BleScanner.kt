package com.seadrop.app.ble

import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.le.BluetoothLeScanner
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanFilter
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.Context
import android.os.Handler
import android.os.Looper
import android.os.ParcelUuid
import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

class BleScanner(
    private val context: Context,
    private val onDeviceFound: (String, Int) -> Unit,
    private val onRegistrationModeDetected: (String) -> Unit = {}
) {

    companion object {
        private const val UUID_SEADROP = "0000fead-0000-1000-8000-00805f9b34fb"
        private const val SCAN_INTERVAL_MS = 5_000L
        private const val SCAN_DURATION_MS = 1_000L
    }

    private val bluetoothAdapter by lazy { BluetoothAdapter.getDefaultAdapter() }
    private val bluetoothLeScanner by lazy { bluetoothAdapter?.bluetoothLeScanner }
    private val handler = Handler(Looper.getMainLooper())
    private var scanJob: Job? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var isScanning = false

    fun startScanning() {
        if (!isScanning && bluetoothLeScanner != null) {
            isScanning = true
            scanJob = scope.launch { scanLoop() }
        }
    }

    fun stopScanning() {
        isScanning = false
        scanJob?.cancel()
        try { bluetoothLeScanner?.stopScan(scanCallback) } catch (_: Exception) {}
    }

    private suspend fun scanLoop() {
        val filters = listOf(
            ScanFilter.Builder()
                .setServiceUuid(ParcelUuid.fromString(UUID_SEADROP))
                .build()
        )
        val settings = ScanSettings.Builder()
            .setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
            .setReportDelay(0)
            .build()
        
        while (isScanning) {
            bluetoothLeScanner?.startScan(filters, settings, scanCallback)
            delay(SCAN_DURATION_MS)
            bluetoothLeScanner?.stopScan(scanCallback)
            delay(SCAN_INTERVAL_MS - SCAN_DURATION_MS)
        }
    }

    private val scanCallback = object : ScanCallback() {
        override fun onScanResult(callbackType: Int, result: ScanResult) {
            val device: BluetoothDevice? = result.device
            val deviceName = device?.name ?: "Unknown"
            val rssi = result.rssi

            if (deviceName.startsWith("SeaDrop") ||
                (device?.uuids != null && device.uuids.any { it.uuid.toString() == UUID_SEADROP })) {
                handler.post {
                    onDeviceFound(deviceName, rssi)
                }
            }

            val ssid = parseRegistrationModeSsid(result)
            if (ssid != null) {
                handler.post {
                    onRegistrationModeDetected(ssid)
                }
            }
        }
    }

    private fun parseRegistrationModeSsid(result: ScanResult): String? {
        val scanRecord = result.scanRecord ?: return null
        val serviceData = scanRecord.getServiceData(ParcelUuid.fromString(UUID_SEADROP))
        if (serviceData != null && serviceData.size >= 2) {
            val regMode = serviceData[0].toInt() and 0xFF
            if (regMode == 1) {
                val ssidLen = serviceData.size - 2
                if (ssidLen > 0) {
                    return String(serviceData, 1, ssidLen, java.nio.charset.StandardCharsets.US_ASCII)
                }
            }
        }
        return null
    }
}