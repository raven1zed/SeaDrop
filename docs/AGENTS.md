# AGENTS.md — SeaDrop Development Contract

This file is the law. Every AI agent working on this codebase reads this file first and follows it completely. No exceptions. No creative interpretation. No adding things not listed here.

---

## What SeaDrop Is

SeaDrop is a production-quality file transfer ecosystem consisting of three codebases:

- `firmware/` — ESP32-WROOM-32 Arduino firmware
- `android/` — Android 16 (API 36) Kotlin + Jetpack Compose app
- `windows/` — Windows 11 24H2 C# .NET 8 WinUI 3 app

All three ship together under the same version number. A change to STP protocol commands requires simultaneous changes in all three.

---

## Non-Negotiable Rules

**Rule 1: Never write stubs.**
Every function must be fully implemented. No `// TODO`, no `throw new NotImplementedException()`, no `return null` placeholders. If you cannot implement something completely, say so explicitly instead of writing broken code.

**Rule 2: Never add unlisted APIs.**
The only network protocol is TCP port 4242 with STP commands (newline-terminated ASCII). No WebSocket. No HTTP beyond NCSI spoof. No REST. No SignalR. No gRPC. No MQTT. If it is not in the STP command table, it does not exist.

**Rule 3: Never swallow exceptions.**
Every catch block must log the exception and surface a user-visible notification or Serial output describing what failed and what the user should do. Silent catch blocks that do nothing are bugs.

**Rule 4: Never build UI before core works.**
The startup sequence must pass all gates before any UI beyond a loading indicator is shown. A BUILD SUCCESSFUL that crashes 3 seconds after launch is not a success.

**Rule 5: Never cross codebase boundaries.**
`firmware/` has no knowledge of Android or Windows internals. `android/` has no knowledge of Windows internals. `windows/` has no knowledge of Android internals. STP is the only interface between them. Do not move files between directories. Do not import cross-platform libraries.

**Rule 6: One file at a time.**
Complete one file fully. Verify it compiles and the relevant gate passes. Then move to the next file. Do not write five files simultaneously.

**Rule 7: BUILD SUCCESSFUL means nothing.**
The only valid success criteria is the feature working end-to-end on real hardware. Compilation is a prerequisite, not a result.

---

## Versioning

Format: `MAJOR.MINOR.PATCH`

- MAJOR: Breaking STP protocol change. All three codebases must update simultaneously.
- MINOR: New feature. Apps remain backward-compatible with older firmware.
- PATCH: Bug fix. No protocol or API changes.

Current version: `1.5.0`

### Git Workflow

```
main          — stable, tagged releases only
dev           — integration branch
feature/xxx   — individual feature branches
fix/xxx       — bug fix branches
```

Branch naming: `feature/windows-hotspot`, `fix/android-ble-crash`, `firmware/ncsi-spoof`

Commit format:
```
type(scope): short description

type: feat | fix | refactor | docs | test
scope: firmware | android | windows | stp
```

Examples:
```
feat(windows): implement HotspotManager with location permission gate
fix(android): catch SecurityException in BLE scanner startup
feat(stp): add VERSION_MISMATCH reject command
```

### GitHub Release Structure

```
Release: v1.5.0
├── SeaDrop-v1.5.0-firmware.zip       ← Arduino sketch folder
├── SeaDrop-v1.5.0-windows.msix       ← Self-signed MSIX
├── SeaDrop-v1.5.0-windows-cert.cer   ← Certificate for TrustedPeople store
├── SeaDrop-v1.5.0-android.apk        ← Sideload APK
└── install-windows.ps1               ← Installs cert + MSIX in one command
```

`install-windows.ps1`:
```powershell
# Run as Administrator
Import-Certificate -FilePath .\SeaDrop-v1.5.0-windows-cert.cer `
    -CertStoreLocation Cert:\LocalMachine\TrustedPeople
Add-AppxPackage -Path .\SeaDrop-v1.5.0-windows.msix
```

Version string in BLE payload and HELLO handshake. REJECT VERSION_MISMATCH if incompatible.

---

## STP Protocol v1.5

All commands: newline-terminated ASCII over TCP port 4242. Binary payloads follow immediately after the command line with byte length declared in the command. Connection is permanent — never closes unless SeaDrop loses power.

| Command | Direction | Description |
|---|---|---|
| `HELLO <type> <name> <token> <version>` | Client → ESP32 | type = ANDROID or WINDOWS |
| `HELLO_ACK <session_id> <version>` | ESP32 → Client | Session ID + firmware version |
| `PING` | Client → ESP32 | Keepalive every 10 seconds |
| `PONG` | ESP32 → Client | Response to PING |
| `CHANNEL <n>` | Windows → ESP32 | Home WiFi channel for SoftAP matching |
| `SEND <file> <bytes> <crc32> <mode>` | Either → ESP32 | mode = STREAM or BUFFER |
| `SEND_ACK <mode>` | ESP32 → Sender | Confirms mode |
| `SEND_DONE` | Sender → ESP32 | End of binary stream |
| `NOTIFY <file> <bytes>` | ESP32 → Receiver | File ready on SD (BUFFER mode) |
| `PULL <file>` | Receiver → ESP32 | Request SD file |
| `PULL_DATA <bytes>` | ESP32 → Receiver | Streams SD file |
| `PULL_DONE` | ESP32 → Receiver | End of SD stream |
| `ACK` | Any | Generic acknowledgement |
| `REJECT <reason>` | ESP32 → Client | See reason codes below |
| `CANCEL` | Any | Cancel active transfer |
| `STATUS` | Client → ESP32 | Request session state |
| `STATUS_RESP <state> <peer> <mode> <sd>` | ESP32 → Client | Includes sd_available flag |
| `REG <type> <name> <hs_ssid> <hs_pass>` | Client → ESP32 | Registration Mode only |
| `TOKEN <hex>` | ESP32 → Client | 16-byte random token |

REJECT reason codes: `AUTH_FAIL`, `SD_FULL`, `SD_UNAVAILABLE`, `NO_PEER`, `BUSY`, `CHECKSUM_FAIL`, `REG_CLOSED`, `VERSION_MISMATCH`

---

## Error Handling Contract

### Every error must:
1. Be caught at the specific site where it occurs
2. Log the full exception message and stack trace
3. Surface a user-visible notification or Serial output
4. Leave the app in a defined recoverable state
5. Never propagate uncaught to crash the process

### Specific error cases — mandatory handling:

**Windows:**

| Error | Where | Required handling |
|---|---|---|
| `WiFiAccessStatus` not Allowed | `SeaDropService.StartAsync()` | Toast: "SeaDrop needs location access — Settings > Privacy > Location". Set tray grey. Stop startup. Do not proceed. |
| `TetheringOperationStatus` not Success | `HotspotManager.StartTetheringAsync()` | Toast: "Hotspot failed: [specific status enum value]". Set tray grey. Stop startup. Do not proceed. |
| TCP `IOException` on connected socket | `TcpListener` read loop | Log exception. Call `StopTetheringAsync()`. Set tray grey. Update tooltip "SeaDrop not found". Start BLE scan. Primary WiFi resumes automatically when hotspot stops. |
| `SocketException` on bind | `TcpListener.StartAsync()` | Toast: "Port 4242 unavailable. Is another SeaDrop instance running?" Stop startup. |
| `BluetoothLEAdvertisementWatcher` error | Watcher `Stopped` event | Log status. Restart watcher after 5 second delay. Max 3 retries before toast notification. |
| Named pipe `IOException` | `SeaDropShell` pipe reader | Log. Restart pipe listener. Do not crash service. |
| `UnauthorizedAccessException` on file save | `FileHelper.SaveAsync()` | Toast: "Cannot save to Downloads\SeaDrop\ — check folder permissions". Do not crash. |
| `COMException` from WinUI thread | Any UI call | Catch, log, show ContentDialog with error details. Never crash the window. |

**Android:**

| Error | Where | Required handling |
|---|---|---|
| Missing `ACCESS_FINE_LOCATION` | `SeaDropService.onStartCommand()` | Update notification: "Location permission needed — tap to fix". PendingIntent to app permission settings. Return `START_STICKY`. Do not proceed to next gate. |
| Missing `NEARBY_WIFI_DEVICES` | `SeaDropService.onStartCommand()` | Update notification: "Nearby devices permission needed — tap to fix". Same pattern. |
| Battery optimization not exempt | `SeaDropService.onStartCommand()` | Update notification: "Battery optimization must be disabled — tap to fix". PendingIntent to `ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`. |
| `addNetworkSuggestions` returns error | `WifiManager.registerSuggestion()` | Update notification: "WiFi permission issue — tap to fix". Log specific error code. |
| `SecurityException` from BLE scan | `BleScanner.start()` | Log. Update notification: "Bluetooth permission denied". Do not crash service. |
| TCP `IOException` in `maintainConnection` | Catch block | `socket?.close()`. `bindProcessToNetwork(null)`. Update notification "SeaDrop not found — searching...". Delay 3000ms. Retry loop continues. |
| `NetworkCallback` timeout | `waitForSeaDropNetwork()` | After 30 seconds with no network: update notification "SeaDrop not responding — is it powered on?" Continue retry loop. |
| `FileNotFoundException` on share | `ShareActivity` | Finish immediately. Start service with error notification: "File not found — share again". |
| WifiNetworkSuggestion 24-hour blacklist | `NetworkCallback` LOSING state | Notification: "SeaDrop disconnected — tap once to reconnect". Deep link to system WiFi settings for SeaDrop SSID. |

**Firmware:**

| Error | Where | Required handling |
|---|---|---|
| SD card mount failure | `sd_buffer.begin()` | `Serial.println("[SD] Mount failed")`. Set `sd_available = false`. Continue boot. Never crash. |
| TFT init failure | `display.begin()` | Return false. Set `display_available = false`. Skip display and touch tasks. Continue boot. Never crash. |
| WiFi STA connection failure | `wifi_sta.begin()` | `Serial.println("[STA] Cannot connect to hotspot: [ssid]")`. Continue boot. SoftAP still runs for Android. |
| NVS key missing | Any `nvs_store.get()` | Return empty string. Caller checks for empty and enters Registration Mode if credentials missing. |
| TCP client auth failure | `tcp_server_task` HELLO parsing | Send `REJECT AUTH_FAIL`. Close socket. Free session slot. Log MAC address. |
| SD write failure mid-transfer | `sd_buffer_task` | Send `REJECT SD_FULL` or `REJECT CHECKSUM_FAIL`. Delete partial file. Update display STATE_ERROR. |
| CRC32 mismatch | Post-write verification | Send `REJECT CHECKSUM_FAIL`. Delete file. Do not deliver corrupted data. |

---

## Startup Sequence — Both Platforms

### Windows — mandatory order, no reordering

```
1. App.OnLaunched()
   → Initialize tray icon in GREY state
   → No window shown

2. SeaDropService.StartAsync() on background thread
   → Gate 1: WiFiAdapter.RequestAccessAsync()
     → If denied: toast notification, tray grey, STOP
   → Gate 2: HotspotManager.StartTetheringAsync()
     → Load SSID + passphrase from Credential Manager
     → If Credential Manager entry missing: open setup wizard
     → If TetheringOperationStatus != Success: toast, tray grey, STOP
   → Gate 3: BluetoothLEAdvertisementWatcher.Start()
     → Filter UUID: 0000FEAD-0000-1000-8000-00805F9B34FB
   → Gate 4: TcpListener.StartAsync() on hotspot interface port 4242
   → On ESP32 TCP inbound connect:
     → Send HELLO_ACK
     → Start PING loop every 10 seconds
     → Set tray icon GREEN
     → Check SQLite outbound queue
```

### Android — mandatory order, no reordering

```
1. SeaDropService.onStartCommand()
   → startForeground() immediately
   → Gate 1: ACCESS_FINE_LOCATION granted?
     → No: notification with fix link, return START_STICKY, STOP
   → Gate 2: NEARBY_WIFI_DEVICES granted?
     → No: notification with fix link, return START_STICKY, STOP
   → Gate 3: battery optimization exempt?
     → No: notification with fix link, return START_STICKY, STOP
   → Gate 4: wifiLock.acquire()
   → Gate 5: wakeLock.acquire()
   → Gate 6: wifiManager.addNetworkSuggestions()
   → Gate 7: bleScanner.start()
   → Gate 8: launch maintainConnection() on Dispatchers.IO
```

---

## Disconnect and Reconnect — Both Platforms

### Windows disconnect (SeaDrop powers off)

```
1. TCP IOException caught in TcpListener read loop
2. Log: "[TCP] Connection lost: [exception message]"
3. HotspotManager.StopTetheringAsync()
4. Set tray icon GREY
5. Update tray tooltip: "SeaDrop not found"
6. BluetoothLEAdvertisementWatcher.Start() — if not already running
7. Primary WiFi resumes automatically — hotspot stopping releases virtual adapter
8. No routing table cleanup needed
```

### Windows reconnect (SeaDrop powers on)

```
1. BLE watcher receives advertisement with UUID 0xFEAD
2. HotspotManager.StartTetheringAsync() with stored credentials
3. Wait for ESP32 TCP inbound connection
4. Send HELLO_ACK, start PING loop
5. Set tray icon GREEN
6. Check outbound queue
```

### Android disconnect (SeaDrop powers off)

```
1. TCP IOException caught in maintainConnection catch block
2. socket?.close()
3. connectivityManager.bindProcessToNetwork(null)
4. Update notification: "SeaDrop not found — searching..."
5. Delay 3000ms
6. Primary mobile data or home WiFi resumes as default route automatically
7. Loop continues — waitForSeaDropNetwork() suspends until AP reappears
```

### Android reconnect (SeaDrop powers on)

```
1. WifiNetworkSuggestion auto-connects to SeaDrop AP
2. waitForSeaDropNetwork() unblocks
3. bindProcessToNetwork(seadropNetwork)
4. network.bindSocket(socket)
5. TCP connect to 192.168.4.1:4242
6. sendHello()
7. Update notification: "SeaDrop connected"
```

---

## UI/UX Specification

### Design Tokens

```
Font title:   YoungSerif — must be bundled in assets
Font body:    Inter — must be bundled in assets
Accent:       #E85D00  (Space Orange)
Background:   #FEFEFE  (never pure #FFFFFF)
Surface:      #F5F5F5
Text primary: #1A1A1A
Text grey:    #888888
Shadow:       #1A1208  (never pure #000000)
Border:       #E0E0E0
Success:      #22C55E
Error:        #EF4444
```

### Windows UI Rules

- Tray icon is the primary UI surface. Main window is settings only.
- Main window supports maximize, minimize, restore, fullscreen via standard WinUI 3 window chrome.
- Window close button hides window. Does not exit app. App exits only from tray right-click Quit.
- Every button has a hover state, pressed state, and disabled state. No flat static buttons.
- Every async operation shows a progress indicator. No frozen UI during network calls.
- Setup wizard screens use full-window layout. No dialog boxes for wizard flow.
- Animations: fade transitions between wizard screens, 200ms duration, no easing function more complex than EaseInOut.

### Android UI Rules

- Jetpack Compose only. No XML layouts. No View system. No Fragment.
- Every screen uses `Scaffold` with consistent padding: 24dp horizontal, 32dp vertical.
- Bottom of every screen: primary action button full width, 56dp height, 8dp corner radius, Space Orange background, white Inter 16sp text.
- Secondary actions: text buttons only, no outlined buttons.
- Every async operation: `CircularProgressIndicator` in Space Orange, centered.
- Navigation: `NavHost` with named routes. Back button always works. No back stack issues.
- Foreground service notification: always visible, never dismissible while service runs.

### Windows Setup Wizard — 8 Screens

Each screen is a separate UserControl. NavFrame handles transitions.

**Screen 1 — Welcome**
```
[YoungSerif 48sp #E85D00] SeaDrop
[Inter 18sp #1A1A1A] Transfer files between your phone and laptop.
                     No internet switching. Ever.
[Space Orange button 48dp height] Get Started
```

**Screen 2 — Location Permission**
```
[YoungSerif 32sp] Location Access
[Inter 16sp] Required by Windows to control the WiFi hotspot.
             SeaDrop never tracks your location.
[Space Orange button] Grant Location Access
→ calls WiFiAdapter.RequestAccessAsync() directly
→ if denied: button label = "Try Again", show #EF4444 warning text
→ wizard cannot advance without Allowed
```

**Screen 3 — Starting Hotspot**
```
[YoungSerif 32sp] Starting SeaDrop Connection
[CircularProgressIndicator Space Orange]
[Inter 16sp #888888] Setting up your laptop as a relay point...
→ calls StartTetheringAsync() with generated SSID/pass
→ stores to Credential Manager
→ auto-advances on success
→ on failure: show TetheringOperationStatus value + "Try Again" button
```

**Screen 4 — Power On SeaDrop**
```
[YoungSerif 32sp] Power On SeaDrop
[Inter 16sp] The screen should show REGISTRATION MODE.
[Pulsing Space Orange circle animation]
→ BLE watcher scanning
→ auto-advances when UUID 0xFEAD detected with reg_mode = 1
```

**Screen 5 — Connecting**
```
[YoungSerif 32sp] Connecting to SeaDrop
[CircularProgressIndicator Space Orange]
→ WiFiAdapter.ConnectAsync() to SeaDrop SoftAP SSID from BLE payload
→ TCP connect to 192.168.4.1:4242
→ auto-advances on TCP established
→ on failure: "Connection failed — is SeaDrop powered on?" + "Try Again"
```

**Screen 6 — Name This Device**
```
[YoungSerif 32sp] Name This Laptop
[Inter 16sp #888888] SeaDrop will use this to identify your laptop.
[TextBox pre-filled with Environment.MachineName]
[Space Orange button] Register
→ sends: REG WINDOWS [name] [hs_ssid] [hs_pass]\n
→ waits for: TOKEN [16-byte-hex]\n
→ stores token to Credential Manager "SeaDrop-Token-Windows"
→ auto-advances on TOKEN received
→ on REJECT: show reason + "Try Again"
```

**Screen 7 — Verification**
```
[YoungSerif 32sp] Checking Everything Works
Three rows, each animates in sequentially:
[✓ green] Hotspot running
[✓ green] SeaDrop connected
[✓ green] Internet confirmed intact
→ Check 1: TetheringOperationalState == On
→ Check 2: TCP socket connected, PONG received within 15s
→ Check 3: HttpClient GET to connectivity-check.ubuntu.com on primary adapter returns 204
→ all must pass before advancing
→ any fail: show which failed, "Retry Checks" button
```

**Screen 8 — Done**
```
[YoungSerif 48sp #E85D00] All set.
[Inter 18sp] SeaDrop lives in your system tray.
             Right-click any file → Send via SeaDrop.
             Or drag files onto the tray icon.
[Space Orange button] Done
→ saves wizard_completed = true to app local settings
→ closes wizard window
→ tray icon visible and green
→ disconnects from SeaDrop SoftAP — ESP32 connects back to hotspot
→ wizard never opens again
```

### Android Setup Wizard — 8 Screens

**Screen 1 — Welcome**
```kotlin
Text("SeaDrop", style = YoungSerif(48.sp), color = SpaceOrange)
Text("Transfer files between your phone and laptop.\nNo internet switching. Ever.",
     style = Inter(18.sp))
Button("Set Up SeaDrop", onClick = { navController.navigate("permissions") })
```

**Screen 2 — Permissions**
Three permission rows. Each: icon + description + "Grant" button.
- Location: `ActivityResultContracts.RequestPermission(ACCESS_FINE_LOCATION)`
- Nearby: `ActivityResultContracts.RequestPermission(NEARBY_WIFI_DEVICES)`
- Battery: `startActivity(Intent(ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS))`

Each row shows green checkmark when granted. "Next" enabled only when all three green. `LaunchedEffect(Unit)` + `onResume` recheck all permissions after returning from system settings.

**Screen 3 — Power On SeaDrop**
Pulsing animation. BLE scanner running. `LaunchedEffect` polls scan results. Auto-navigate to screen 4 when UUID 0xFEAD found with reg_mode = 1.

**Screen 4 — Connecting**
Progress indicator. `WifiNetworkSpecifier` request for SeaDrop SoftAP SSID from BLE payload (one-time setup only). On `NetworkCallback.onAvailable`: TCP connect to 192.168.4.1:4242. Auto-navigate to screen 5.

**Screen 5 — Name This Device**
`OutlinedTextField` pre-filled with `Build.MODEL`. Button "Register" sends `REG ANDROID [name]\n`, waits for `TOKEN [hex]\n`, stores to `EncryptedSharedPreferences`. Auto-navigate to screen 6.

**Screen 6 — WiFi Suggestion Approval**
Text explains auto-connect. App calls `addNetworkSuggestions`. System dialog appears. `BroadcastReceiver` for `ACTION_WIFI_NETWORK_SUGGESTION_POST_CONNECTION` detects approval. Auto-navigate to screen 7.

**Screen 7 — Verification**
Three animated checks:
- Token stored in EncryptedSharedPreferences
- WifiNetworkSuggestion contains SeaDrop SSID
- HttpURLConnection to connectivity-check.ubuntu.com on mobile data network returns 204

**Screen 8 — Done**
`Text("All set.", YoungSerif(48.sp), SpaceOrange)`. Button "Done" calls `finish()`. `SharedPreferences` key `wizard_completed = true`. Wizard never launches again.

---

## Firmware Boot Sequence

```cpp
void setup() {
    Serial.begin(115200);
    Serial.println("[BOOT] SeaDrop v1.5.0 starting");

    // 1. NVS
    nvs_store.begin();
    Serial.println("[NVS] Loaded");

    // 2. SD card — optional, never crash
    sd_buffer.begin(); // sets sd_available internally
    Serial.printf("[SD] Available: %s\n", sd_available ? "yes" : "no");

    // 3. TFT display — optional, never crash
    bool display_available = display.begin();
    Serial.printf("[TFT] Available: %s\n", display_available ? "yes" : "no");
    if (display_available) display.setState(STATE_IDLE);

    // 4. NCSI spoof BEFORE softAP
    ncsi.begin();
    Serial.println("[NCSI] DNS + HTTP spoof running");

    // 5. SoftAP
    wifi_ap.begin(nvs_store.ssid, nvs_store.pass);
    Serial.printf("[AP] SSID: %s\n", nvs_store.ssid);

    // 6. STA to Windows hotspot
    wifi_sta.begin(nvs_store.hs_ssid, nvs_store.hs_pass);
    Serial.printf("[STA] Connecting to: %s\n", nvs_store.hs_ssid);

    // 7. BLE
    ble_adv.begin(!display_available, sd_available);
    Serial.println("[BLE] Advertising UUID 0xFEAD");

    // 8. TCP server
    tcp_server.begin(4242);
    Serial.println("[TCP] Server on port 4242");

    // 9. Tasks — conditional on hardware
    xTaskCreatePinnedToCore(tcp_server_task, "tcp",  8192, NULL, 5, NULL, 0);
    xTaskCreatePinnedToCore(ble_adv_task,    "ble",  4096, NULL, 3, NULL, 0);
    xTaskCreatePinnedToCore(ncsi_task,       "ncsi", 4096, NULL, 3, NULL, 0);
    if (display_available) {
        xTaskCreatePinnedToCore(display_task, "disp", 8192, NULL, 4, NULL, 1);
        xTaskCreatePinnedToCore(touch_task,   "tch",  4096, NULL, 3, NULL, 1);
    }
    if (sd_available) {
        xTaskCreatePinnedToCore(sd_buffer_task, "sd", 8192, NULL, 3, NULL, 1);
    }
    Serial.println("[BOOT] Complete");
}

void loop() { vTaskDelay(portMAX_DELAY); }
```

---

## TFT Display UI

240x320 pixels. All rendering skipped when `display_available = false`.

Font sizes: title 24pt YoungSerif equivalent, body 16pt Inter equivalent, status 12pt.
Colors: background #1A1A2E, text #FEFEFE, accent #E85D00, signal bars #22C55E.

```
STATE_IDLE:
┌────────────────────────┐
│                        │
│       SeaDrop          │  ← 24pt centered
│                        │
│   No devices nearby    │  ← 16pt centered #888888
│                        │
│  SeaDrop-A3F9C2  [SD]  │  ← 12pt bottom #888888, SD dims if unavailable
└────────────────────────┘

STATE_CONNECTED:
┌────────────────────────┐
│  MyPhone        ███░   │  ← 16pt + signal bars
│                        │
│  MyLaptop       ████   │
└────────────────────────┘

STATE_CONFIRM (Close tier — green border):
┌╔══════════════════════╗┐
│║ photo.jpg    4.2MB   ║│  ← 16pt
│║                      ║│
│║ MyPhone ───► MyLaptop║│  ← direction arrow
│║                      ║│
│║  [ Confirm  3s ]     ║│  ← countdown, tap anywhere outside = cancel
└╚══════════════════════╝┘

STATE_TRANSFER:
┌────────────────────────┐
│  photo.jpg             │  ← 16pt
│                        │
│  ████████████░░░░  67% │  ← full width progress bar #E85D00
│                        │
│       2.1 MB/s         │  ← 16pt centered
└────────────────────────┘

STATE_DONE:
┌────────────────────────┐
│                        │
│       ✓ Done           │  ← 24pt #22C55E
│                        │
│     0.8 seconds        │  ← 16pt #888888
│                        │
└────────────────────────┘
Returns to STATE_CONNECTED after 3 seconds.

STATE_ERROR:
┌────────────────────────┐
│                        │
│   ✗ Transfer failed    │  ← 24pt #EF4444
│                        │
│    SD_UNAVAILABLE      │  ← 16pt #888888 error code
│                        │
└────────────────────────┘
Returns to STATE_CONNECTED after 5 seconds.
```

---

## Build Order — Follow This Exactly

Do not skip steps. Do not start a step before the previous one is verified working.

```
Step 1: Firmware — seadrop.ino boot sequence
        Verify: Serial output shows all [BOOT] lines with no crash
        Verify: headless mode (no display, no SD) boots cleanly

Step 2: Firmware — NCSI spoof (ncsi.cpp)
        Verify: connect Android to SeaDrop AP
        Verify: Android internet indicator stays green (not grey/disconnected)
        Verify: connect Windows laptop to SeaDrop hotspot
        Verify: Windows internet indicator stays connected

Step 3: Firmware — TCP server (tcp_server.cpp)
        Verify: two clients connect simultaneously
        Verify: PING/PONG cycle stable for 60 seconds
        Verify: stale session reaped after 30 seconds no PING

Step 4: Firmware — streaming relay
        Verify: file sent from one TCP client received correctly by other
        Verify: CRC32 matches on receiver
        Verify: both directions work

Step 5: Windows — SeaDropService.cs startup gates only
        Verify: location permission gate fires correctly
        Verify: hotspot starts without crash
        Verify: TcpListener binds on hotspot interface
        Verify: app launches, tray icon appears, no crash after 30 seconds

Step 6: Windows — Setup wizard all 8 screens
        Verify: each screen transitions correctly
        Verify: screen 2 location grant works
        Verify: screen 3 hotspot starts automatically
        Verify: screen 7 all three checks pass
        Verify: wizard_completed flag prevents re-launch

Step 7: Windows — Disconnect/reconnect cycle
        Verify: power off SeaDrop → tray goes grey → internet resumes
        Verify: power on SeaDrop → tray goes green → transfers resume

Step 8: Android — SeaDropService permission gates only
        Verify: service starts on boot
        Verify: missing permission shows correct notification with fix link
        Verify: service survives screen off for 5 minutes

Step 9: Android — maintainConnection loop
        Verify: bindProcessToNetwork works
        Verify: socket connects to 192.168.4.1:4242
        Verify: PING/PONG stable
        Verify: disconnect → notification updates → reconnect automatic

Step 10: Android — Setup wizard all 8 screens
         Verify: each screen transitions correctly
         Verify: permission grants work
         Verify: BLE detection auto-advances screen 3
         Verify: wizard_completed prevents re-launch

Step 11: Both — Bilateral transfer
         Verify: Android → Windows file arrives in Downloads\SeaDrop\
         Verify: Windows → Android file arrives in Downloads/SeaDrop/
         Verify: Windows toast with Accept/Decline
         Verify: Android notification with Accept/Decline
         Verify: TFT shows correct direction arrow for each

Step 12: TFT — all 6 states
         Verify: STATE_IDLE shows SSID
         Verify: STATE_CONNECTED shows both device names + RSSI bars
         Verify: STATE_CONFIRM auto-confirms on Close tier
         Verify: STATE_TRANSFER progress bar updates
         Verify: STATE_DONE returns to CONNECTED after 3s
         Verify: STATE_ERROR returns to CONNECTED after 5s
```

---

## What "Apple Level Polished" Means in Practice

This is not a vague instruction. It means:

1. **Nothing crashes.** Ever. Under any condition. Power off SeaDrop mid-transfer: app catches IOException, shows notification, resumes. Kill the app from task manager: service restarts on next launch. Deny a permission mid-session: app shows notification explaining what broke.

2. **Every async operation has a loading state.** No frozen UI. No unresponsive buttons. If something takes more than 200ms, show a progress indicator.

3. **Transitions exist.** Wizard screens fade between each other 200ms EaseInOut. Tray icon color changes animate. Progress bars animate smoothly, not jump in steps.

4. **Typography is consistent.** YoungSerif for every title. Inter for every piece of body text. Space Orange for every primary action. No exceptions. No mixing.

5. **Spacing is consistent.** 24dp horizontal padding everywhere. 32dp vertical padding on screens. 16dp between elements. 8dp between related elements.

6. **States are complete.** Every button has default, hover, pressed, disabled. Every input has empty, focused, filled, error. Nothing has only one visual state.

7. **Errors explain themselves.** "Something went wrong" is not an error message. "Hotspot failed: TetheringOperationStatus.WiFiDeviceOff — turn on WiFi and try again" is an error message.

8. **The app does what it says.** If the spec says auto-connect, it auto-connects. If the spec says internet stays intact, internet stays intact. No manual steps after setup wizard completes.

---

## File Structure

```
seadrop/
├── firmware/
│   └── seadrop/
│       ├── seadrop.ino
│       ├── config.h
│       ├── wifi_ap.h / .cpp
│       ├── wifi_sta.h / .cpp
│       ├── ncsi.h / .cpp
│       ├── ble_adv.h / .cpp
│       ├── tcp_server.h / .cpp
│       ├── stp.h / .cpp
│       ├── relay.h / .cpp
│       ├── sd_buffer.h / .cpp
│       ├── rssi.h / .cpp
│       ├── display.h / .cpp
│       ├── touch.h / .cpp
│       ├── nvs_store.h / .cpp
│       └── registration.h / .cpp
├── android/
│   └── app/src/main/
│       ├── AndroidManifest.xml
│       └── java/com/seadrop/app/
│           ├── MainActivity.kt
│           ├── SeaDropService.kt
│           ├── BootReceiver.kt
│           ├── ShareActivity.kt
│           ├── network/
│           │   ├── WifiManager.kt
│           │   ├── NetworkBinder.kt
│           │   └── TcpClient.kt
│           ├── ble/BleScanner.kt
│           ├── transfer/
│           │   ├── TransferManager.kt
│           │   ├── OutboundQueue.kt
│           │   └── FileHelper.kt
│           ├── stp/
│           │   ├── StpCommand.kt
│           │   └── StpParser.kt
│           ├── notification/NotificationHelper.kt
│           ├── registration/RegistrationManager.kt
│           ├── storage/SecurePrefs.kt
│           └── ui/
│               ├── theme/Theme.kt
│               ├── theme/Typography.kt
│               └── wizard/
│                   ├── WizardScreen1.kt through WizardScreen8.kt
├── windows/
│   ├── SeaDrop.sln
│   ├── SeaDrop/
│   │   ├── Package.appxmanifest
│   │   ├── App.xaml / App.xaml.cs
│   │   ├── MainWindow.xaml / .cs
│   │   ├── core/SeaDropService.cs
│   │   ├── network/
│   │   │   ├── HotspotManager.cs
│   │   │   ├── BleWatcher.cs
│   │   │   └── ChannelDetector.cs
│   │   ├── transfer/
│   │   │   ├── TransferManager.cs
│   │   │   ├── OutboundQueue.cs
│   │   │   └── FileHelper.cs
│   │   ├── stp/
│   │   │   ├── StpCommand.cs
│   │   │   └── StpParser.cs
│   │   ├── shell/SeaDropContextMenu.cs
│   │   ├── notification/ToastHelper.cs
│   │   ├── registration/RegistrationManager.cs
│   │   ├── storage/CredentialStore.cs
│   │   ├── tray/TrayManager.cs
│   │   └── wizard/
│   │       ├── WizardWindow.xaml / .cs
│   │       ├── Screen1Welcome.xaml / .cs
│   │       ├── Screen2Location.xaml / .cs
│   │       ├── Screen3Hotspot.xaml / .cs
│   │       ├── Screen4PowerOn.xaml / .cs
│   │       ├── Screen5Connecting.xaml / .cs
│   │       ├── Screen6Register.xaml / .cs
│   │       ├── Screen7Verify.xaml / .cs
│   │       └── Screen8Done.xaml / .cs
│   ├── SeaDrop.ShellExtension/
│   │   └── ExplorerCommandHandler.cs
│   └── SeaDrop.Installer/
│       └── Package.wapproj
└── docs/
    ├── SeaDrop_Specification_v1.5.docx
    └── AGENTS.md
```

---

*SeaDrop AGENTS.md — v1.5.0 — June 2026*
*This file is read before any code is written. It is updated when the spec changes.*
