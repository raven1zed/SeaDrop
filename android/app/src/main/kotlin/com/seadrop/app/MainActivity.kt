package com.seadrop.app

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.ServiceConnection
import android.os.Build
import android.os.Bundle
import android.os.IBinder
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

class MainActivity : ComponentActivity() {

    private var service: SeaDropService? = null
    private var bound = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // Initialize globally consistent preferences
        SeaDropPrefs.init(applicationContext)

        // First launch → registration wizard
        if (!SeaDropPrefs.isRegistered) {
            startActivity(Intent(this, RegistrationActivity::class.java))
            finish()
            return
        }

        setContent {
            SeaDropSettingsTheme {
                SettingsScreen(service, bound, onReRegister = {
                    // Reset credentials
                    SeaDropPrefs.regDone = false
                    SeaDropPrefs.authToken = ""
                    SeaDropPrefs.seadropSsid = ""
                    SeaDropPrefs.seadropPass = ""
                    
                    // Redirect to wizard
                    startActivity(Intent(this@MainActivity, RegistrationActivity::class.java))
                    finish()
                })
            }
        }
        ensureServiceRunning()
    }

    override fun onStart() {
        super.onStart()
        if (SeaDropPrefs.isRegistered) {
            bindService(Intent(this, SeaDropService::class.java), connection, Context.BIND_AUTO_CREATE)
        }
    }

    override fun onStop() {
        if (bound) {
            unbindService(connection)
            bound = false
        }
        super.onStop()
    }

    private fun ensureServiceRunning() {
        val intent = Intent(this, SeaDropService::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(intent)
        } else {
            startService(intent)
        }
    }

    private val connection = object : ServiceConnection {
        override fun onServiceConnected(name: ComponentName, binder: IBinder) {
            service = (binder as SeaDropService.LocalBinder).getService()
            bound = true
        }
        override fun onServiceDisconnected(name: ComponentName) {
            service = null
            bound = false
        }
    }
}

@Composable
fun SeaDropSettingsTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = lightColorScheme(
            primary = Color(0xFFE85D00),
            onPrimary = Color.White,
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

@Composable
fun SettingsScreen(service: SeaDropService?, bound: Boolean, onReRegister: () -> Unit) {
    var deviceName by remember { mutableStateOf(SeaDropPrefs.deviceName) }
    var statusText by remember { mutableStateOf("Searching...") }
    val context = LocalContext.current

    LaunchedEffect(service, bound) {
        while (bound && service != null) {
            statusText = service.statusText
            kotlinx.coroutines.delay(1000)
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(Color(0xFFFEFEFE))
            .padding(48.dp),
        horizontalAlignment = Alignment.Start
    ) {
        Text(
            text = "SeaDrop",
            style = MaterialTheme.typography.headlineLarge,
            color = Color(0xFFE85D00)
        )
        Spacer(modifier = Modifier.height(8.dp))
        Text(
            text = "Status: $statusText",
            style = MaterialTheme.typography.bodyLarge,
            color = Color(0xFF888888)
        )
        Spacer(modifier = Modifier.height(32.dp))

        Text(
            text = "What should SeaDrop call this phone?",
            style = MaterialTheme.typography.bodyLarge,
            fontWeight = FontWeight.SemiBold,
            color = Color(0xFF1A1208)
        )
        Spacer(modifier = Modifier.height(12.dp))
        OutlinedTextField(
            value = deviceName,
            onValueChange = { deviceName = it },
            placeholder = { Text(Build.MODEL) },
            singleLine = true,
            modifier = Modifier.fillMaxWidth()
        )
        Spacer(modifier = Modifier.height(16.dp))
        Button(
            onClick = {
                if (deviceName.isNotBlank()) {
                    SeaDropPrefs.deviceName = deviceName
                    Toast.makeText(context, "Saved Device Name", Toast.LENGTH_SHORT).show()
                }
            },
            shape = RoundedCornerShape(12.dp),
            colors = ButtonDefaults.buttonColors(containerColor = Color(0xFFE85D00))
        ) {
            Text("Save Name", style = MaterialTheme.typography.labelLarge)
        }

        Spacer(modifier = Modifier.height(48.dp))
        
        Text(
            text = "Connection Details",
            style = MaterialTheme.typography.bodyLarge,
            fontWeight = FontWeight.SemiBold,
            color = Color(0xFF1A1208)
        )
        Spacer(modifier = Modifier.height(12.dp))
        Text(
            text = "SeaDrop SSID: ${SeaDropPrefs.seadropSsid}",
            style = MaterialTheme.typography.bodyMedium,
            color = Color(0xFF888888)
        )
        Spacer(modifier = Modifier.height(4.dp))
        Text(
            text = "App Version: 1.5.0",
            style = MaterialTheme.typography.bodyMedium,
            color = Color(0xFF888888)
        )

        Spacer(modifier = Modifier.weight(1f))

        Button(
            onClick = onReRegister,
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(12.dp),
            colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF293548))
        ) {
            Text("Re-register Device", style = MaterialTheme.typography.labelLarge, color = Color.White)
        }
    }
}