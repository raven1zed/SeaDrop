Version:1.0StartHTML:0000000179EndHTML:0000323634StartFragment:0000049130EndFragment:0000323594SourceURL:file:///C:/laragon/www/SeaDrop/SeaDrop_Specification_v1.4.docx<style></style>

**SeaDrop**

Specification & Implementation Document

Version1.4  —  June 2026

Changes from v1.3: Full project file and folder structure forall three codebases (firmware, Android, Windows). Bilateral transfer flowdocumented. TFT UI/UX defined as minimalist 240x320 layout. System requirementslocked: Windows 11 24H2, Android 16+. NetworkOperatorTetheringManagerwiFiControl capability confirmed for WiFi-only laptops. Codebase boundariesenforced per component.

SeaDrop is a portable, pocket-sized file transfer devicepowered by an ESP32-WROOM-32 microcontroller with an SPI TFT touchscreen andmicroSD slot. It creates its own WiFi access point, enabling an Android phoneand a Windows laptop on completely separate networks to transfer files betweeneach other — in both directions — without either device losing internet at anypoint.

System requirements: Windows 11 24H2 or later. Android 16 (API36) or later.



1. System Overview
   ==================

1.1 Core Behavioral Requirements
--------------------------------

•        Neither device loses internet at any point — not duringsetup, not during transfer, not ever

•        Bilateral file transfer: Android to Windows and Windowsto Android, identical UX in both directions

•        Share from native context on both platforms — sharesheet on Android, right-click context menu on Windows

•        SeaDrop is detected and connected automatically whenpowered on — no manual action after first setup

•        Persistent TCP connection — the line is always open,transfers start instantly

•        One-time setup only via first-launch wizard in bothapps — no USB cable for users

•        After SeaDrop power cycle or device reboot — reconnectsautomatically, no user action
1.2 System Requirements
-----------------------

| **Windows** | Windows 11 24H2 (build 26100) or later                                         |
| ----------- | ------------------------------------------------------------------------------ |
| **Android** | Android 16 (API 36) or later — Samsung Galaxy A15 OneUI 8 confirmed compatible |
| **ESP32**   | ESP32-WROOM-32, Arduino core for ESP32 v3.x or later                           |
| **microSD** | Class 10 / UHS-I rated — required for SDMMC 4-bit throughput targets           |

1.3 Architecture
----------------

┌──────────────────────────────────────────────────────────────┐

│                          SeaDrop                             │

│  ESP32-WROOM-32  APSTA Mode                                  │

│                                                              │

│  SoftAP 192.168.4.1 ──► TCP :4242 ──►Streaming Relay       │

│  STA ──────────────────────────────► SDMMC4-bit (offline)  │

│  DNSServer + ESPAsyncWebServer ──► NCSISpoof               │

│  BLE UUID 0xFEAD ──► Discovery                              │

│  SPI ILI9341 240x320 ──► Minimalist TFTUI                 │

│  WiFi RSSI ──► Proximity Trust Zones                        │

└──────────┬───────────────────────────┬──────────────────────┘

           │ SoftAP                     │ STA → Windows Hotspot

  ┌────────┴──────────┐       ┌─────────┴────────────┐

  │   Android App     │       │    Windows App        │

  │ WifiNetworkSugg.  │       │ NetworkOperatorTether │

  │ bindProcessToNet  │       │ wiFiControl manifest  │

  │ Share Sheet entry │       │ IExplorerCommand      │

  │ Incoming notify   │       │ Incoming toast        │

  │ Outbound queue    │       │ Outbound queue        │

  │ Internet intact   │       │ Internet intact       │

└───────────────────┘       └──────────────────────┘
1.4 Windows Single-Radio Solution
---------------------------------

The HP Victus Realtek 8852BE-VT reports zero concurrentchannels but confirms Co-existence Support: Wi-Fi performance is maintained,P2P GO ports count: 1, P2P Max Mobile AP Clients: 8. This means the chipsupports running Mobile Hotspot concurrently with infrastructure WiFi via NDISInfrastructure Station + Software AP mode.

The Windows app usesNetworkOperatorTetheringManager.CreateFromConnectionProfile() with thewiFiControl DeviceCapability declared in the MSIX manifest. This API works onWiFi-only laptops — no SIM card or mobile broadband account required. Thehotspot SSID and password are fixed values set during registration. ESP32connects to the laptop hotspot in STA mode. Laptop internet stays on home WiFi.



2. Project File and Folder Structure
   ====================================

The project is split into three completely separate codebasesin one repository. Each codebase has a hard boundary — no shared code betweenthem. AI agents and contributors must not move files across these boundaries orintroduce cross-dependencies.
2.1 Repository Root
-------------------

seadrop/

├── firmware/          ← ESP32 Arduino firmware only

├── android/           ← Android Kotlin app only

├── windows/           ← Windows C# .NET 8 app only

├── docs/              ← Specification documents

│   ├── SeaDrop_Specification_v1.4.docx

│   └── assets/

├── .gitignore

└── README.md
2.2 Firmware Structure — /firmware/
-----------------------------------

firmware/

├── seadrop/                    ← Arduino sketch folder(must match folder name)

│   ├── seadrop.ino             ← Main entry point: setup() andloop()

│   ├── config.h                ← Pin definitions, constants,version string

│   ├── wifi_ap.h / .cpp        ← SoftAP init, APSTA mode, channelmanagement

│   ├── wifi_sta.h / .cpp       ← STA connection to Windows hotspot,reconnect loop

│   ├── ncsi.h / .cpp           ← DNSServer + ESPAsyncWebServer NCSIspoof

│   ├── ble_adv.h / .cpp        ← NimBLE advertisement UUID 0xFEAD,reg_mode flag

│   ├── tcp_server.h / .cpp     ← TCP server :4242, session table, relaylogic

│   ├── stp.h / .cpp            ← STP command parser and builder

│   ├── relay.h / .cpp          ← Streaming relay ring buffer, flowcontrol

│   ├── sd_buffer.h / .cpp      ← SDMMC 4-bit init, file write/read,queue handler

│   ├── rssi.h / .cpp           ← RSSI rolling average, tierclassification

│   ├── display.h / .cpp        ← TFT_eSPI init, state machine renderfunctions

│   ├── touch.h / .cpp          ← XPT2046 touch polling via T_IRQinterrupt

│   ├── nvs_store.h / .cpp      ← Preferences.h wrappers for all NVS keys

│   └── registration.h / .cpp   ← Registration mode logic, REG/TOKENcommands

├── platformio.ini              ← PlatformIO config (preferredover Arduino IDE)

└── libraries/                  ← Local library overrides ifneeded
2.3 Android Structure — /android/
---------------------------------

android/

├── app/

│   ├── src/main/

│   │   ├── AndroidManifest.xml

│   │   ├── java/com/seadrop/app/

│   │   │   ├── MainActivity.kt          ← Setup wizard entry, settings only

│   │   │   ├── SeaDropService.kt        ← Foreground service: TCP loop, WiFi,BLE

│   │   │   ├── BootReceiver.kt          ← BOOT_COMPLETED → startsSeaDropService

│   │   │   ├── ShareActivity.kt         ← Handles ACTION_SEND from share sheet

│   │   │   ├── network/

│   │   │   │   ├── WifiManager.kt       ← WifiNetworkSuggestion logic

│   │   │   │   ├── NetworkBinder.kt     ← bindProcessToNetwork wrapper

│   │   │   │   └── TcpClient.kt         ← Persistent socket, readLoop, PING

│   │   │   ├── ble/

│   │   │   │   └── BleScanner.kt        ← BluetoothLeScanner, UUID 0xFEAD

│   │   │   ├── transfer/

│   │   │   │   ├── TransferManager.kt   ← Orchestrates SEND/PULL sequences

│   │   │   │   ├── OutboundQueue.kt     ← Room database queue operations

│   │   │   │   └── FileHelper.kt        ← SAF URI resolution, file read/write

│   │   │   ├── stp/

│   │   │   │   ├── StpCommand.kt        ← STP command data classes

│   │   │   │   └── StpParser.kt         ← Command parser and builder

│   │   │   ├── notification/

│   │   │   │   └── NotificationHelper.kt ← Incoming file,progress, queue

│   │   │   ├── registration/

│   │   │   │   └── RegistrationManager.kt ← REG/TOKEN overTCP, wizard flow

│   │   │   └── storage/

│   │   │       └── SecurePrefs.kt       ← EncryptedSharedPreferences wrapper

│   │   └── res/

│   │       ├── layout/                  ← XMLlayouts for setup wizard only

│   │       └── values/                  ←strings, colors, styles

│   └── build.gradle.kts

├── build.gradle.kts

└── settings.gradle.kts
2.4 Windows Structure — /windows/
---------------------------------

windows/

├── SeaDrop.sln

├── SeaDrop/                             ← Main WinUI 3 appproject

│   ├── Package.appxmanifest             ← MSIX manifest: wiFiControlcapability

│   ├── App.xaml / App.xaml.cs           ← App entry point, tray icon init

│   ├── MainWindow.xaml / .cs            ← Settings window — opens on demandonly

│   ├── core/

│   │   ├── SeaDropService.cs            ←Background service orchestrator

│   │   ├── TcpListener.cs               ←Persistent TCP listener on hotspot IF

│   │   └── TcpClient.cs                 ←Outbound TCP to ESP32 (same socket)

│   ├── network/

│   │   ├── HotspotManager.cs            ←NetworkOperatorTetheringManager wrapper

│   │   ├── BleWatcher.cs                ←BluetoothLEAdvertisementWatcher

│   │   └── ChannelDetector.cs           ←wlanapi.dll: reads home WiFi channel

│   ├── transfer/

│   │   ├── TransferManager.cs           ←Orchestrates SEND/PULL sequences

│   │   ├── OutboundQueue.cs             ←SQLite queue operations

│   │   └── FileHelper.cs                ←File read/write, path validation

│   ├── stp/

│   │   ├── StpCommand.cs                ←STP command record types

│   │   └── StpParser.cs                 ←Command parser and builder

│   ├── shell/

│   │   └── SeaDropContextMenu.cs        ←IExplorerCommand implementation

│   ├── notification/

│   │   └── ToastHelper.cs               ←Incoming file, progress, queue toasts

│   ├── registration/

│   │   └── RegistrationManager.cs       ←REG/TOKEN, wizard flow, hotspot creds

│   └── storage/

│       └── CredentialStore.cs           ← Windows Credential Manager wrapper

├──SeaDrop.ShellExtension/              ←Separate COM project for IExplorerCommand

│   ├── ExplorerCommandHandler.cs

│   └── Package.appxmanifest

└──SeaDrop.Installer/                   ←MSIX packaging project

    ├── Package.wapproj

└── PackageDependencies/             ← Bundled .NET 8 + WinAppSDKruntimes
2.5 Codebase Boundary Rules
---------------------------

•        firmware/ has zero knowledge of Android or Windowsinternals. It only speaks STP over TCP.

•        android/ has zero knowledge of Windows internals. Itonly speaks STP to the ESP32 IP.

•        windows/ has zero knowledge of Android internals. Itonly speaks STP to the ESP32 hotspot IP.

•        STP is the only shared interface. Any change to STPcommands must be reflected in all three codebases simultaneously and documentedin Section 6.

•        AI agents working on this codebase must not move filesbetween firmware/, android/, and windows/. Each directory is a self-containedproject with its own build system.

•        No shared utility libraries between Android andWindows. Duplicating a CRC32 function in both is correct. Importing across-platform library is not.



3. Hardware
   ===========

3.1 Components
--------------

| **Microcontroller** | ESP32-WROOM-32 (XX5R69, 38-pin DevKit)                 |
| ------------------- | ------------------------------------------------------ |
| **USB-UART**        | CP2102 — developer flashing only, not user-facing      |
| **Display**         | ILI9341 2.4" TFT 240x320 — 4-wire SPI only             |
| **Touch**           | XPT2046 resistive — SPI, shares VSPI bus with display  |
| **Storage**         | microSD — SDMMC 4-bit (Class 10 / UHS-I required)      |
| **Power**           | USB Micro-B, 5V from any USB power source or powerbank |

3.2 Mandatory eFuse Burn — Before First Flash
---------------------------------------------

GPIO 12 is SDMMC D2. SD specification requires 10k pull-up onall data lines. GPIO 12 HIGH at boot = 1.8V flash voltage on WROOM-32 = bootloop. A pull-down fighting the SD pull-up causes CRC errors. Permanent fix:burn eFuses to lock VDD_SDIO to 3.3V. Irreversible. Must be done before anyfirmware is flashed.

pip install esptool

espefuse.py --portCOM_PORT set_flash_voltage 3.3V

# Type BURN when prompted.Cannot be undone.

3.3 Strapping Pin Summary
-------------------------

| **GPIO** | **Boot Function**       | **v1.4 Assignment and Status**                                                             |
| -------- | ----------------------- | ------------------------------------------------------------------------------------------ |
| GPIO 0   | Download mode           | Unassigned. Float or 10k pull-up. Safe.                                                    |
| GPIO 2   | Download mode secondary | SDMMC D0. No external pull-up. gpio_pullup_en() in firmware after boot before SDMMC mount. |
| GPIO 5   | SDIO timing             | SDMMC D3. 10k pull-up. Safe.                                                               |
| GPIO 12  | Flash LDO voltage       | SDMMC D2. 10k pull-up ONLY after eFuse burn. See §3.2.                                     |
| GPIO 15  | SDIO timing + boot log  | SDMMC CMD only. NOT shared with TFT_CS. v1.2 fatal conflict resolved in v1.3.              |

3.4 Pin Map
-----------

| **Pin/Signal** | **ESP32 GPIO** | **Notes**                                         |
| -------------- | -------------- | ------------------------------------------------- |
| TFT_CS         | GPIO 22        | Display chip select — not a strapping pin         |
| TFT_DC         | GPIO 27        | Display data/command                              |
| TFT_RST        | GPIO 26        | Display reset                                     |
| TFT_MOSI       | GPIO 23        | VSPI MOSI — hardware SPI                          |
| TFT_CLK        | GPIO 18        | VSPI clock — hardware SPI                         |
| TFT_MISO       | GPIO 19        | VSPI MISO — hardware SPI                          |
| T_CS           | GPIO 33        | XPT2046 touch chip select                         |
| T_IRQ          | GPIO 32        | Touch interrupt — reduces polling load            |
| SDMMC_CLK      | GPIO 14        | SDMMC clock                                       |
| SDMMC_CMD      | GPIO 15        | SDMMC command — 10k pull-up                       |
| SDMMC_D0       | GPIO 2         | SDMMC data 0 — no external pull-up, firmware only |
| SDMMC_D1       | GPIO 4         | SDMMC data 1 — 10k pull-up                        |
| SDMMC_D2       | GPIO 12        | SDMMC data 2 — 10k pull-up after eFuse burn only  |
| SDMMC_D3       | GPIO 13        | SDMMC data 3 — 10k pull-up                        |
| 5V             | VIN            | USB 5V — NOT 3V3                                  |
| GND            | GND            | Common ground                                     |

3.5 TFT_eSPI config.h
---------------------

#define ILI9341_DRIVER

#define TFT_CS   22

#define TFT_DC   27

#define TFT_RST  26

#define TFT_MOSI 23

#define TFT_CLK  18

#define TFT_MISO 19

#define TOUCH_CS 33

#define SPI_FREQUENCY       40000000

#defineSPI_TOUCH_FREQUENCY  2500000



4. TFT Display UI — Minimalist 240x320
   ======================================

The TFT is 240x320 pixels. The UI is read at arm's length.Every element must be immediately readable. No decorative elements, no statusbars, no noise. The screen communicates exactly one thing per state.
4.1 Design Rules
----------------

•        Maximum three elements visible at any time

•        Progress bar always full width (240px) when visible

•        Font sizes: title 24pt, body 16pt, status 12pt

•        RSSI shown as signal bars (1-3 filled bars) not dBmnumbers

•        Direction arrow ( ──► ) is the only indicator oftransfer direction — no labels like 'sender' or 'receiver'

•        Color used only for state: white = idle, green border =close tier, yellow border = medium tier, no border = far tier
4.2 STATE_IDLE
--------------

┌────────────────────────┐

│                        │

│       SeaDrop          │

│                        │

│   No devices nearby    │

│                        │

│  SeaDrop-A3F9C2        │

└────────────────────────┘

SSID shown small at bottom. Nothing else. No uptime, no stats.
4.3 STATE_CONNECTED
-------------------

┌────────────────────────┐

│  📱 MyPhone     ███░   │

│                        │

│  💻 MyLaptop    ████   │

└────────────────────────┘

Device name + signal bars per device. Signal bars update every5 seconds. No other information.
4.4 STATE_CONFIRM — Android to Windows
--------------------------------------

┌────────────────────────┐

│  photo_2026.jpg  4.2MB │

│                        │

│  MyPhone ──────► MyLaptop

│                        │

│     [ Confirm  3s ]    │

└────────────────────────┘

Close tier: green border, countdown replaces static label.Medium/Far tier: yellow or no border, countdown hidden, button reads 'Confirm'.Tap anywhere outside button = Cancel.
4.5 STATE_CONFIRM — Windows to Android
--------------------------------------

┌────────────────────────┐

│  document.pdf   2.3MB  │

│                        │

│  MyLaptop ────► MyPhone│

│                        │

│     [ Confirm  3s ]    │

└────────────────────────┘

Direction arrow is the only difference. Everything elseidentical. Bilateral transfer is symmetric at the UI level.
4.6 STATE_TRANSFER
------------------

┌────────────────────────┐

│  photo_2026.jpg        │

│                        │

│  ████████████░░░░  67% │

│                        │

│       2.1 MB/s         │

└────────────────────────┘

No sender/receiver labels. No mode indicator. Progress bar andspeed only. User already confirmed it — they just want to know when it's done.
4.7 STATE_DONE
--------------

┌────────────────────────┐

│                        │

│         ✓ Done         │

│                        │

│      0.8 seconds       │

│                        │

└────────────────────────┘

Returns to STATE_CONNECTED after 3 seconds.
4.8 STATE_ERROR
---------------

┌────────────────────────┐

│                        │

│    ✗ Transfer failed   │

│                        │

│      SD_FULL           │

│                        │

└────────────────────────┘

Error code shown plainly. Returns to STATE_CONNECTED after 5seconds. Error codes: SD_FULL, TIMEOUT, CHECKSUM_FAIL, REJECTED, PEER_OFFLINE.



5. Bilateral Transfer Flow
   ==========================

5.1 Android to Windows
----------------------

User shares any file from any Android app via share sheet.SeaDrop appears as a share target. Tapping it triggers SeaDropService to sendthe file. The ESP32 confirms on TFT. Windows receives a toast notification withAccept/Decline. File lands in C:\Users\[user]\Downloads\SeaDrop\.

Android share sheet

  → SeaDropService.sendFile(uri)

  → TcpClient.send(SEND file bytes crc32STREAM)

  → ESP32 tcp_server_task: relay to Windows

  → Windows TcpListener receives stream

  → ToastHelper: 'photo_2026.jpg from MyPhone —Accept / Decline'

→ FileHelper.save() to Downloads\SeaDrop\
5.2 Windows to Android
----------------------

User right-clicks any file in Explorer. 'Send via SeaDrop'appears at the top level. Clicking it triggers SeaDropService to send the file.The ESP32 confirms on TFT. Android receives a system notification withAccept/Decline. File lands in Downloads/SeaDrop/.

Explorer right-click →'Send via SeaDrop'

  → SeaDropContextMenu.InvokeAsync()

  → TransferManager.SendFile(path)

  → TcpClient.send(SEND file bytes crc32STREAM)

  → ESP32 tcp_server_task: relay to Android

  → Android TcpClient readLoop receives NOTIFY

  → NotificationHelper: 'document.pdf fromMyLaptop — Accept / Decline'

→ FileHelper.save() to Downloads/SeaDrop/
5.3 Transfer Symmetry
---------------------

The STP protocol is symmetric. SEND can come from eitherclient. The ESP32 tcp_server_task checks which session sent the command andrelays to the other. No special handling for direction. The TFT direction arrowis the only asymmetric element — it reads the sender session MAC and updatesaccordingly.
5.4 Incoming File — Android Notification
----------------------------------------

SeaDrop

document.pdf  |  2.3MB  from MyLaptop

[ Accept ]  [ Decline ]

Inline actions. No app opens on tap — FileHelper.save() runsdirectly from the notification action receiver. App only opens if the userexplicitly taps the notification body.
5.5 Incoming File — Windows Toast
---------------------------------

SeaDrop

photo_2026.jpg  |  4.2MB  from MyPhone

[ Accept ]  [ Decline ]

Windows toast with inline buttons. Accept triggersFileHelper.save() directly. Decline sends CANCEL to ESP32. No app window opens.



6. SeaDrop Transfer Protocol (STP) v1.4
   =======================================

Minimal persistent TCP protocol over port 4242.Newline-terminated ASCII commands. Binary payloads follow immediately with bytelength in the command header. Connection is permanent — never closes unlessSeaDrop loses power.
6.1 Commands
------------

| **HELLO <type> <name> <token>**           | Client greeting. type = ANDROID or WINDOWS                                  |
| ----------------------------------------- | --------------------------------------------------------------------------- |
| **HELLO_ACK <session_id>**                | ESP32 assigns session ID                                                    |
| **PING**                                  | Keepalive every 10s on idle                                                 |
| **PONG**                                  | ESP32 response to PING                                                      |
| **CHANNEL <n>**                           | Windows reports home WiFi channel. ESP32 matches SoftAP.                    |
| **SEND <file> <bytes> <crc32> <mode>**    | Either client declares send intent. mode = STREAM or BUFFER                 |
| **SEND_ACK <mode>**                       | ESP32 confirms mode                                                         |
| **SEND_DONE**                             | Sender signals end of binary stream                                         |
| **NOTIFY <file> <bytes>**                 | ESP32 to receiver: file ready (BUFFER mode only)                            |
| **PULL <file>**                           | Receiver requests buffered file from SD                                     |
| **PULL_DATA <bytes>**                     | ESP32 streams file bytes                                                    |
| **PULL_DONE**                             | ESP32 signals end of stream                                                 |
| **ACK**                                   | Generic acknowledgement                                                     |
| **REJECT <reason>**                       | AUTH_FAIL / SD_FULL / NO_PEER / BUSY / CHECKSUM_FAIL / REG_CLOSED           |
| **CANCEL**                                | Either party cancels active transfer                                        |
| **STATUS**                                | Client requests current session state                                       |
| **STATUS_RESP <state> <peer> <mode>**     | ESP32 response to STATUS                                                    |
| **REG <type> <name> <hs_ssid> <hs_pass>** | Registration. Windows includes hotspot credentials. Registration Mode only. |
| **TOKEN <hex>**                           | ESP32 response to REG — 16-byte random token                                |

6.2 Streaming Relay — Both Directions
-------------------------------------

Android                    ESP32                    Windows

   |                         |                         |

   |── HELLO ANDROID ───────►|◄─── HELLOWINDOWS ──────|

   |◄─ HELLO_ACK sid_1 ──────|──── HELLO_ACKsid_2 ───►|

   |◄────────────── CHANNEL 6 from Windows────────────►|

   | PING/PONG every 10s both sides                      |

   |                         |                         |

   | [Android shares file]   |                         |

   |── SEND file STREAM ────►|                         |

   |◄─ SEND_ACK STREAM ──────|                         |

   |── [binary stream] ──────|──── [relay]───────────►|

   |── SEND_DONE ───────────►|──── ACK───────────────►|

   |◄─ ACK ──────────────────|                         |

   |                         |                         |

   | [Windows sends file]    |                         |

   |                         |◄─── SEND file STREAM───|

   |                         |──── SEND_ACK STREAM───►|

   |◄─── [relay] ────────────|◄─── [binarystream] ────|

   | NOTIFY arrives          |◄─── SEND_DONE ──────────|

   |◄─── ACK ────────────────|──── ACK───────────────►|

   | [line stays open]       | [line stays open]       |



7. ESP32 Firmware
   =================

7.1 Stack
---------

| **Framework** | Arduino core for ESP32 v3.x                |
| ------------- | ------------------------------------------ |
| **Display**   | TFT_eSPI — hardware SPI ILI9341 with DMA   |
| **Touch**     | XPT2046_Touchscreen — T_IRQ interrupt mode |
| **SD**        | ESP-IDF SDMMC host driver — 4-bit mode     |
| **BLE**       | NimBLE-Arduino                             |
| **WiFi**      | WiFi.h — WIFI_AP_STA                       |
| **DNS**       | DNSServer (Arduino ESP32 core)             |
| **HTTP**      | ESPAsyncWebServer — NCSI spoof only        |
| **NVS**       | Preferences.h                              |
| **RTOS**      | FreeRTOS                                   |

7.2 FreeRTOS Tasks
------------------

| **tcp_server_task** | Core 0 — TCP :4242, session table, streaming relay, SDMMC coordination, RSSI reads, channel matching |
| ------------------- | ---------------------------------------------------------------------------------------------------- |
| **ble_adv_task**    | Core 0 — NimBLE advertisement UUID 0xFEAD, reg_mode flag in payload                                  |
| **ncsi_task**       | Core 0 — DNSServer loop + ESPAsyncWebServer NCSI responses                                           |
| **display_task**    | Core 1 — TFT_eSPI state machine rendering                                                            |
| **touch_task**      | Core 1 — XPT2046 via T_IRQ interrupt, feeds confirm/cancel events                                    |
| **sd_buffer_task**  | Core 1 — SDMMC writes/reads for offline-receiver mode via queue                                      |

7.3 NCSI Spoof
--------------

// In ncsi.cpp — startsbefore WiFi.softAP()

dnsServer.start(53,"*", IPAddress(192,168,4,1));

// Windows:www.msftconnecttest.com/connecttest.txt

server.on("/connecttest.txt",HTTP_GET, [](AsyncWebServerRequest *r){

    r->send(200, "text/plain","Microsoft Connect Test"); });

// Android:connectivitycheck.gstatic.com/generate_204

server.on("/generate_204",HTTP_GET, [](AsyncWebServerRequest *r){

    r->send(204); });

server.begin();

// WiFi.softAP() calledAFTER server.begin()
7.4 NVS Keys
------------

| **seadrop/ssid**    | SoftAP SSID — generated first boot, immutable              |
| ------------------- | ---------------------------------------------------------- |
| **seadrop/pass**    | SoftAP WPA2 password                                       |
| **seadrop/hsssid**  | Windows hotspot SSID — set during Windows registration     |
| **seadrop/hspass**  | Windows hotspot password — set during Windows registration |
| **seadrop/devA**    | Android device name                                        |
| **seadrop/devW**    | Windows device name                                        |
| **seadrop/tokA**    | 16-byte Android auth token                                 |
| **seadrop/tokW**    | 16-byte Windows auth token                                 |
| **seadrop/regdone** | Registration complete boolean                              |

7.5 RSSI Proximity Trust
------------------------

// In rssi.cpp

typedef enum { RSSI_CLOSE,RSSI_MEDIUM, RSSI_FAR } rssi_tier_t;

// CLOSE:  RSSI > -55 dBm → auto-confirm after 3s

// MEDIUM: RSSI -55 to -70→ manual tap required

// FAR:    RSSI < -70 → manual tap + TFT warning

// Rolling average: 5samples, 3 consecutive to commit tier change

// Android RSSI:esp_wifi_ap_get_sta_list() → sta_list.sta[i].rssi

// Windows RSSI:esp_wifi_sta_get_ap_info() → ap_info.rssi



8. Android Application
   ======================

8.1 Stack
---------

| **Language**        | Kotlin                                                 |
| ------------------- | ------------------------------------------------------ |
| **Min SDK**         | API 36 (Android 16)                                    |
| **WiFi**            | WifiNetworkSuggestion — OS-managed auto-connect        |
| **Network binding** | ConnectivityManager.bindProcessToNetwork()             |
| **Background**      | Foreground Service — FOREGROUND_SERVICE_TYPE_DATA_SYNC |
| **Boot**            | RECEIVE_BOOT_COMPLETED                                 |
| **BLE**             | BluetoothLeScanner — UUID 0xFEAD                       |
| **TCP**             | Java Socket + Kotlin coroutines — persistent           |
| **Share**           | Direct Share / ChooserTargetService                    |
| **Files**           | Storage Access Framework                               |
| **Tokens**          | EncryptedSharedPreferences (AES-256)                   |
| **Queue**           | Room database                                          |
| **Locks**           | WIFI_MODE_FULL_HIGH_PERF + WakeLock                    |

8.2 Key Permissions
-------------------

ACCESS_FINE_LOCATION

CHANGE_WIFI_STATE /ACCESS_WIFI_STATE

BLUETOOTH_SCAN /BLUETOOTH_CONNECT

NEARBY_WIFI_DEVICES

FOREGROUND_SERVICE

RECEIVE_BOOT_COMPLETED

INTERNET / WAKE_LOCK

REQUEST_IGNORE_BATTERY_OPTIMIZATIONS
8.3 WifiNetworkSuggestion — 24-Hour Blacklist
---------------------------------------------

If the user manually disconnects from SeaDrop AP via thesystem WiFi picker, Android blacklists the suggestion for up to 24 hours — notjust that session. The app detects this via NetworkCallback and shows: 'SeaDropdisconnected — tap once to reconnect.' Tapping triggers manual connection whichclears the block. addNetworkSuggestions alone does not clear amanual-disconnect blacklist.
8.4 First-Launch Wizard
-----------------------

1.     Install APK. Open app. Wizard starts.

2.     'Power on SeaDrop. Screen shows REGISTRATION MODE.'

3.     'Enter SSID and password shown on SeaDrop screen.'

4.     App connects to SeaDrop AP — one-time manual stepduring setup only.

5.     App sends: REG ANDROID [device name]. ESP32 responds:TOKEN [hex]. Stored in EncryptedSharedPreferences.

6.     addNetworkSuggestions called. System shows one-timeapproval dialog.

7.     Battery optimization exemption requested. User tapsAllow.

8.     Wizard verifies: Android internet indicator stays greenafter connection. Pass = setup complete.



9. Windows Application
   ======================

9.1 Stack
---------

| **Language** | C# (.NET 8)                                                      |
| ------------ | ---------------------------------------------------------------- |
| **UI**       | WinUI 3 — system tray + settings window on demand                |
| **Hotspot**  | NetworkOperatorTetheringManager.CreateFromConnectionProfile()    |
| **Manifest** | <DeviceCapability Name="wiFiControl"/> required                  |
| **BLE**      | Windows.Devices.Bluetooth.Advertisement                          |
| **TCP**      | System.Net.Sockets — persistent listener + client on same socket |
| **Shell**    | IExplorerCommand — first-level Windows 11 context menu           |
| **Boot**     | MSIX startup task via Task Scheduler                             |
| **Tokens**   | Windows Credential Manager (DPAPI)                               |
| **Queue**    | SQLite                                                           |
| **Min OS**   | Windows 11 24H2 (build 26100)                                    |

9.2 NetworkOperatorTetheringManager — WiFi-Only Confirmed
---------------------------------------------------------

CreateFromConnectionProfile() works on WiFi-only laptops. NoSIM or mobile broadband account required. CreateFromNetworkAccountId() requiresa SIM — that API is not used. The only hard requirement is the wiFiControlDeviceCapability in the MSIX manifest. Without it, the call throwsDisabledBySystemCapability.

// Package.appxmanifest

<Capabilities>

  <DeviceCapabilityName="wiFiControl"/>

</Capabilities>

// HotspotManager.cs

var profile =NetworkInformation.GetInternetConnectionProfile();

var capability =NetworkOperatorTetheringManager

    .GetTetheringCapabilityFromConnectionProfile(profile);

if (capability !=TetheringCapability.Enabled)

    throw newInvalidOperationException($"Tethering unavailable: {capability}");

var manager =NetworkOperatorTetheringManager

    .CreateFromConnectionProfile(profile);

var config = newNetworkOperatorTetheringAccessPointConfiguration {

    Ssid = storedHotspotSsid,

    Passphrase = storedHotspotPass,

    Band =TetheringWiFiBand.TwoPointFourGigahertz

};

awaitmanager.ConfigureAccessPointAsync(config);

awaitmanager.StartTetheringAsync();
9.3 IExplorerCommand — Windows 11 First-Level Menu
--------------------------------------------------

Classic MSIX shell extensions appear in 'Show more options' onWindows 11. IExplorerCommand implementation places 'Send via SeaDrop' at thetop level. The shell extension lives in the separate SeaDrop.ShellExtensionproject, packaged together in the MSIX installer.
9.4 First-Launch Wizard
-----------------------

9.     Install MSIX. Open app. Wizard starts. Hotspot startssilently in background.

10.  'Poweron SeaDrop. Screen shows REGISTRATION MODE.'

11.  'EnterSeaDrop SSID and password shown on screen.'

12.  Appconnects to SeaDrop SoftAP — one-time manual connection during setup only.

13.  Appsends: REG WINDOWS [device name] [hs_ssid] [hs_pass]. ESP32 stores hotspotcredentials in NVS, responds: TOKEN [hex]. Stored in Credential Manager.

14.  Appdisconnects from SoftAP. ESP32 now connects to Windows hotspot in STA modeautomatically.

15.  Appverifies: TCP connection arrives from ESP32 on hotspot interface. Internetverified live on primary WiFi via external ping. Pass = setup complete.

16.  BLEwatcher and startup task registered. Hotspot runs silently on every subsequentboot.



10. Security Model
    ==================

| **Auth tokens**       | 16-byte random tokens in NVS. Every HELLO requires the correct token. Unknown tokens dropped silently.                                                        |
| --------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **SoftAP password**   | WPA2. Shown on TFT during Registration Mode only. Prevents random AP joins.                                                                                   |
| **Hotspot password**  | Generated randomly at Windows registration. Stored in NVS and Credential Manager. Not shown after setup.                                                      |
| **Registration Mode** | REG commands only accepted during Registration Mode. 5-minute timeout. Partial registrations cleared on timeout. Re-entered by holding BOOT button 3 seconds. |
| **RSSI trust**        | UX convenience layer only. Not a security boundary. Documented as such.                                                                                       |
| **Transfer consent**  | Receiver accepts every file via notification. Far tier requires TFT tap. Close tier auto-confirms after 3s with visible cancel.                               |
| **No cloud**          | No server, no relay, no internet routing through ESP32. Files never leave the local network.                                                                  |
| **NCSI scope**        | NCSI spoof serves probe URLs only. ESP32 does not proxy or relay any internet traffic.                                                                        |
| **Transport**         | Plaintext TCP behind WPA2. Acceptable for personal proximity use. TLS available on ESP32-WROVER (8MB PSRAM).                                                  |



11. Build Order — 16 Days
    =========================

| **Day 1**      | eFuse burn. Wire SPI display on GPIO 22. Wire SDMMC 4-bit. Firmware pull-ups on GPIO 2/12 in setup(). Boot clean. Benchmark SD: confirm 2.5+ MB/s. Both must pass before any further work.                                                                                                                        |
| -------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Days 2–3**   | NCSI spoof running before SoftAP activates. Connect Android: verify internet indicator stays green. Connect Windows laptop to hotspot: verify internet stays live on home WiFi. Both NCSI checks must pass.                                                                                                       |
| **Days 4–5**   | TCP server, two persistent sessions, PING/PONG. Stale session reaping. Streaming relay both directions: Android→Windows and Windows→Android. CRC32 verified. Throughput measured.                                                                                                                                 |
| **Days 6–7**   | SD buffer mode: receiver offline, file written to SDMMC, receiver reconnects and pulls. Power-cycle SeaDrop mid-transfer: verify reconnect and resume.                                                                                                                                                            |
| **Days 8–9**   | Android app: WifiNetworkSuggestion, bindProcessToNetwork, persistent TCP, HELLO. First-launch wizard. Share sheet registration. Verify phone internet intact throughout.                                                                                                                                          |
| **Days 10–11** | Windows app: NetworkOperatorTetheringManager hotspot, BLE watcher, persistent TCP listener, HELLO. First-launch wizard with internet verification gate. IExplorerCommand shell extension first-level on Windows 11. Verify laptop internet intact throughout.                                                     |
| **Days 12–13** | RSSI rolling average and tier classification in firmware. TFT state machine all six states. Minimalist 240x320 layout. Bilateral TFT direction arrow. Touch confirm/cancel.                                                                                                                                       |
| **Days 14–15** | Android share sheet bilateral: incoming and outgoing notifications. Windows tray icon, toast bilateral. Outbound queue both platforms. Boot persistence both platforms. Full end-to-end demo: different networks, powerbank SeaDrop, transfer both directions, internet verified live on both devices throughout. |
| **Day 16**     | Buffer.                                                                                                                                                                                                                                                                                                           |



12. Known Constraints
    =====================

| **Transfer speed**           | Streaming relay: approaches WiFi ceiling. SDMMC buffer: 2.5–10 MB/s. SPI SD (abandoned in v1.3) was 0.2–0.5 MB/s.                                         |
| ---------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Concurrent transfers**     | One at a time. REJECT BUSY on second request. App outbound queue handles sequential.                                                                      |
| **Android API floor**        | API 36 (Android 16). WifiNetworkSuggestion, NEARBY_WIFI_DEVICES, FOREGROUND_SERVICE_TYPE_DATA_SYNC all stable at this level.                              |
| **WifiNetworkSuggestion**    | Manual disconnect via system WiFi picker: up to 24-hour blacklist. Not just session-scoped. One notification re-enables via manual reconnect.             |
| **OEM battery killers**      | Samsung OneUI, Xiaomi MIUI, Huawei/Honor, OnePlus ColorOS. Battery exemption required. User may need to disable Adaptive Battery for SeaDrop.             |
| **Samsung Galaxy A15**       | OneUI 8 / Android 16. Standard generate_204 NCSI spoof confirmed sufficient. Old S7-era DNS workaround not needed.                                        |
| **Realtek 8852BE-VT**        | Zero concurrent channels. Solved by inverting AP role: laptop hosts hotspot, ESP32 connects. Co-existence confirmed in netsh wirelesscapabilities output. |
| **NetworkOperatorTethering** | Requires wiFiControl DeviceCapability in MSIX manifest. WiFi-only laptops confirmed working. CreateFromNetworkAccountId (SIM-required API) not used.      |
| **Windows hotspot conflict** | Overwrites existing Mobile Hotspot SSID/password. Wizard warns user. Uninstaller restores original settings.                                              |
| **eFuse burn**               | Irreversible. Required before first firmware flash for SDMMC D2 pull-up safety.                                                                           |
| **GPIO 2 pull-up**           | No external pull-up on SDMMC D0. Firmware-only via gpio_pullup_en() after boot. External pull-up blocks USB flashing.                                     |
| **TLS**                      | Not on WROOM-32 — heap exhaustion with two simultaneous TLS sessions. Available on WROVER (8MB PSRAM) or ESP32-S3.                                        |
| **SD endurance**             | Streaming relay never writes to SD. Buffer mode only on offline receiver. Industrial cards for heavy buffer-mode use.                                     |
| **NCSI cache**               | Windows caches negative NCSI results per BSSID. ESP32 starts DNS + HTTP before SoftAP activates to prevent cached negatives.                              |

SeaDrop — Specification v1.4 — June 2026
