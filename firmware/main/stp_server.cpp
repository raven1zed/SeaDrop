#include "stp_server.hpp"
#include "protocol.hpp"
#include "globals.hpp"
#include "ble_server.hpp"
#include "transfer.hpp"

#include "esp_wifi.h"
#include "esp_event.h"
#include "esp_netif.h"
#include "esp_log.h"
#include "esp_mac.h"
#include "mdns.h"
#include "esp_random.h"
#include "nvs_flash.h"
#include "nvs.h"
#include "driver/gpio.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/semphr.h"
#include "lwip/sockets.h"
#include "lwip/netdb.h"

#include <cstring>
#include <cstdio>
#include <cstdlib>
#include <cctype>
#include <cerrno>
#include <algorithm>

// Safe truncate-and-copy. Always null-terminates dst. Avoids -Wstringop-truncation.
template <size_t N>
static inline void safe_copy(char (&dst)[N], const char* src) {
    if (!src) { dst[0] = '\0'; return; }
    size_t len = std::min<size_t>(strlen(src), N - 1);
    memcpy(dst, src, len);
    dst[len] = '\0';
}

static const char* TAG = "seadrop_stp";

// Registration Mode timeout: 5 minutes
#define REG_TIMEOUT_MS (5 * 60 * 1000)
// BOOT button GPIO (active LOW, internal pull-up)
#define BOOT_GPIO GPIO_NUM_0
// Hold duration to re-enter Registration Mode (3 seconds)
#define BOOT_HOLD_MS 3000

namespace seadrop::stp_server {

static volatile int s_android_fd = -1;
static volatile int s_windows_fd = -1;
static SemaphoreHandle_t s_win_write_mutex = nullptr;
static SemaphoreHandle_t s_and_write_mutex = nullptr;

// ── NVS helpers ───────────────────────────────────────────────
static bool nvs_get_str(const char* ns, const char* key, char* out, size_t max_len) {
    nvs_handle_t h;
    if (nvs_open(ns, NVS_READONLY, &h) != ESP_OK) return false;
    size_t len = max_len;
    esp_err_t err = ::nvs_get_str(h, key, out, &len);
    nvs_close(h);
    return (err == ESP_OK);
}

static bool nvs_set_str(const char* ns, const char* key, const char* val) {
    nvs_handle_t h;
    if (nvs_open(ns, NVS_READWRITE, &h) != ESP_OK) return false;
    ::nvs_set_str(h, key, val);
    nvs_commit(h);
    nvs_close(h);
    return true;
}

static bool verify_token(const char* type, const char* token) {
    char saved[32] = {};
    const char* key = (strcmp(type, "ANDROID") == 0) ? "tokA" : "tokW";
    if (!nvs_get_str("seadrop_auth", key, saved, sizeof(saved))) return false;
    return (strcmp(saved, token) == 0);
}

static void generate_token(char* out16) {
    uint8_t b[8];
    esp_fill_random(b, 8);
    for (int i = 0; i < 8; i++) sprintf(out16 + i * 2, "%02x", b[i]);
    out16[16] = '\0';
}

// CRC32 is computed in transfer::verify_crc32 (transfer.cpp) — single source of truth.

// ── Socket helpers ────────────────────────────────────────────
static int read_line(int fd, char* buf, int max_len) {
    int len = 0;
    while (len < max_len - 1) {
        char c;
        if (recv(fd, &c, 1, 0) <= 0) return -1;
        if (c == '\n') { buf[len] = '\0'; return len; }
        if (c != '\r') buf[len++] = c;
    }
    buf[len] = '\0';
    return len;
}

static bool send_str(int fd, const char* str) {
    size_t len = strlen(str), sent = 0;
    while (sent < len) {
        int n = send(fd, str + sent, len - sent, 0);
        if (n <= 0) return false;
        sent += n;
    }
    return true;
}

// ── RSSI monitoring ───────────────────────────────────────────
// Called periodically to read RSSI for both clients and update rolling averages.
static void update_rssi() {
    xSemaphoreTake(g_state_mutex, portMAX_DELAY);

    // Android RSSI: read from SoftAP station table
    if (g_android_connected) {
        wifi_sta_list_t sta_list = {};
        if (esp_wifi_ap_get_sta_list(&sta_list) == ESP_OK && sta_list.num > 0) {
            int8_t rssi = sta_list.sta[0].rssi;
            g_session.android_rssi_sum += rssi;
            g_session.android_rssi_samples++;
            if (g_session.android_rssi_samples >= 5) {
                g_session.android_rssi = (int8_t)(g_session.android_rssi_sum / 5);
                g_session.android_tier = classify_rssi(g_session.android_rssi);
                g_session.android_rssi_sum     = 0;
                g_session.android_rssi_samples = 0;
            }
        }
    }

    // Windows RSSI: read from STA connection (ESP32 as client of Windows hotspot)
    if (g_windows_connected) {
        wifi_ap_record_t ap_info = {};
        if (esp_wifi_sta_get_ap_info(&ap_info) == ESP_OK) {
            int8_t rssi = ap_info.rssi;
            g_session.windows_rssi_sum += rssi;
            g_session.windows_rssi_samples++;
            if (g_session.windows_rssi_samples >= 5) {
                g_session.windows_rssi = (int8_t)(g_session.windows_rssi_sum / 5);
                g_session.windows_tier = classify_rssi(g_session.windows_rssi);
                g_session.windows_rssi_sum     = 0;
                g_session.windows_rssi_samples = 0;
            }
        }
    }

    xSemaphoreGive(g_state_mutex);
}

// ── Registration Mode ─────────────────────────────────────────
static void enter_registration_mode() {
    xSemaphoreTake(g_state_mutex, portMAX_DELAY);
    g_reg_state.active              = true;
    g_reg_state.android_registered = false;
    g_reg_state.windows_registered = false;
    g_reg_state.start_tick          = xTaskGetTickCount();
    g_reg_state.timeout_ms          = REG_TIMEOUT_MS;
    g_display_state                  = DisplayState::REGISTRATION;
    xSemaphoreGive(g_state_mutex);
    ble::update_advertisement();
    ESP_LOGI(TAG, "Registration Mode ACTIVE — 5 minute window");
}

// Handle a REG command (sent over TCP during registration)
// Syntax: REG ANDROID <name>  or  REG WINDOWS <name> <hotspot_ssid> <hotspot_pass>
static bool handle_reg_command(const char* line, int client_fd) {
    xSemaphoreTake(g_state_mutex, portMAX_DELAY);
    bool reg_active = g_reg_state.active;
    uint32_t elapsed = (xTaskGetTickCount() - g_reg_state.start_tick) * portTICK_PERIOD_MS;
    if (elapsed >= REG_TIMEOUT_MS) {
        g_reg_state.active  = false;
        g_display_state     = g_android_connected ? DisplayState::ANDROID_CONNECTED : DisplayState::IDLE;
        reg_active          = false;
    }
    xSemaphoreGive(g_state_mutex);

    if (!reg_active) {
        send_str(client_fd, "REJECT REG_CLOSED\n");
        return false;
    }

    char type[16] = {}, name[64] = {};
    char hsssid[64] = {}, hspass[64] = {};
    int parsed = sscanf(line, "REG %15s %63s %63s %63s", type, name, hsssid, hspass);
    if (parsed < 2) {
        send_str(client_fd, "REJECT BAD_PARAMETERS\n");
        return false;
    }

    char token[17];
    generate_token(token);

    if (strcmp(type, "ANDROID") == 0) {
        nvs_set_str("seadrop_auth", "devA", name);
        nvs_set_str("seadrop_auth", "tokA", token);
        xSemaphoreTake(g_state_mutex, portMAX_DELAY);
        safe_copy(g_android_device_name, name);
        g_reg_state.android_registered = true;
        xSemaphoreGive(g_state_mutex);
        ESP_LOGI(TAG, "Registered Android: %s token=%s", name, token);
    } else if (strcmp(type, "WINDOWS") == 0) {
        nvs_set_str("seadrop_auth", "devW", name);
        nvs_set_str("seadrop_auth", "tokW", token);
        // Store Windows hotspot credentials so ESP32 STA can auto-connect
        if (hsssid[0]) nvs_set_str("seadrop_auth", "hsssid", hsssid);
        if (hspass[0]) nvs_set_str("seadrop_auth", "hspass", hspass);
        xSemaphoreTake(g_state_mutex, portMAX_DELAY);
        safe_copy(g_windows_device_name, name);
        g_reg_state.windows_registered = true;
        xSemaphoreGive(g_state_mutex);
        ESP_LOGI(TAG, "Registered Windows: %s hs=%s token=%s", name, hsssid, token);
    } else {
        send_str(client_fd, "REJECT BAD_TYPE\n");
        return false;
    }

    char resp[32];
    snprintf(resp, sizeof(resp), "TOKEN %s\n", token);
    send_str(client_fd, resp);

    // If both registered, exit Registration Mode and set regdone
    xSemaphoreTake(g_state_mutex, portMAX_DELAY);
    bool both_done = g_reg_state.android_registered && g_reg_state.windows_registered;
    if (both_done) {
        g_reg_state.active = false;
        g_display_state    = DisplayState::IDLE;
    }
    xSemaphoreGive(g_state_mutex);

    if (both_done) {
        nvs_set_str("seadrop_auth", "regdone", "1");
        ESP_LOGI(TAG, "Both devices registered — Registration Mode complete");
        ble::update_advertisement();
    }

    return true;
}

// ── Android client loop ───────────────────────────────────────
static void android_loop(int fd) {
    char line[512];
    while (true) {
        int len = read_line(fd, line, sizeof(line));
        if (len < 0) break;
        if (len == 0) continue;

        ESP_LOGI(TAG, "[AND] %s", line);

        char cmd[16] = {};
        sscanf(line, "%15s", cmd);

        if (strcmp(cmd, "SEND") == 0) {
            char fname[MAX_FILENAME_LEN] = {};
            uint32_t size = 0, crc = 0;
            if (sscanf(line, "SEND %255s %lu %lu", fname,
                       (unsigned long*)&size, (unsigned long*)&crc) < 3) {
                send_str(fd, "REJECT BAD_PARAMETERS\n");
                continue;
            }

            if (!transfer::is_sd_mounted() && size > transfer::MAX_RAM_SPOOL_SIZE) {
                ESP_LOGE(TAG, "File size %lu exceeds RAM spool limit (%d bytes) - no SD card mounted",
                         (unsigned long)size, (int)transfer::MAX_RAM_SPOOL_SIZE);
                send_str(fd, "REJECT SD_FULL\n");
                continue;
            }

            xSemaphoreTake(g_state_mutex, portMAX_DELAY);
            memset(&g_session, 0, sizeof(g_session));
            g_session.active            = true;
            g_session.file_size         = size;
            g_session.android_to_windows = true;
            g_session.windows_accepted  = false;
            snprintf(g_session.transfer_id, sizeof(g_session.transfer_id),
                     "tx_%lu", (unsigned long)xTaskGetTickCount());
            safe_copy(g_session.filename, fname);
            // Store CRC hex in checksum field
            snprintf(g_session.checksum, sizeof(g_session.checksum),
                     "%08lX", (unsigned long)crc);
            g_display_state = DisplayState::TRANSFERRING;
            char tid[16] = {}; size_t tl = std::min<size_t>(strlen(g_session.transfer_id), sizeof(tid) - 1); memcpy(tid, g_session.transfer_id, tl);
            xSemaphoreGive(g_state_mutex);

            transfer::begin_session(&g_session);
            send_str(fd, "SEND_ACK\n");

            // Stream binary data to SD
            uint8_t chunk[1024];
            uint32_t total_read = 0;
            bool write_ok = true;
            while (total_read < size) {
                uint32_t to_read = ((size - total_read) < sizeof(chunk))
                                   ? (size - total_read) : sizeof(chunk);
                int n = recv(fd, chunk, to_read, 0);
                if (n <= 0) { write_ok = false; break; }
                if (write_ok && !transfer::spool_write(chunk, n)) write_ok = false;
                total_read += n;
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_session.bytes_written = total_read;
                xSemaphoreGive(g_state_mutex);
            }
            transfer::spool_close_write();

            if (!write_ok) {
                send_str(fd, "REJECT SD_WRITE_FAIL\n");
                transfer::end_session();
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_display_state = DisplayState::ERROR;
                xSemaphoreGive(g_state_mutex);
                vTaskDelay(pdMS_TO_TICKS(5000));
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_display_state = g_windows_connected
                    ? DisplayState::BOTH_CONNECTED : DisplayState::ANDROID_CONNECTED;
                xSemaphoreGive(g_state_mutex);
                continue;
            }

            // Read SEND_DONE
            len = read_line(fd, line, sizeof(line));
            if (len < 0 || strcmp(line, "SEND_DONE") != 0) {
                send_str(fd, "REJECT MISSING_DONE\n");
            transfer::end_session();
            continue;
        }

        // Verify CRC32
        if (transfer::verify_crc32(tid, size, crc)) {
            xSemaphoreTake(g_state_mutex, portMAX_DELAY);
            g_session.checksum_ok    = true;
            g_session.upload_complete = true;
            xSemaphoreGive(g_state_mutex);
            send_str(fd, "ACK\n");

            // Notify Windows via its TCP connection
            int win_fd = s_windows_fd;
                if (win_fd >= 0) {
                    char notify[512];
                    snprintf(notify, sizeof(notify), "NOTIFY %s %lu\n",
                             fname, (unsigned long)size);
                    xSemaphoreTake(s_win_write_mutex, portMAX_DELAY);
                    send_str(win_fd, notify);
                    xSemaphoreGive(s_win_write_mutex);
                }
            } else {
                send_str(fd, "REJECT CHECKSUM_FAIL\n");
                transfer::end_session();
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_display_state = DisplayState::ERROR;
                xSemaphoreGive(g_state_mutex);
                vTaskDelay(pdMS_TO_TICKS(5000));
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_display_state = g_windows_connected
                    ? DisplayState::BOTH_CONNECTED : DisplayState::ANDROID_CONNECTED;
                xSemaphoreGive(g_state_mutex);
            }

        } else if (strcmp(cmd, "PULL") == 0) {
            xSemaphoreTake(g_state_mutex, portMAX_DELAY);
            bool ready  = g_session.active && g_session.upload_complete;
            uint32_t sz = g_session.file_size;
            char tid[16] = {}; size_t tl = std::min<size_t>(strlen(g_session.transfer_id), sizeof(tid) - 1); memcpy(tid, g_session.transfer_id, tl);
            xSemaphoreGive(g_state_mutex);

            if (!ready) { send_str(fd, "REJECT NOT_READY\n"); continue; }

            char pull_hdr[64];
            snprintf(pull_hdr, sizeof(pull_hdr), "PULL_DATA %lu\n", (unsigned long)sz);

            xSemaphoreTake(s_and_write_mutex, portMAX_DELAY);
            send_str(fd, pull_hdr);

            if (transfer::spool_open_read(tid)) {
                uint8_t chunk[2048];
                uint32_t sent = 0;
                while (sent < sz) {
                    size_t n = transfer::spool_read(chunk, sizeof(chunk));
                    if (n == 0) break;
                    size_t blk = 0;
                    while (blk < n) {
                        int r = send(fd, chunk + blk, n - blk, 0);
                        if (r <= 0) goto pull_error_and;
                        blk += r;
                    }
                    sent += n;
                }
                transfer::spool_close_read();
            }
            send_str(fd, "PULL_DONE\n");
            xSemaphoreGive(s_and_write_mutex);

            // Wait for ACK
            len = read_line(fd, line, sizeof(line));
            if (len >= 0 && strcmp(line, "ACK") == 0) {
                transfer::end_session();
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_display_state = DisplayState::COMPLETE;
                xSemaphoreGive(g_state_mutex);
                vTaskDelay(pdMS_TO_TICKS(3000));
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_display_state = g_android_connected
                    ? DisplayState::BOTH_CONNECTED : DisplayState::WINDOWS_CONNECTED;
                xSemaphoreGive(g_state_mutex);
            } else {
                pull_error_and:
                transfer::spool_close_read();
                if (xSemaphoreGetMutexHolder(s_and_write_mutex) ==
                    xTaskGetCurrentTaskHandle()) {
                    xSemaphoreGive(s_and_write_mutex);
                }
                transfer::end_session();
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_display_state = DisplayState::ERROR;
                xSemaphoreGive(g_state_mutex);
                vTaskDelay(pdMS_TO_TICKS(5000));
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_display_state = g_android_connected
                    ? DisplayState::BOTH_CONNECTED : DisplayState::ANDROID_CONNECTED;
                xSemaphoreGive(g_state_mutex);
            }

        } else if (strcmp(cmd, "REG") == 0) {
            handle_reg_command(line, fd);

        } else if (strcmp(cmd, "PING") == 0) {
            send_str(fd, "PONG\n");

        } else if (strcmp(cmd, "STATUS") == 0) {
            xSemaphoreTake(g_state_mutex, portMAX_DELAY);
            char resp[128];
            snprintf(resp, sizeof(resp), "STATUS_RESP %d %s\n",
                     (int)g_display_state, g_windows_device_name);
            xSemaphoreGive(g_state_mutex);
            send_str(fd, resp);

        } else if (strcmp(cmd, "CANCEL") == 0) {
            transfer::end_session();
            xSemaphoreTake(g_state_mutex, portMAX_DELAY);
            g_display_state = g_windows_connected
                ? DisplayState::BOTH_CONNECTED : DisplayState::ANDROID_CONNECTED;
            xSemaphoreGive(g_state_mutex);
        }
    }
}

// ── Windows client loop ───────────────────────────────────────
static void windows_loop(int fd) {
    char line[512];
    while (true) {
        int len = read_line(fd, line, sizeof(line));
        if (len < 0) break;
        if (len == 0) continue;

        ESP_LOGI(TAG, "[WIN] %s", line);

        char cmd[16] = {};
        sscanf(line, "%15s", cmd);

        if (strcmp(cmd, "CHANNEL") == 0) {
            // Windows reports its current home WiFi channel.
            // Match our SoftAP to it to reduce radio switching overhead.
            int ch = 0;
            sscanf(line, "CHANNEL %d", &ch);
            if (ch >= 1 && ch <= 13) {
                uint8_t cur_ch = 0, cur_secondary = 0;
                esp_wifi_get_channel(&cur_ch, (wifi_second_chan_t*)&cur_secondary);
                if (cur_ch != (uint8_t)ch) {
                    esp_wifi_set_channel((uint8_t)ch, WIFI_SECOND_CHAN_NONE);
                    ESP_LOGI(TAG, "SoftAP channel changed: %d → %d", cur_ch, ch);
                }
            }

        } else if (strcmp(cmd, "SEND") == 0) {
            char fname[MAX_FILENAME_LEN] = {};
            uint32_t size = 0, crc = 0;
            if (sscanf(line, "SEND %255s %lu %lu", fname,
                       (unsigned long*)&size, (unsigned long*)&crc) < 3) {
                send_str(fd, "REJECT BAD_PARAMETERS\n");
                continue;
            }

            if (!transfer::is_sd_mounted() && size > transfer::MAX_RAM_SPOOL_SIZE) {
                ESP_LOGE(TAG, "File size %lu exceeds RAM spool limit (%d bytes) - no SD card mounted",
                         (unsigned long)size, (int)transfer::MAX_RAM_SPOOL_SIZE);
                send_str(fd, "REJECT SD_FULL\n");
                continue;
            }

            xSemaphoreTake(g_state_mutex, portMAX_DELAY);
            memset(&g_session, 0, sizeof(g_session));
            g_session.active            = true;
            g_session.file_size         = size;
            g_session.android_to_windows = false; // Sending from Windows to Android
            g_session.windows_accepted  = false;
            snprintf(g_session.transfer_id, sizeof(g_session.transfer_id),
                     "tx_%lu", (unsigned long)xTaskGetTickCount());
            safe_copy(g_session.filename, fname);
            // Store CRC hex in checksum field
            snprintf(g_session.checksum, sizeof(g_session.checksum),
                     "%08lX", (unsigned long)crc);
            g_display_state = DisplayState::TRANSFERRING;
            char tid[16] = {}; size_t tl = std::min<size_t>(strlen(g_session.transfer_id), sizeof(tid) - 1); memcpy(tid, g_session.transfer_id, tl);
            xSemaphoreGive(g_state_mutex);

            transfer::begin_session(&g_session);
            send_str(fd, "SEND_ACK\n");

            // Stream binary data to SD/RAM spool
            uint8_t chunk[1024];
            uint32_t total_read = 0;
            bool write_ok = true;
            while (total_read < size) {
                uint32_t to_read = ((size - total_read) < sizeof(chunk))
                                   ? (size - total_read) : sizeof(chunk);
                int n = recv(fd, chunk, to_read, 0);
                if (n <= 0) { write_ok = false; break; }
                if (write_ok && !transfer::spool_write(chunk, n)) write_ok = false;
                total_read += n;
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_session.bytes_written = total_read;
                xSemaphoreGive(g_state_mutex);
            }
            transfer::spool_close_write();

            if (!write_ok) {
                send_str(fd, "REJECT SD_WRITE_FAIL\n");
                transfer::end_session();
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_display_state = DisplayState::ERROR;
                xSemaphoreGive(g_state_mutex);
                vTaskDelay(pdMS_TO_TICKS(5000));
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_display_state = g_windows_connected
                    ? DisplayState::BOTH_CONNECTED : DisplayState::WINDOWS_CONNECTED;
                xSemaphoreGive(g_state_mutex);
                continue;
            }

            // Read SEND_DONE
            len = read_line(fd, line, sizeof(line));
            if (len < 0 || strcmp(line, "SEND_DONE") != 0) {
                send_str(fd, "REJECT MISSING_DONE\n");
            transfer::end_session();
            continue;
        }

        // Verify CRC32
        if (transfer::verify_crc32(tid, size, crc)) {
            xSemaphoreTake(g_state_mutex, portMAX_DELAY);
            g_session.checksum_ok    = true;
            g_session.upload_complete = true;
            xSemaphoreGive(g_state_mutex);
            send_str(fd, "ACK\n");

            // Notify Android via its TCP connection
            int and_fd = s_android_fd;
                if (and_fd >= 0) {
                    char notify[512];
                    snprintf(notify, sizeof(notify), "NOTIFY %s %lu\n",
                             fname, (unsigned long)size);
                    xSemaphoreTake(s_and_write_mutex, portMAX_DELAY);
                    send_str(and_fd, notify);
                    xSemaphoreGive(s_and_write_mutex);
                }
            } else {
                send_str(fd, "REJECT CHECKSUM_FAIL\n");
                transfer::end_session();
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_display_state = DisplayState::ERROR;
                xSemaphoreGive(g_state_mutex);
                vTaskDelay(pdMS_TO_TICKS(5000));
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_display_state = g_windows_connected
                    ? DisplayState::BOTH_CONNECTED : DisplayState::WINDOWS_CONNECTED;
                xSemaphoreGive(g_state_mutex);
            }

        } else if (strcmp(cmd, "PULL") == 0) {
            xSemaphoreTake(g_state_mutex, portMAX_DELAY);
            bool ready  = g_session.active && g_session.upload_complete;
            uint32_t sz = g_session.file_size;
            char tid[16] = {}; size_t tl = std::min<size_t>(strlen(g_session.transfer_id), sizeof(tid) - 1); memcpy(tid, g_session.transfer_id, tl);
            xSemaphoreGive(g_state_mutex);

            if (!ready) { send_str(fd, "REJECT NOT_READY\n"); continue; }

            char pull_hdr[64];
            snprintf(pull_hdr, sizeof(pull_hdr), "PULL_DATA %lu\n", (unsigned long)sz);

            xSemaphoreTake(s_win_write_mutex, portMAX_DELAY);
            send_str(fd, pull_hdr);

            if (transfer::spool_open_read(tid)) {
                uint8_t chunk[2048];
                uint32_t sent = 0;
                while (sent < sz) {
                    size_t n = transfer::spool_read(chunk, sizeof(chunk));
                    if (n == 0) break;
                    size_t blk = 0;
                    while (blk < n) {
                        int r = send(fd, chunk + blk, n - blk, 0);
                        if (r <= 0) goto pull_error;
                        blk += r;
                    }
                    sent += n;
                }
                transfer::spool_close_read();
            }
            send_str(fd, "PULL_DONE\n");
            xSemaphoreGive(s_win_write_mutex);

            // Wait for ACK
            len = read_line(fd, line, sizeof(line));
            if (len >= 0 && strcmp(line, "ACK") == 0) {
                transfer::end_session();
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_display_state = DisplayState::COMPLETE;
                xSemaphoreGive(g_state_mutex);
                vTaskDelay(pdMS_TO_TICKS(3000));
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_display_state = g_android_connected
                    ? DisplayState::BOTH_CONNECTED : DisplayState::WINDOWS_CONNECTED;
                xSemaphoreGive(g_state_mutex);
            } else {
                pull_error:
                transfer::spool_close_read();
                if (xSemaphoreGetMutexHolder(s_win_write_mutex) ==
                    xTaskGetCurrentTaskHandle()) {
                    xSemaphoreGive(s_win_write_mutex);
                }
                transfer::end_session();
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_display_state = DisplayState::ERROR;
                xSemaphoreGive(g_state_mutex);
                vTaskDelay(pdMS_TO_TICKS(5000));
                xSemaphoreTake(g_state_mutex, portMAX_DELAY);
                g_display_state = g_android_connected
                    ? DisplayState::BOTH_CONNECTED : DisplayState::WINDOWS_CONNECTED;
                xSemaphoreGive(g_state_mutex);
            }

        } else if (strcmp(cmd, "REG") == 0) {
            handle_reg_command(line, fd);

        } else if (strcmp(cmd, "PING") == 0) {
            send_str(fd, "PONG\n");

        } else if (strcmp(cmd, "STATUS") == 0) {
            xSemaphoreTake(g_state_mutex, portMAX_DELAY);
            char resp[128];
            snprintf(resp, sizeof(resp), "STATUS_RESP %d %s\n",
                     (int)g_display_state, g_android_device_name);
            xSemaphoreGive(g_state_mutex);
            send_str(fd, resp);

        } else if (strcmp(cmd, "CANCEL") == 0) {
            transfer::end_session();
            xSemaphoreTake(g_state_mutex, portMAX_DELAY);
            g_display_state = g_android_connected
                ? DisplayState::BOTH_CONNECTED : DisplayState::WINDOWS_CONNECTED;
            xSemaphoreGive(g_state_mutex);
        }
    }
}

// ── Client handler task ───────────────────────────────────────
static void client_handler_task(void* arg) {
    int fd = (int)(intptr_t)arg;

    char line[256];
    int len = read_line(fd, line, sizeof(line));
    if (len <= 0) { close(fd); vTaskDelete(nullptr); return; }

    char cmd[16] = {}, type[16] = {}, name[64] = {}, token[32] = {};
    int parsed = sscanf(line, "%15s %15s %63s %31s", cmd, type, name, token);

    // Allow un-authenticated REG commands during Registration Mode
    if (parsed >= 3 && strcmp(cmd, "REG") == 0) {
        handle_reg_command(line, fd);
        close(fd);
        vTaskDelete(nullptr);
        return;
    }

    if (parsed < 4 || strcmp(cmd, "HELLO") != 0) {
        send_str(fd, "REJECT BAD_HANDSHAKE\n");
        close(fd); vTaskDelete(nullptr); return;
    }

    if (!verify_token(type, token)) {
        send_str(fd, "REJECT AUTH_FAIL\n");
        close(fd); vTaskDelete(nullptr); return;
    }

    send_str(fd, "HELLO_ACK 1\n");

    bool is_win = (strcmp(type, "WINDOWS") == 0);
    if (is_win) {
        if (s_windows_fd >= 0) close(s_windows_fd);
        s_windows_fd = fd;
        xSemaphoreTake(g_state_mutex, portMAX_DELAY);
        g_windows_connected = true;
        safe_copy(g_windows_device_name, name);
        g_display_state = g_android_connected
            ? DisplayState::BOTH_CONNECTED : DisplayState::WINDOWS_CONNECTED;
        xSemaphoreGive(g_state_mutex);
        ble::update_advertisement();
        ESP_LOGI(TAG, "Windows authenticated: %s", name);
        windows_loop(fd);
        close(fd);
        if (s_windows_fd == fd) s_windows_fd = -1;
        xSemaphoreTake(g_state_mutex, portMAX_DELAY);
        g_windows_connected = false;
        g_display_state = g_android_connected
            ? DisplayState::ANDROID_CONNECTED : DisplayState::IDLE;
        xSemaphoreGive(g_state_mutex);
        ble::update_advertisement();
    } else {
        if (s_android_fd >= 0) close(s_android_fd);
        s_android_fd = fd;
        xSemaphoreTake(g_state_mutex, portMAX_DELAY);
        g_android_connected = true;
        safe_copy(g_android_device_name, name);
        g_display_state = g_windows_connected
            ? DisplayState::BOTH_CONNECTED : DisplayState::ANDROID_CONNECTED;
        xSemaphoreGive(g_state_mutex);
        ble::update_advertisement();
        ESP_LOGI(TAG, "Android authenticated: %s", name);
        android_loop(fd);
        close(fd);
        if (s_android_fd == fd) s_android_fd = -1;
        xSemaphoreTake(g_state_mutex, portMAX_DELAY);
        g_android_connected = false;
        g_display_state = g_windows_connected
            ? DisplayState::WINDOWS_CONNECTED : DisplayState::IDLE;
        xSemaphoreGive(g_state_mutex);
        ble::update_advertisement();
    }
    vTaskDelete(nullptr);
}

// ── BOOT button monitor ───────────────────────────────────────
static void boot_button_task(void* /*arg*/) {
    gpio_config_t io = {};
    io.pin_bit_mask  = (1ULL << BOOT_GPIO);
    io.mode          = GPIO_MODE_INPUT;
    io.pull_up_en    = GPIO_PULLUP_ENABLE;
    io.pull_down_en  = GPIO_PULLDOWN_DISABLE;
    io.intr_type     = GPIO_INTR_DISABLE;
    gpio_config(&io);

    while (true) {
        if (gpio_get_level(BOOT_GPIO) == 0) {
            vTaskDelay(pdMS_TO_TICKS(BOOT_HOLD_MS));
            if (gpio_get_level(BOOT_GPIO) == 0) {
                ESP_LOGI(TAG, "BOOT button held — re-entering Registration Mode");
                enter_registration_mode();
                // Debounce: wait for release
                while (gpio_get_level(BOOT_GPIO) == 0) vTaskDelay(pdMS_TO_TICKS(100));
            }
        }
        vTaskDelay(pdMS_TO_TICKS(50));
    }
}

// ── Registration Mode timeout monitor ────────────────────────
static void reg_timeout_task(void* /*arg*/) {
    while (true) {
        vTaskDelay(pdMS_TO_TICKS(1000));
        xSemaphoreTake(g_state_mutex, portMAX_DELAY);
        if (g_reg_state.active) {
            uint32_t elapsed = (xTaskGetTickCount() - g_reg_state.start_tick)
                               * portTICK_PERIOD_MS;
            if (elapsed >= REG_TIMEOUT_MS) {
                g_reg_state.active  = false;
                g_display_state = g_android_connected
                    ? (g_windows_connected ? DisplayState::BOTH_CONNECTED
                                           : DisplayState::ANDROID_CONNECTED)
                    : (g_windows_connected ? DisplayState::WINDOWS_CONNECTED
                                           : DisplayState::IDLE);
                ESP_LOGW(TAG, "Registration Mode timed out — partial regs cleared");
                // Clear partial registrations from NVS
                if (!g_reg_state.android_registered) {
                    nvs_handle_t h;
                    if (nvs_open("seadrop_auth", NVS_READWRITE, &h) == ESP_OK) {
                        nvs_erase_key(h, "tokA"); nvs_erase_key(h, "devA");
                        nvs_commit(h); nvs_close(h);
                    }
                }
                if (!g_reg_state.windows_registered) {
                    nvs_handle_t h;
                    if (nvs_open("seadrop_auth", NVS_READWRITE, &h) == ESP_OK) {
                        nvs_erase_key(h, "tokW"); nvs_erase_key(h, "devW");
                        nvs_commit(h); nvs_close(h);
                    }
                }
            }
        }
        xSemaphoreGive(g_state_mutex);
    }
}

// ── RSSI monitor task ─────────────────────────────────────────
static void rssi_task(void* /*arg*/) {
    while (true) {
        vTaskDelay(pdMS_TO_TICKS(5000));  // every 5 seconds
        update_rssi();
    }
}

// ── WiFi event handler ────────────────────────────────────────
static void wifi_event_handler(void* arg, esp_event_base_t base,
                               int32_t id, void* data) {
    if (base == WIFI_EVENT) {
        if (id == WIFI_EVENT_AP_STACONNECTED) {
            auto* e = (wifi_event_ap_staconnected_t*)data;
            ESP_LOGI(TAG, "AP: client joined MAC=" MACSTR, MAC2STR(e->mac));
        } else if (id == WIFI_EVENT_AP_STADISCONNECTED) {
            auto* e = (wifi_event_ap_stadisconnected_t*)data;
            ESP_LOGI(TAG, "AP: client left MAC=" MACSTR, MAC2STR(e->mac));
        } else if (id == WIFI_EVENT_STA_START) {
            ESP_LOGI(TAG, "STA: started, connecting to '%s'...", WIN_HOTSPOT_SSID);
            esp_wifi_connect();
        } else if (id == WIFI_EVENT_STA_DISCONNECTED) {
            auto* e = (wifi_event_sta_disconnected_t*)data;
            ESP_LOGW(TAG, "STA: disconnected (reason %d) — retrying in 3s", e->reason);
            xSemaphoreTake(g_state_mutex, portMAX_DELAY);
            g_windows_connected = false;
            if (g_display_state == DisplayState::BOTH_CONNECTED ||
                g_display_state == DisplayState::WINDOWS_CONNECTED) {
                g_display_state = g_android_connected
                    ? DisplayState::ANDROID_CONNECTED : DisplayState::IDLE;
            }
            xSemaphoreGive(g_state_mutex);
            ble::update_advertisement();
            vTaskDelay(pdMS_TO_TICKS(3000));
            esp_wifi_connect();
        }
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        auto* e = (ip_event_got_ip_t*)data;
        ESP_LOGI(TAG, "STA: got IP " IPSTR " from Windows hotspot",
                 IP2STR(&e->ip_info.ip));
    }
}

// ── Main task ─────────────────────────────────────────────────
void task(void* /*arg*/) {
    s_win_write_mutex = xSemaphoreCreateMutex();
    s_and_write_mutex = xSemaphoreCreateMutex();
    configASSERT(s_win_write_mutex && s_and_write_mutex);

    // Load registered names from NVS
    xSemaphoreTake(g_state_mutex, portMAX_DELAY);
    nvs_get_str("seadrop_auth", "devA", g_android_device_name,
                sizeof(g_android_device_name));
    nvs_get_str("seadrop_auth", "devW", g_windows_device_name,
                sizeof(g_windows_device_name));
    xSemaphoreGive(g_state_mutex);

    // Check if registration has been completed before
    char regdone[4] = {};
    bool need_registration = !nvs_get_str("seadrop_auth", "regdone", regdone, sizeof(regdone))
                             || strcmp(regdone, "1") != 0;

    // ── WiFi APSTA ────────────────────────────────────────────
    esp_netif_create_default_wifi_ap();
    esp_netif_create_default_wifi_sta();

    wifi_init_config_t wcfg = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&wcfg));

    ESP_ERROR_CHECK(esp_event_handler_instance_register(
        WIFI_EVENT, ESP_EVENT_ANY_ID, wifi_event_handler, nullptr, nullptr));
    ESP_ERROR_CHECK(esp_event_handler_instance_register(
        IP_EVENT, IP_EVENT_STA_GOT_IP, wifi_event_handler, nullptr, nullptr));

    // SoftAP config (Android connects here). ap.ssid and ap.password are
    // fixed-size uint8_t arrays — use explicit-length copy.
    wifi_config_t ap_cfg = {};
    memset(&ap_cfg, 0, sizeof(ap_cfg));
    size_t ap_ssid_len = std::min<size_t>(strlen(g_ap_ssid), sizeof(ap_cfg.ap.ssid));
    memcpy(ap_cfg.ap.ssid, g_ap_ssid, ap_ssid_len);
    size_t ap_pass_len = std::min<size_t>(strlen(AP_PASS), sizeof(ap_cfg.ap.password));
    memcpy(ap_cfg.ap.password, AP_PASS, ap_pass_len);
    ap_cfg.ap.ssid_len       = strlen(g_ap_ssid);
    ap_cfg.ap.channel        = AP_CHANNEL;
    ap_cfg.ap.authmode       = WIFI_AUTH_WPA2_PSK;
    ap_cfg.ap.max_connection = AP_MAX_STA;
    ap_cfg.ap.pmf_cfg.required = false;

    // STA config: connect to Windows Mobile Hotspot
    // If registered, load hotspot SSID/pass from NVS; else use defaults.
    // sta.ssid/password are uint8_t arrays — explicit memcpy with length cap.
    wifi_config_t sta_cfg = {};
    memset(&sta_cfg, 0, sizeof(sta_cfg));
    char hs_ssid[64] = {}, hs_pass[64] = {};
    const char* use_ssid = nullptr;
    const char* use_pass = nullptr;
    if (nvs_get_str("seadrop_auth", "hsssid", hs_ssid, sizeof(hs_ssid)) &&
        nvs_get_str("seadrop_auth", "hspass", hs_pass, sizeof(hs_pass))) {
        use_ssid = hs_ssid;
        use_pass = hs_pass;
    } else {
        use_ssid = WIN_HOTSPOT_SSID;
        use_pass = WIN_HOTSPOT_PASS;
    }
    size_t sl = std::min<size_t>(strlen(use_ssid), sizeof(sta_cfg.sta.ssid) - 1);
    memcpy(sta_cfg.sta.ssid, use_ssid, sl);
    size_t pl = std::min<size_t>(strlen(use_pass), sizeof(sta_cfg.sta.password) - 1);
    memcpy(sta_cfg.sta.password, use_pass, pl);
    sta_cfg.sta.scan_method          = WIFI_ALL_CHANNEL_SCAN;
    sta_cfg.sta.threshold.authmode   = WIFI_AUTH_WPA2_PSK;

    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_APSTA));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_AP,  &ap_cfg));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_STA, &sta_cfg));
    ESP_ERROR_CHECK(esp_wifi_start());

    // Initialize mDNS so Windows can discover ESP32 as seadrop.local
    esp_err_t mdns_err = mdns_init();
    if (mdns_err == ESP_OK) {
        mdns_hostname_set("seadrop");
        mdns_instance_name_set("SeaDrop PTD");
        mdns_service_add("SeaDrop-STP", "_seadrop", "_tcp", 4242, nullptr, 0);
        ESP_LOGI(TAG, "mDNS initialized. Hostname: seadrop.local");
    } else {
        ESP_LOGE(TAG, "mDNS init failed: %d", mdns_err);
    }

    ESP_LOGI(TAG, "WiFi APSTA up. SoftAP: %s  STA→: %s",
             g_ap_ssid, (char*)sta_cfg.sta.ssid);

    // Enter Registration Mode if first boot
    if (need_registration) {
        enter_registration_mode();
    }

    // Spawn auxiliary tasks
    xTaskCreate(boot_button_task, "boot_btn",  2048, nullptr, 3, nullptr);
    xTaskCreate(reg_timeout_task, "reg_tmout", 2048, nullptr, 3, nullptr);
    xTaskCreate(rssi_task,        "rssi",      2048, nullptr, 2, nullptr);

    // ── TCP server on port 4242 ───────────────────────────────
    int srv = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    int opt = 1;
    setsockopt(srv, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt));

    struct sockaddr_in addr = {};
    addr.sin_family      = AF_INET;
    addr.sin_port        = htons(STP_PORT);
    addr.sin_addr.s_addr = htonl(INADDR_ANY);

    if (bind(srv, (struct sockaddr*)&addr, sizeof(addr)) != 0) {
        ESP_LOGE(TAG, "TCP bind failed: %d", errno);
        close(srv); vTaskDelete(nullptr); return;
    }
    listen(srv, 8);
    ESP_LOGI(TAG, "STP server listening on TCP/%d", STP_PORT);

    while (true) {
        struct sockaddr_in client_addr;
        socklen_t client_len = sizeof(client_addr);
        int cfd = accept(srv, (struct sockaddr*)&client_addr, &client_len);
        if (cfd >= 0) {
            int ka = 1, ki = 5, kv = 2, kc = 3;
            setsockopt(cfd, SOL_SOCKET, SO_KEEPALIVE, &ka, sizeof(ka));
            setsockopt(cfd, IPPROTO_TCP, TCP_KEEPIDLE,  &ki, sizeof(ki));
            setsockopt(cfd, IPPROTO_TCP, TCP_KEEPINTVL, &kv, sizeof(kv));
            setsockopt(cfd, IPPROTO_TCP, TCP_KEEPCNT,   &kc, sizeof(kc));
            xTaskCreate(client_handler_task, "stp_client", 6144,
                        (void*)(intptr_t)cfd, 5, nullptr);
        }
    }

    close(srv);
    vTaskDelete(nullptr);
}

} // namespace seadrop::stp_server
