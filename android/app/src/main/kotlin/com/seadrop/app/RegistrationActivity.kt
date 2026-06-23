package com.seadrop.app

import android.Manifest
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.Uri
import android.net.wifi.WifiManager
import android.net.wifi.WifiNetworkSpecifier
import android.net.wifi.WifiNetworkSuggestion
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.util.Log
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.animation.core.*
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import com.seadrop.app.network.WifiManager as SeaDropWifiManager
import com.seadrop.app.registration.RegistrationManager
import com.seadrop.app.storage.SecurePrefs
import com.seadrop.app.ble.BleScanner
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.net.InetSocketAddress
import java.net.Socket

class RegistrationActivity : ComponentActivity() {

    private val viewModel: RegistrationViewModel by viewModels()
    private var wifiManager: SeaDropWifiManager? = null
    private var registrationManager: RegistrationManager? = null
    private var securePrefs: SecurePrefs? = null
    private var bleScanner: BleScanner? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Initialize globally consistent preferences
        SeaDropPrefs.init(applicationContext)

        securePrefs = SecurePrefs(this)
        wifiManager = SeaDropWifiManager(this, securePrefs!!) { onBlacklisted() }
        
        registrationManager = RegistrationManager(
            context = this,
            securePrefs = securePrefs!!,
            onRegistrationSuccess = { onRegistrationSuccess() },
            onRegistrationFailure = { msg -> onRegistrationFailure(msg) }
        )

        bleScanner = BleScanner(
            context = this,
            onDeviceFound = { _, _ -> },
            onRegistrationModeDetected = { ssid ->
                runOnUiThread {
                    if (viewModel.softApSsid.isEmpty()) {
                        viewModel.softApSsid = ssid
                        viewModel.status = "SeaDrop device found: $ssid"
                        viewModel.bleDetected = true
                    }
                }
            }
        )

        setContent {
            SeaDropTheme {
                val navController = rememberNavController()
                NavHost(navController, startDestination = "welcome") {
                    composable("welcome") {
                        WelcomeScreen(onNext = { navController.navigate("permissions") })
                    }
                    composable("permissions") {
                        PermissionScreen(viewModel, onNext = { navController.navigate("power_on") })
                    }
                    composable("power_on") {
                        PowerOnScreen(viewModel, bleScanner) { navController.navigate("connecting") }
                    }
                    composable("connecting") {
                        ConnectingScreen(viewModel, registrationManager) { navController.navigate("name_device") }
                    }
                    composable("name_device") {
                        NameDeviceScreen(viewModel, registrationManager) { navController.navigate("wifi_suggestion") }
                    }
                    composable("wifi_suggestion") {
                        WifiSuggestionScreen(viewModel, registrationManager) { navController.navigate("verify") }
                    }
                    composable("verify") {
                        VerifyScreen(viewModel, registrationManager) { navController.navigate("done") }
                    }
                    composable("done") {
                        DoneScreen { finish() }
                    }
                }
            }
        }
    }

    override fun onResume() {
        super.onResume()
        updatePermissionStates()
    }

    private fun updatePermissionStates() {
        viewModel.locationGranted = ContextCompat.checkSelfPermission(
            this, Manifest.permission.ACCESS_FINE_LOCATION
        ) == PackageManager.PERMISSION_GRANTED

        viewModel.nearbyDevicesGranted = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            ContextCompat.checkSelfPermission(
                this, Manifest.permission.NEARBY_WIFI_DEVICES
            ) == PackageManager.PERMISSION_GRANTED
        } else {
            true
        }

        val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
        viewModel.batteryExempt = pm.isIgnoringBatteryOptimizations(packageName)
    }

    private fun onRegistrationSuccess() {
        runOnUiThread {
            viewModel.registrationSuccess = true
            viewModel.status = "Registered. Ready for transfers."
        }
    }

    private fun onRegistrationFailure(msg: String) {
        runOnUiThread {
            viewModel.registrationSuccess = false
            viewModel.status = "Registration failed: $msg"
        }
    }

    private fun onBlacklisted() {
        runOnUiThread {
            viewModel.status = "SeaDrop disconnected — reconnect to continue"
        }
    }
}

class RegistrationViewModel : androidx.lifecycle.ViewModel() {
    var deviceName by mutableStateOf(Build.MODEL)
    var status by mutableStateOf("")
    var registrationSuccess by mutableStateOf(false)
    var locationGranted by mutableStateOf(false)
    var nearbyDevicesGranted by mutableStateOf(false)
    var batteryExempt by mutableStateOf(false)
    var connecting by mutableStateOf(false)
    var wifiSuggestionAdded by mutableStateOf(false)
    var verificationChecks by mutableStateOf(Triple(false, false, false))

    var softApSsid by mutableStateOf("")
    var bleDetected by mutableStateOf(false)
}

// ── Design Assets ─────────────────────────────────────────────────────────────
val YoungSerif = FontFamily(
    Font(R.font.youngserif_regular, FontWeight.Normal)
)

val Inter = FontFamily(
    Font(R.font.inter_regular, FontWeight.Normal),
    Font(R.font.inter_medium, FontWeight.Medium),
    Font(R.font.inter_semibold, FontWeight.SemiBold)
)

@Composable
fun SeaDropTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = lightColorScheme(
            primary = Color(0xFFE85D00),
            onPrimary = Color(0xFFFFFFFF),
            background = Color(0xFFFEFEFE),
            onBackground = Color(0xFF1A1208),
            surface = Color(0xFFE8E8E8),
            onSurface = Color(0xFF1A1208),
            surfaceVariant = Color(0xFF293548),
            onSurfaceVariant = Color(0xFF888888)
        ),
        typography = Typography(
            headlineLarge = TextStyle(fontFamily = YoungSerif, fontSize = 36.sp),
            titleLarge = TextStyle(fontFamily = YoungSerif, fontSize = 24.sp),
            bodyLarge = TextStyle(fontFamily = Inter, fontSize = 16.sp),
            bodyMedium = TextStyle(fontFamily = Inter, fontSize = 14.sp),
            labelLarge = TextStyle(fontFamily = Inter, fontSize = 14.sp, fontWeight = FontWeight.Medium)
        ),
        content = content
    )
}

// ── Screen 1: Welcome ─────────────────────────────────────────────────────────
@Composable
fun WelcomeScreen(onNext: () -> Unit) {
    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(0xFFFEFEFE))
            .padding(48.dp)
    ) {
        Column(
            modifier = Modifier.align(Alignment.Center),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Text(
                text = "SeaDrop",
                style = MaterialTheme.typography.headlineLarge,
                color = Color(0xFFE85D00)
            )
            Spacer(modifier = Modifier.height(24.dp))
            Text(
                text = "Transfer files between your phone and laptop. No internet switching. Ever.",
                style = MaterialTheme.typography.bodyLarge,
                color = Color(0xFF888888),
                textAlign = TextAlign.Center
            )
            Spacer(modifier = Modifier.height(48.dp))
            Button(
                onClick = onNext,
                modifier = Modifier.height(48.dp),
                shape = RoundedCornerShape(12.dp),
                colors = ButtonDefaults.buttonColors(containerColor = Color(0xFFE85D00))
            ) {
                Text("Set Up SeaDrop", style = MaterialTheme.typography.labelLarge, color = Color.White)
            }
        }
    }
}

// ── Screen 2: Permissions ─────────────────────────────────────────────────────
@Composable
fun PermissionScreen(viewModel: RegistrationViewModel, onNext: () -> Unit) {
    val allGranted = viewModel.locationGranted && viewModel.nearbyDevicesGranted && viewModel.batteryExempt
    val context = LocalContext.current

    val locationLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestPermission()
    ) { granted ->
        viewModel.locationGranted = granted
    }

    val nearbyLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestPermission()
    ) { granted ->
        viewModel.nearbyDevicesGranted = granted
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(0xFFFEFEFE))
            .padding(48.dp)
    ) {
        Column(
            modifier = Modifier.align(Alignment.Center),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Text(
                text = "Permissions",
                style = MaterialTheme.typography.titleLarge,
                color = Color(0xFF1A1208)
            )
            Spacer(modifier = Modifier.height(32.dp))

            PermissionRow(
                label = "Location Permission",
                description = "Required by Android to manage WiFi networks.",
                granted = viewModel.locationGranted,
                onRequest = {
                    locationLauncher.launch(Manifest.permission.ACCESS_FINE_LOCATION)
                }
            )
            Spacer(modifier = Modifier.height(16.dp))

            PermissionRow(
                label = "Nearby Devices",
                description = "Required for BLE scanner discovery.",
                granted = viewModel.nearbyDevicesGranted,
                onRequest = {
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                        nearbyLauncher.launch(Manifest.permission.NEARBY_WIFI_DEVICES)
                    }
                }
            )
            Spacer(modifier = Modifier.height(16.dp))

            PermissionRow(
                label = "Ignore Battery Optimization",
                description = "Required to keep transfer background service alive.",
                granted = viewModel.batteryExempt,
                onRequest = {
                    val batteryIntent = Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
                        data = Uri.parse("package:${context.packageName}")
                    }
                    context.startActivity(batteryIntent)
                }
            )

            Spacer(modifier = Modifier.height(48.dp))
            Button(
                onClick = onNext,
                enabled = allGranted,
                modifier = Modifier.height(48.dp),
                shape = RoundedCornerShape(12.dp),
                colors = ButtonDefaults.buttonColors(
                    containerColor = if (allGranted) Color(0xFFE85D00) else Color(0xFF293548)
                )
            ) {
                Text("Next", style = MaterialTheme.typography.labelLarge, color = Color.White)
            }
        }
    }
}

@Composable
fun PermissionRow(label: String, description: String, granted: Boolean, onRequest: () -> Unit) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 4.dp)
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = label,
                style = MaterialTheme.typography.bodyLarge,
                fontWeight = FontWeight.SemiBold,
                color = Color(0xFF1A1208)
            )
            Text(
                text = description,
                style = MaterialTheme.typography.bodyMedium,
                color = Color(0xFF888888)
            )
        }
        Spacer(modifier = Modifier.width(16.dp))
        if (granted) {
            Text("✓", color = Color(0xFF22C55E), fontSize = 24.sp, fontWeight = FontWeight.Bold)
        } else {
            Button(
                onClick = onRequest,
                shape = RoundedCornerShape(12.dp),
                colors = ButtonDefaults.buttonColors(containerColor = Color(0xFFE85D00))
            ) {
                Text("Grant", style = MaterialTheme.typography.labelLarge)
            }
        }
    }
}

// ── Screen 3: Power On SeaDrop ────────────────────────────────────────────────
@Composable
fun PowerOnScreen(viewModel: RegistrationViewModel, bleScanner: BleScanner?, onNext: () -> Unit) {
    DisposableEffect(Unit) {
        bleScanner?.startScanning()
        onDispose {
            bleScanner?.stopScanning()
        }
    }

    LaunchedEffect(viewModel.bleDetected) {
        if (viewModel.bleDetected) {
            onNext()
        }
    }

    val infiniteTransition = rememberInfiniteTransition(label = "pulse")
    val pulseScale by infiniteTransition.animateFloat(
        initialValue = 0.8f,
        targetValue = 1.2f,
        animationSpec = infiniteRepeatable(
            animation = tween(1000, easing = LinearEasing),
            repeatMode = RepeatMode.Reverse
        ),
        label = "pulse"
    )

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(0xFFFEFEFE))
            .padding(48.dp)
    ) {
        Column(
            modifier = Modifier.align(Alignment.Center),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Box(
                modifier = Modifier
                    .size(80.dp)
                    .scale(pulseScale)
                    .background(Color(0xFFE85D00), shape = CircleShape)
            )
            Spacer(modifier = Modifier.height(32.dp))
            Text(
                text = "Power on SeaDrop",
                style = MaterialTheme.typography.titleLarge,
                color = Color(0xFF1A1208)
            )
            Spacer(modifier = Modifier.height(16.dp))
            Text(
                text = "The screen should show REGISTRATION MODE.",
                style = MaterialTheme.typography.bodyLarge,
                color = Color(0xFF888888),
                textAlign = TextAlign.Center
            )
        }
    }
}

// ── Screen 4: Connecting ──────────────────────────────────────────────────────
@Composable
fun ConnectingScreen(viewModel: RegistrationViewModel, regManager: RegistrationManager?, onNext: () -> Unit) {
    var errorMsg by remember { mutableStateOf("") }
    
    fun startConnecting() {
        errorMsg = ""
        viewModel.connecting = true
        regManager?.connectToSoftAp(
            ssid = viewModel.softApSsid,
            onConnected = {
                viewModel.connecting = false
                onNext()
            },
            onFailure = { err ->
                viewModel.connecting = false
                errorMsg = err
            }
        )
    }

    LaunchedEffect(Unit) {
        startConnecting()
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(0xFFFEFEFE))
            .padding(48.dp)
    ) {
        Column(
            modifier = Modifier.align(Alignment.Center),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Text(
                text = "Connecting to SeaDrop…",
                style = MaterialTheme.typography.titleLarge,
                color = Color(0xFF1A1208)
            )
            Spacer(modifier = Modifier.height(24.dp))
            
            if (viewModel.connecting) {
                CircularProgressIndicator(color = Color(0xFFE85D00))
            } else if (errorMsg.isNotEmpty()) {
                Text(errorMsg, color = Color.Red, textAlign = TextAlign.Center)
                Spacer(modifier = Modifier.height(24.dp))
                Button(
                    onClick = { startConnecting() },
                    shape = RoundedCornerShape(12.dp),
                    colors = ButtonDefaults.buttonColors(containerColor = Color(0xFFE85D00))
                ) {
                    Text("Try Again", style = MaterialTheme.typography.labelLarge)
                }
            }
        }
    }
}

// ── Screen 5: Name This Device ────────────────────────────────────────────────
@Composable
fun NameDeviceScreen(viewModel: RegistrationViewModel, regManager: RegistrationManager?, onNext: () -> Unit) {
    var name by remember { mutableStateOf(viewModel.deviceName) }
    var errorMsg by remember { mutableStateOf("") }
    var registering by remember { mutableStateOf(false) }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(0xFFFEFEFE))
            .padding(48.dp)
    ) {
        Column(
            modifier = Modifier.align(Alignment.Center),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Text(
                text = "What should SeaDrop call this phone?",
                style = MaterialTheme.typography.titleLarge,
                color = Color(0xFF1A1208)
            )
            Spacer(modifier = Modifier.height(32.dp))
            OutlinedTextField(
                value = name,
                onValueChange = { name = it },
                label = { Text("Device name") },
                singleLine = true,
                modifier = Modifier.fillMaxWidth()
            )
            Spacer(modifier = Modifier.height(24.dp))
            
            if (registering) {
                CircularProgressIndicator(color = Color(0xFFE85D00))
            } else {
                if (errorMsg.isNotEmpty()) {
                    Text(errorMsg, color = Color.Red, textAlign = TextAlign.Center)
                    Spacer(modifier = Modifier.height(16.dp))
                }
                Button(
                    onClick = {
                        registering = true
                        errorMsg = ""
                        regManager?.registerDevice(
                            deviceName = name,
                            onRegistered = {
                                viewModel.deviceName = name
                                registering = false
                                onNext()
                            },
                            onFailure = { err ->
                                registering = false
                                errorMsg = err
                            }
                        )
                    },
                    enabled = name.isNotBlank(),
                    modifier = Modifier.height(48.dp),
                    shape = RoundedCornerShape(12.dp),
                    colors = ButtonDefaults.buttonColors(containerColor = Color(0xFFE85D00))
                ) {
                    Text("Register", style = MaterialTheme.typography.labelLarge, color = Color.White)
                }
            }
        }
    }
}

// ── Screen 6: WiFi Suggestion Approval ────────────────────────────────────────
@Composable
fun WifiSuggestionScreen(viewModel: RegistrationViewModel, regManager: RegistrationManager?, onNext: () -> Unit) {
    val context = LocalContext.current
    var errorMsg by remember { mutableStateOf("") }
    var approved by remember { mutableStateOf(false) }

    fun addSuggestion() {
        errorMsg = ""
        regManager?.registerWifiSuggestion(
            onSuccess = {
                // System displays network suggestion pop up
            },
            onFailure = { err ->
                errorMsg = err
            }
        )
    }

    DisposableEffect(context) {
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(ctx: Context, intent: Intent) {
                if (intent.action == WifiManager.ACTION_WIFI_NETWORK_SUGGESTION_POST_CONNECTION) {
                    viewModel.wifiSuggestionAdded = true
                    approved = true
                }
            }
        }
        val filter = IntentFilter(WifiManager.ACTION_WIFI_NETWORK_SUGGESTION_POST_CONNECTION)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            context.registerReceiver(receiver, filter, Context.RECEIVER_EXPORTED)
        } else {
            context.registerReceiver(receiver, filter)
        }
        
        onDispose {
            try {
                context.unregisterReceiver(receiver)
            } catch (_: Exception) {}
        }
    }

    LaunchedEffect(Unit) {
        addSuggestion()
    }

    LaunchedEffect(approved) {
        if (approved) {
            kotlinx.coroutines.delay(1000)
            onNext()
        }
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(0xFFFEFEFE))
            .padding(48.dp)
    ) {
        Column(
            modifier = Modifier.align(Alignment.Center),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Text(
                text = "Allow SeaDrop to connect automatically",
                style = MaterialTheme.typography.titleLarge,
                color = Color(0xFF1A1208),
                textAlign = TextAlign.Center
            )
            Spacer(modifier = Modifier.height(24.dp))
            Text(
                text = "Android will ask for permission once. After that, SeaDrop connects silently whenever it is nearby.",
                style = MaterialTheme.typography.bodyLarge,
                color = Color(0xFF888888),
                textAlign = TextAlign.Center
            )
            Spacer(modifier = Modifier.height(32.dp))
            
            if (approved) {
                Text("✓ Approved! Connecting to SeaDrop...", color = Color(0xFF22C55E), style = MaterialTheme.typography.bodyLarge)
            } else {
                CircularProgressIndicator(color = Color(0xFFE85D00))
                if (errorMsg.isNotEmpty()) {
                    Spacer(modifier = Modifier.height(16.dp))
                    Text(errorMsg, color = Color.Red)
                }
                Spacer(modifier = Modifier.height(32.dp))
                Button(
                    onClick = { addSuggestion() },
                    shape = RoundedCornerShape(12.dp),
                    colors = ButtonDefaults.buttonColors(containerColor = Color(0xFFE85D00))
                ) {
                    Text("Re-request Suggestion", style = MaterialTheme.typography.labelLarge)
                }
                
                Spacer(modifier = Modifier.height(16.dp))
                Button(
                    onClick = {
                        viewModel.wifiSuggestionAdded = true
                        onNext()
                    },
                    shape = RoundedCornerShape(12.dp),
                    colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF293548))
                ) {
                    Text("Proceed to Verification", style = MaterialTheme.typography.labelLarge)
                }
            }
        }
    }
}

// ── Screen 7: Verification ────────────────────────────────────────────────────
@Composable
fun VerifyScreen(viewModel: RegistrationViewModel, regManager: RegistrationManager?, onNext: () -> Unit) {
    val context = LocalContext.current
    var check1 by remember { mutableStateOf<Boolean?>(null) }
    var check2 by remember { mutableStateOf<Boolean?>(null) }
    var check3 by remember { mutableStateOf<Boolean?>(null) }
    var running by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    fun runChecks() {
        running = true
        check1 = null
        check2 = null
        check3 = null

        val securePrefs = SecurePrefs(context)
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        
        // Relinquish temporary SoftAP direct network binding
        regManager?.cleanupNetwork(cm)

        scope.launch(Dispatchers.IO) {
            // Check 1: Token exists
            val token = securePrefs.getToken()
            val hasToken = token.isNotEmpty()
            launch(Dispatchers.Main) { check1 = hasToken }

            // Check 2: Suggestion registered
            val wm = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
            val ssid = securePrefs.getString("seadrop_ssid") ?: ""
            val suggestionExists = wm.networkSuggestions.any { it.ssid == ssid }
            launch(Dispatchers.Main) { check2 = suggestionExists }

            // Check 3: Default internet route works
            var internetOk = false
            try {
                val socket = Socket()
                socket.connect(InetSocketAddress("connectivity-check.ubuntu.com", 80), 5000)
                socket.close()
                internetOk = true
            } catch (e: Exception) {
                Log.e("VerifyScreen", "Internet check failed: ${e.message}")
            }
            launch(Dispatchers.Main) { check3 = internetOk }

            launch(Dispatchers.Main) {
                running = false
                viewModel.verificationChecks = Triple(hasToken, suggestionExists, internetOk)
                if (hasToken && suggestionExists && internetOk) {
                    kotlinx.coroutines.delay(1500)
                    onNext()
                }
            }
        }
    }

    LaunchedEffect(Unit) {
        runChecks()
    }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(0xFFFEFEFE))
            .padding(48.dp)
    ) {
        Column(
            modifier = Modifier.align(Alignment.Center),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Text(
                text = "Verifying",
                style = MaterialTheme.typography.titleLarge,
                color = Color(0xFF1A1208)
            )
            Spacer(modifier = Modifier.height(32.dp))

            VerifyCheckRow("SeaDrop registered", check1)
            Spacer(modifier = Modifier.height(16.dp))
            VerifyCheckRow("WiFi suggestion added", check2)
            Spacer(modifier = Modifier.height(16.dp))
            VerifyCheckRow("Internet reachable", check3)

            Spacer(modifier = Modifier.height(48.dp))

            if (!running) {
                val allPassed = check1 == true && check2 == true && check3 == true
                if (!allPassed) {
                    Button(
                        onClick = { runChecks() },
                        shape = RoundedCornerShape(12.dp),
                        colors = ButtonDefaults.buttonColors(containerColor = Color(0xFFE85D00))
                    ) {
                        Text("Retry Checks", style = MaterialTheme.typography.labelLarge)
                    }
                }
            } else {
                CircularProgressIndicator(color = Color(0xFFE85D00))
            }
        }
    }
}

@Composable
fun VerifyCheckRow(label: String, checked: Boolean?) {
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier.fillMaxWidth()
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyLarge,
            color = when (checked) {
                true -> Color(0xFF22C55E)
                false -> Color.Red
                null -> Color(0xFF888888)
            },
            modifier = Modifier.weight(1f)
        )
        when (checked) {
            true -> Text("✓", color = Color(0xFF22C55E), fontSize = 20.sp, fontWeight = FontWeight.Bold)
            false -> Text("✗", color = Color.Red, fontSize = 20.sp, fontWeight = FontWeight.Bold)
            null -> CircularProgressIndicator(modifier = Modifier.size(16.dp), strokeWidth = 2.dp)
        }
    }
}

// ── Screen 8: Done ────────────────────────────────────────────────────────────
@Composable
fun DoneScreen(onDone: () -> Unit) {
    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(0xFFFEFEFE))
            .padding(48.dp)
    ) {
        Column(
            modifier = Modifier.align(Alignment.Center),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Text(
                text = "All set.",
                style = MaterialTheme.typography.headlineLarge,
                color = Color(0xFFE85D00)
            )
            Spacer(modifier = Modifier.height(16.dp))
            Text(
                text = "SeaDrop runs in the background. Share any file and choose SeaDrop, or wait to receive files from your laptop.",
                style = MaterialTheme.typography.bodyLarge,
                color = Color(0xFF888888),
                textAlign = TextAlign.Center
            )
            Spacer(modifier = Modifier.height(48.dp))
            Button(
                onClick = onDone,
                modifier = Modifier.height(48.dp),
                shape = RoundedCornerShape(12.dp),
                colors = ButtonDefaults.buttonColors(containerColor = Color(0xFFE85D00))
            ) {
                Text("Done", style = MaterialTheme.typography.labelLarge, color = Color.White)
            }
        }
    }
}