#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/semphr.h"
#include "esp_system.h"
#include "esp_log.h"
#include "esp_mac.h"
#include "esp_netif.h"
#include "esp_event.h"
#include "nvs_flash.h"

#include "protocol.hpp"
#include "globals.hpp"
#include "stp_server.hpp"
#include "display.hpp"
#include "ble_server.hpp"
#include "ncsi.hpp"

static const char* TAG = "seadrop_main";

// ── Global shared state definitions ──────────────────────────
// Declared extern in globals.hpp; all tasks access under g_state_mutex.
SemaphoreHandle_t           g_state_mutex             = nullptr;
seadrop::TransferSession    g_session                 = {};
seadrop::DisplayState       g_display_state           = seadrop::DisplayState::IDLE;
seadrop::RegistrationState  g_reg_state               = {};
bool                        g_windows_connected       = false;
bool                        g_android_connected       = false;
char                        g_windows_device_name[64] = {};
char                        g_android_device_name[64] = {};
char                        g_ap_ssid[32]             = {};

extern "C" void app_main(void) {
    // 1. NVS — must be first (tokens and hotspot creds stored here)
    esp_err_t ret = nvs_flash_init();
    if (ret == ESP_ERR_NVS_NO_FREE_PAGES || ret == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        ret = nvs_flash_init();
    }
    ESP_ERROR_CHECK(ret);

    // 2. TCP/IP stack + default event loop
    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());

    // 3. Global state mutex
    g_state_mutex = xSemaphoreCreateMutex();
    configASSERT(g_state_mutex != nullptr);

    // 4. Generate SoftAP SSID from last 2 bytes of WiFi MAC
    //    Result: "SeaDrop_XXYY" where XX YY are uppercase hex MAC bytes.
    uint8_t mac[6];
    ESP_ERROR_CHECK(esp_read_mac(mac, ESP_MAC_WIFI_SOFTAP));
    snprintf(g_ap_ssid, sizeof(g_ap_ssid), "%s%02X%02X",
             seadrop::AP_SSID_PREFIX, mac[4], mac[5]);
    ESP_LOGI(TAG, "SeaDrop PTD  AP: %s  (MAC=" MACSTR ")", g_ap_ssid, MAC2STR(mac));

    // 5. NCSI spoof — DNS/53 + HTTP/80 — MUST start before WiFi.
    //    Windows and Android probe connectivity the moment they associate.
    //    If the spoof is not ready they mark the BSSID as "no internet"
    //    and may not retest (spec §4.5 Windows NCSI caching behaviour).
    seadrop::ncsi::init();

    // 6. Display init (8-bit Parallel ILI9341) — returns false if not attached
    bool display_available = seadrop::display::init();
    ESP_LOGI(TAG, "[TFT] Available: %s", display_available ? "yes" : "no");

    // 7. Display Task — Core 1 — only started if ILI9341 is present (AGENTS.md boot spec)
    if (display_available) {
        xTaskCreatePinnedToCore(
            seadrop::display::task, "display_task",
            4096, nullptr, 3, nullptr, 1);
    }

    // 8. BLE advertising Task — Core 1
    xTaskCreatePinnedToCore(
        seadrop::ble::task, "ble_task",
        5120, nullptr, 5, nullptr, 1);

    // 9. STP server (WiFi APSTA + TCP :4242) — Core 0
    //     This task initialises WiFi, enters Registration Mode if needed,
    //     and accepts incoming connections from Android and Windows.
    xTaskCreatePinnedToCore(
        seadrop::stp_server::task, "stp_task",
        14336, nullptr, 5, nullptr, 0);

    ESP_LOGI(TAG, "SeaDrop PTD v1.5 — boot complete");
}
