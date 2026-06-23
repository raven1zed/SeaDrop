# SeaDrop v1.5

File transfer between Android and Windows over a local Wi-Fi relay — no internet required, no cloud involved, no pairing step after first setup.

SeaDrop is a dedicated hardware device built on an ESP32-WROOM-32. It creates a Wi-Fi access point that both an Android phone and a Windows laptop connect to simultaneously. Files are relayed through the device over TCP at wire speed. The device has a 240x320 ILI9341 TFT display showing live transfer state.

---

## What it is

- The Android app sends a file via the system share sheet.
- The Windows app receives it in `Downloads\SeaDrop\` and shows a toast notification.
- The ESP32 relays the binary stream and displays transfer progress on screen.
- No internet connection is interrupted on either device.
- No Bluetooth pairing after setup. No QR codes. No accounts.

---

## Hardware

| Component | Part |
|---|---|
| Microcontroller | ESP32-WROOM-32 |
| Display | 3.2" ILI9341 TFT, 240x320, 8-bit parallel |
| Storage | microSD via SPI (optional — streaming works without it) |
| Power | USB-C 5V |

Display GPIO mapping (8-bit parallel):

| Signal | GPIO |
|---|---|
| RST | 32 |
| CS | 33 |
| RS (D/C) | 2 |
| WR | 4 |
| RD | 15 |
| D0–D7 | 12, 13, 14, 26, 25, 21, 22, 27 |

---

## Repository Structure

```
seadrop/
├── firmware/
│   └── main/
│       ├── main.cpp             Boot sequence, task spawning
│       ├── protocol.hpp         Shared constants, color palette, STP enums
│       ├── display.cpp/.hpp     ILI9341 8-bit parallel driver + all screen states
│       ├── stp_server.cpp/.hpp  TCP server, STP command parser, session manager
│       ├── ble_server.cpp/.hpp  BLE advertisement (UUID 0xFEAD, registration beacon)
│       ├── ncsi.cpp/.hpp        DNS + HTTP spoof — keeps Windows/Android internet indicator green
│       ├── transfer.cpp/.hpp    Binary stream relay, CRC32 verification
│       └── globals.hpp          Global state shared across tasks
├── android/
│   └── app/src/main/
│       ├── AndroidManifest.xml
│       └── kotlin/com/seadrop/app/
│           ├── SeaDropService.kt        Foreground service, Wi-Fi lock, reconnect loop
│           ├── MainActivity.kt          Status screen
│           ├── ShareActivity.kt         Receives files from system share sheet
│           ├── BootReceiver.kt          Auto-start on device boot
│           ├── ble/BleScanner.kt        Scans for UUID 0xFEAD
│           ├── network/TcpClient.kt     TCP connection to 192.168.4.1:4242
│           ├── network/NetworkBinder.kt Binds process to SeaDrop Wi-Fi network
│           ├── stp/StpCommand.kt        STP command definitions
│           ├── stp/StpParser.kt         Line-based ASCII parser
│           ├── transfer/TransferManager.kt Outbound file stream
│           ├── registration/RegistrationManager.kt First-time setup
│           └── storage/SecurePrefs.kt   EncryptedSharedPreferences wrapper
├── windows/
│   ├── SeaDrop.sln
│   ├── core/SeaDropService.cs           Background service, startup gate sequence
│   ├── network/HotspotManager.cs        Mobile hotspot control via Windows.Networking.NetworkOperators
│   ├── network/BleWatcher.cs            BLE advertisement watcher, UUID 0xFEAD filter
│   ├── stp/StpCommand.cs                STP command definitions
│   ├── stp/StpParser.cs                 Line-based ASCII parser
│   ├── transfer/TransferManager.cs      Inbound file stream, CRC32 check
│   ├── transfer/OutboundQueue.cs        SQLite-backed send queue
│   ├── shell/SeaDropContextMenu.cs      Right-click context menu shell extension
│   ├── notification/ToastHelper.cs      Windows toast notifications
│   ├── tray/TrayManager.cs              System tray icon (grey/green state)
│   ├── registration/RegistrationManager.cs First-time setup wizard
│   └── storage/CredentialStore.cs       Windows Credential Manager wrapper
└── docs/
    ├── AGENTS.md                Development contract — read before touching code
    └── SeaDrop_Specification_v1.5.docx  Full specification
```

---

## Protocol — STP v1.5

All commands are newline-terminated ASCII over TCP port 4242. Binary payloads follow immediately after the command line. The connection stays open permanently.

| Command | Direction | Description |
|---|---|---|
| `HELLO <type> <name> <token> <version>` | Client to ESP32 | type = ANDROID or WINDOWS |
| `HELLO_ACK <session_id> <version>` | ESP32 to Client | Confirms session |
| `PING` | Client to ESP32 | Keepalive every 10 seconds |
| `PONG` | ESP32 to Client | Response to PING |
| `SEND <file> <bytes> <crc32> <mode>` | Either to ESP32 | mode = STREAM or BUFFER |
| `SEND_ACK <mode>` | ESP32 to Sender | Confirms mode |
| `SEND_DONE` | Sender to ESP32 | End of binary stream |
| `NOTIFY <file> <bytes>` | ESP32 to Receiver | File ready (BUFFER mode) |
| `PULL <file>` | Receiver to ESP32 | Request buffered file |
| `PULL_DATA <bytes>` | ESP32 to Receiver | Sends buffered file |
| `PULL_DONE` | ESP32 to Receiver | End of buffered stream |
| `ACK` | Any | Generic acknowledgement |
| `REJECT <reason>` | ESP32 to Client | See reason codes below |
| `CANCEL` | Any | Cancel active transfer |
| `STATUS` | Client to ESP32 | Request session state |
| `STATUS_RESP <state> <peer> <mode> <sd>` | ESP32 to Client | Includes sd_available flag |
| `REG <type> <name> <hs_ssid> <hs_pass>` | Client to ESP32 | Registration mode only |
| `TOKEN <hex>` | ESP32 to Client | 16-byte random auth token |

REJECT reason codes: `AUTH_FAIL`, `SD_FULL`, `SD_UNAVAILABLE`, `NO_PEER`, `BUSY`, `CHECKSUM_FAIL`, `REG_CLOSED`, `VERSION_MISMATCH`

---

## Building

### Firmware

Requirements: ESP-IDF v5.x, CMake 3.24+.

```powershell
cd firmware
.\build_flash.ps1
```

The script builds and flashes to the port defined in `build_flash.ps1` (default COM6). Monitor output:

```powershell
.\monitor.ps1
```

Expected boot output:
```
[BOOT] SeaDrop v1.5.0 starting
[NVS] Loaded
[SD] Available: yes|no
[TFT] Available: yes|no
[NCSI] DNS + HTTP spoof running
[AP] SSID: SeaDrop-XXXXXX
[STA] Connecting to: <hotspot ssid>
[BLE] Advertising UUID 0xFEAD
[TCP] Server on port 4242
[BOOT] Complete
```

### Android

Requirements: Android Studio Meerkat, JDK 21, Android SDK API 36.

```bash
cd android
./gradlew assembleDebug
```

APK is at `android/app/build/outputs/apk/debug/app-debug.apk`. Sideload with `adb install`.

### Windows

Requirements: Visual Studio 2022 17.9+, .NET 8 SDK, Windows App SDK 1.5.

```powershell
cd windows
dotnet build SeaDrop.sln -c Release
```

To package as MSIX and install:

```powershell
.\install.ps1
```

---

## First Run

When the device has no stored credentials it enters **Registration Mode**. The TFT displays `REGISTRATION MODE` with two pending status tiles — one for Android, one for Windows.

**Android**: Open the app. It detects the BLE advertisement (UUID `0xFEAD`, `reg_mode=1`), connects to the SoftAP, sends `REG ANDROID <name>`, and receives a `TOKEN` response stored in `EncryptedSharedPreferences`.

**Windows**: The setup wizard detects the same BLE beacon, starts the mobile hotspot, connects to the SoftAP, sends `REG WINDOWS <name> <hs_ssid> <hs_pass>`, and stores the token in Windows Credential Manager.

After both register, the device reboots into normal operation. It never enters Registration Mode again unless NVS is erased.

---

## Transfer Flow

1. User shares a file from Android or right-clicks in Explorer and selects **Send via SeaDrop**.
2. The sender app connects to 192.168.4.1:4242, sends `SEND <filename> <bytes> <crc32> STREAM`.
3. The ESP32 relays the binary stream directly to the other connected client.
4. The receiver writes to `Downloads/SeaDrop/` (Android) or `Downloads\SeaDrop\` (Windows).
5. CRC32 is verified post-receive. On mismatch: `REJECT CHECKSUM_FAIL`, file deleted.
6. TFT shows `STATE_TRANSFER` with live progress bar, returns to `STATE_CONNECTED` when done.

---

## System Requirements

| Platform | Requirement |
|---|---|
| Windows | Windows 11 24H2 (build 26100+), Wi-Fi adapter with hotspot support |
| Android | Android 16 (API 36+), Wi-Fi, Bluetooth LE |
| ESP32 | ESP32-WROOM-32, 4MB flash |

---

## Version

Current: `1.5.0`

Version format: `MAJOR.MINOR.PATCH`
- MAJOR: Breaking STP protocol change. All three codebases update simultaneously.
- MINOR: New feature. Apps remain backward-compatible with older firmware.
- PATCH: Bug fix. No protocol changes.

---

## License

Proprietary. All rights reserved.