#pragma once
#include <cstdint>
#include <cstddef>

// ============================================================
// SeaDrop PTD — Shared Protocol Constants  v1.5
// Hardware: ESP32-WROOM-32 + 8-bit Parallel ILI9341 TFT
// ============================================================

namespace seadrop {

// ------------------------------------------------------------
// BLE advertisement service UUID (16-bit)
// ------------------------------------------------------------
constexpr uint16_t BLE_SERVICE_UUID16 = 0xFEAD;

// ------------------------------------------------------------
// WiFi SoftAP  (Android connects here)
// ------------------------------------------------------------
constexpr const char*  AP_SSID_PREFIX  = "SeaDrop_";
constexpr const char*  AP_PASS         = "seadrop2026";
constexpr const char*  AP_IP           = "192.168.4.1";
constexpr uint8_t      AP_CHANNEL      = 6;
constexpr uint8_t      AP_MAX_STA      = 4;

// ------------------------------------------------------------
// Windows Mobile Hotspot credentials
// The Windows app creates a hotspot with exactly these values.
// The ESP32 STA connects to it automatically after registration.
// ------------------------------------------------------------
constexpr const char*  WIN_HOTSPOT_SSID = "SeaDrop-PC";
constexpr const char*  WIN_HOTSPOT_PASS = "seadrop2026";

// ------------------------------------------------------------
// STP TCP port
// ------------------------------------------------------------
constexpr uint16_t STP_PORT = 4242;

// ------------------------------------------------------------
// SD Card  — SDMMC 4-bit (NOT SPI)
// gpio_pullup_en() called in firmware before mount.
// GPIO 12 (D2) safe only after eFuse VDD_SDIO burn.
// ------------------------------------------------------------
//   SDMMC_CLK  → GPIO 14
//   SDMMC_CMD  → GPIO 15   (10k pull-up)
//   SDMMC_D0   → GPIO 2    (NO external pull-up — firmware only)
//   SDMMC_D1   → GPIO 4    (10k pull-up)
//   SDMMC_D2   → GPIO 12   (10k pull-up — eFuse burn required)
//   SDMMC_D3   → GPIO 13   (10k pull-up)
constexpr const char* SD_MOUNT = "/sdcard";

// ------------------------------------------------------------
// ILI9341 8-bit Parallel display — GPIO assignments
// LCD_RST → GPIO 32
// LCD_CS  → GPIO 33 (active LOW)
// LCD_RS  → GPIO 2  (D/C: 0=command, 1=data)
// LCD_WR  → GPIO 4  (active LOW write strobe)
// LCD_RD  → GPIO 15 (active LOW read strobe - tied LOW for write-only)
// LCD_D0-7 → GPIO 12-14, 26, 25, 21, 22, 27 (8-bit data bus)
// ------------------------------------------------------------
constexpr int LCD_RST   = 32;
constexpr int LCD_CS    = 33;
constexpr int LCD_RS    = 2;
constexpr int LCD_WR    = 4;
constexpr int LCD_RD    = 15;
constexpr int LCD_D0    = 12;
constexpr int LCD_D1    = 13;
constexpr int LCD_D2    = 26;
constexpr int LCD_D3    = 25;
constexpr int LCD_D4    = 21;
constexpr int LCD_D5    = 22;
constexpr int LCD_D6    = 27;
constexpr int LCD_D7    = 14;

// XPT2046 touch — shares SPI bus (optional, not wired yet)
constexpr int TOUCH_CS  = -1;  // Not connected

// LCD dimensions (portrait)
constexpr uint16_t LCD_W = 240;
constexpr uint16_t LCD_H = 320;

// ILI9341 RGB565 palette — AGENTS.md design tokens
constexpr uint16_t COLOR_WHITE        = 0xFFFF;  // white text / values
constexpr uint16_t COLOR_BLACK        = 0x0000;  // pure black (compat)
constexpr uint16_t COLOR_NAVY         = 0x18C5;  // #1A1A2E — screen background
constexpr uint16_t COLOR_SPACE_ORANGE = 0xEAE0;  // #E85D00 — primary accent
constexpr uint16_t COLOR_SUCCESS      = 0x262B;  // #22C55E — connected / done
constexpr uint16_t COLOR_ERROR_RED    = 0xEA28;  // #EF4444 — error / failed
constexpr uint16_t COLOR_TEXT_GREY    = 0x8C51;  // #888888 — secondary labels
constexpr uint16_t COLOR_TILE_DARK    = 0x2128;  // #252540 — dark tile surface
constexpr uint16_t COLOR_AMBER        = 0xFD00;  // medium RSSI signal bar
constexpr uint16_t COLOR_LGRAY        = 0xC618;  // light grey (label text on tiles)
constexpr uint16_t COLOR_DGRAY        = 0x2104;  // dark grey (compat)
constexpr uint16_t COLOR_CYAN         = 0x07FF;  // compat
constexpr uint16_t COLOR_GREEN        = 0x07E0;  // compat
constexpr uint16_t COLOR_RED          = 0xF800;  // compat
constexpr uint16_t COLOR_ORANGE       = 0xFD20;  // compat

// ------------------------------------------------------------
// RSSI proximity trust tiers (spec §2.4)
// ------------------------------------------------------------
enum class RssiTier : uint8_t {
    CLOSE  = 0,  // > -55 dBm  — auto-confirm after 3s
    MEDIUM = 1,  // -55 to -70 — manual TFT tap required
    FAR    = 2,  // < -70 dBm  — manual tap, "signal weak" warning
};

constexpr int RSSI_CLOSE_DBM  = -55;
constexpr int RSSI_MEDIUM_DBM = -70;

inline RssiTier classify_rssi(int8_t rssi) {
    if (rssi > RSSI_CLOSE_DBM)  return RssiTier::CLOSE;
    if (rssi > RSSI_MEDIUM_DBM) return RssiTier::MEDIUM;
    return RssiTier::FAR;
}

// ------------------------------------------------------------
// Transfer session — shared between tasks (lock g_state_mutex)
// ------------------------------------------------------------
constexpr size_t MAX_FILENAME_LEN = 256;

struct TransferSession {
    char     transfer_id[16];
    char     filename[MAX_FILENAME_LEN];
    char     mime_type[64];
    char     checksum[9];          // CRC32 hex — 8 chars + NUL (spec uses CRC32 only, no SHA256)
    uint32_t file_size;
    uint32_t bytes_written;         // bytes written to SD
    bool     android_to_windows;
    bool     windows_accepted;
    bool     upload_complete;       // Android→SD phase done
    bool     checksum_ok;
    bool     active;

    // RSSI proximity (rolling 5-sample averages, updated every 5s)
    int8_t   android_rssi;          // last averaged RSSI (dBm)
    int8_t   windows_rssi;
    RssiTier android_tier;
    RssiTier windows_tier;
    uint8_t  android_rssi_samples;  // counter 0-5
    uint8_t  windows_rssi_samples;
    int32_t  android_rssi_sum;      // running sum for rolling avg
    int32_t  windows_rssi_sum;
};

// ------------------------------------------------------------
// Display state machine (spec §2.5)
// ------------------------------------------------------------
enum class DisplayState : uint8_t {
    IDLE,
    ANDROID_CONNECTED,
    WINDOWS_CONNECTED,
    BOTH_CONNECTED,
    REGISTRATION,       // Registration Mode — shows countdown + slot fill
    CONFIRM,            // Transfer confirm — shows filename, direction, 3s countdown
    TRANSFERRING,
    COMPLETE,
    ERROR,
};

// ------------------------------------------------------------
// Registration state (shared, protected by g_state_mutex)
// ------------------------------------------------------------
struct RegistrationState {
    bool     active;              // currently in Registration Mode
    bool     android_registered;
    bool     windows_registered;
    uint32_t start_tick;          // xTaskGetTickCount() at mode entry
    uint32_t timeout_ms;          // 5 * 60 * 1000
};

} // namespace seadrop
