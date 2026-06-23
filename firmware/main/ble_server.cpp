#include "ble_server.hpp"
#include "protocol.hpp"
#include "globals.hpp"

#include "esp_log.h"
#include "esp_nimble_cfg.h"
#include "nimble/nimble_port.h"
#include "nimble/nimble_port_freertos.h"
#include "host/ble_hs.h"
#include "host/ble_uuid.h"
#include "host/ble_gap.h"
#include "services/gap/ble_svc_gap.h"
#include "services/gatt/ble_svc_gatt.h"
#include "host/util/util.h"

#include <cstring>
#include <cstdio>

static const char* TAG = "seadrop_ble";

namespace seadrop::ble {

static uint8_t s_addr_type = 0;
static bool s_ble_sync = false;

// Forward declaration
static void start_advertising();

static void on_sync(void) {
    int rc = ble_hs_util_ensure_addr(0);
    if (rc != 0) {
        ESP_LOGE(TAG, "Error ensuring address: %d", rc);
        return;
    }
    rc = ble_hs_id_infer_auto(0, &s_addr_type);
    if (rc != 0) {
        ESP_LOGE(TAG, "Error determining address type: %d", rc);
        return;
    }

    s_ble_sync = true;
    start_advertising();
}

static void on_reset(int reason) {
    ESP_LOGW(TAG, "Resetting BLE state, reason: %d", reason);
    s_ble_sync = false;
}

static void start_advertising() {
    if (!s_ble_sync) return;

    struct ble_gap_adv_params adv_params;
    struct ble_hs_adv_fields fields;
    int rc;

    // Get current connection count
    xSemaphoreTake(g_state_mutex, portMAX_DELAY);
    int conn_count = 0;
    if (g_windows_connected) conn_count++;
    if (g_android_connected) conn_count++;
    
    char ssid[32];
    strncpy(ssid, g_ap_ssid, sizeof(ssid) - 1);
    ssid[sizeof(ssid) - 1] = '\0';
    xSemaphoreGive(g_state_mutex);

    // 1. Set advertisement fields
    memset(&fields, 0, sizeof(fields));

    // Flags: General Discoverable, BR/EDR Not Supported
    fields.flags = BLE_HS_ADV_F_DISC_GEN | BLE_HS_ADV_F_BREDR_UNSUP;

    // 16-bit Service UUID: 0xFEAD
    ble_uuid16_t service_uuid = BLE_UUID16_INIT(0xFEAD);
    fields.uuids16 = &service_uuid;
    fields.num_uuids16 = 1;
    fields.uuids16_is_complete = 1;

    // Service Data: 2 bytes UUID (FE AD) + reg_mode (1 byte) + SSID + connection count (1 byte)
    uint8_t service_data[64];
    service_data[0] = 0xAD;  // UUID 16-bit little-endian
    service_data[1] = 0xFE;
    
    // Read reg_mode state from globals
    xSemaphoreTake(g_state_mutex, portMAX_DELAY);
    uint8_t reg_mode = g_reg_state.active ? 1 : 0;
    xSemaphoreGive(g_state_mutex);
    
    service_data[2] = reg_mode;
    
    size_t ssid_len = strlen(ssid);
    if (ssid_len > 32) ssid_len = 32;
    memcpy(service_data + 3, ssid, ssid_len);
    service_data[3 + ssid_len] = (uint8_t)conn_count;
    
    fields.svc_data_uuid16 = service_data;
    fields.svc_data_uuid16_len = 3 + ssid_len + 1;

    rc = ble_gap_adv_set_fields(&fields);
    if (rc != 0) {
        ESP_LOGE(TAG, "Error setting adv fields: %d", rc);
        return;
    }

    // 2. Set scan response fields (for device name)
    struct ble_hs_adv_fields rsp_fields;
    memset(&rsp_fields, 0, sizeof(rsp_fields));
    
    rsp_fields.name = (uint8_t*)"SeaDrop";
    rsp_fields.name_len = 7;
    rsp_fields.name_is_complete = 1;

    rc = ble_gap_adv_rsp_set_fields(&rsp_fields);
    if (rc != 0) {
        ESP_LOGE(TAG, "Error setting scan response fields: %d", rc);
        return;
    }

    // 3. Start advertising
    memset(&adv_params, 0, sizeof(adv_params));
    adv_params.conn_mode = BLE_GAP_CONN_MODE_NON; // Non-connectable beacon
    adv_params.disc_mode = BLE_GAP_DISC_MODE_GEN; // General discoverable

    // Stop if already advertising to apply new parameters
    ble_gap_adv_stop();

    rc = ble_gap_adv_start(s_addr_type, nullptr, BLE_HS_FOREVER,
                           &adv_params, nullptr, nullptr);
    if (rc != 0) {
        ESP_LOGE(TAG, "Error starting advertising: %d", rc);
    } else {
        ESP_LOGI(TAG, "BLE advertising started. SSID=%s, clients=%d", ssid, conn_count);
    }
}

void update_advertisement() {
    if (s_ble_sync) {
        start_advertising();
    }
}

static void nimble_host_task(void* param) {
    ESP_LOGI(TAG, "NimBLE host task started");
    nimble_port_run(); // Blocks until nimble_port_stop() is called
    nimble_port_freertos_deinit();
}

void task(void* arg) {
    ESP_LOGI(TAG, "BLE task starting");

    int rc = nimble_port_init();
    if (rc != 0) {
        ESP_LOGE(TAG, "Failed to init nimble port: %d", rc);
        vTaskDelete(nullptr);
        return;
    }

    ble_svc_gap_init();
    ble_svc_gatt_init();

    rc = ble_svc_gap_device_name_set("SeaDrop");
    if (rc != 0) {
        ESP_LOGE(TAG, "Failed to set GAP device name: %d", rc);
    }

    ble_hs_cfg.sync_cb = on_sync;
    ble_hs_cfg.reset_cb = on_reset;

    nimble_port_freertos_init(nimble_host_task);
    vTaskDelete(nullptr);
}

} // namespace seadrop::ble
