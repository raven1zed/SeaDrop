#include "transfer.hpp"
#include "protocol.hpp"
#include "globals.hpp"

#include "esp_log.h"

#include <cstring>
#include <cstdio>
#include <cerrno>

static const char* TAG = "seadrop_transfer";

namespace seadrop::transfer {

static FILE*            s_wfp        = nullptr;  // write file pointer
static FILE*            s_rfp        = nullptr;  // read file pointer
static TransferSession* s_active     = nullptr;
static bool             s_sd_mounted = false;  // SD card not used

// RAM fallback spool buffer (used always since no SD)
static uint8_t*         s_ram_buffer      = nullptr;
static size_t           s_ram_buffer_size = 0;
static size_t           s_ram_written     = 0;
static size_t           s_ram_read_pos    = 0;

bool is_sd_mounted() {
    return s_sd_mounted;
}

bool init_sd_spool() {
    ESP_LOGI(TAG, "SD card not used (RAM spool only)");
    return false;
}

// ── Spool file I/O ────────────────────────────────────────────
bool spool_open_write(const char* transfer_id) {
    if (!s_sd_mounted) {
        // Fallback: Allocate RAM buffer
        if (s_ram_buffer) {
            free(s_ram_buffer);
            s_ram_buffer = nullptr;
        }
        s_ram_buffer = (uint8_t*)malloc(MAX_RAM_SPOOL_SIZE);
        if (!s_ram_buffer) {
            ESP_LOGE(TAG, "Failed to allocate RAM spool buffer of %d bytes", (int)MAX_RAM_SPOOL_SIZE);
            return false;
        }
        s_ram_buffer_size = MAX_RAM_SPOOL_SIZE;
        s_ram_written     = 0;
        ESP_LOGI(TAG, "Spool write opened in RAM (%d bytes limit)", (int)MAX_RAM_SPOOL_SIZE);
        return true;
    }

    char path[80];
    snprintf(path, sizeof(path), "%s/%s.bin", SD_MOUNT, transfer_id);
    s_wfp = fopen(path, "wb");
    if (!s_wfp) {
        ESP_LOGE(TAG, "Cannot open %s for write: %s", path, strerror(errno));
        return false;
    }
    ESP_LOGI(TAG, "Spool write opened: %s", path);
    return true;
}

bool spool_write(const uint8_t* data, size_t len) {
    if (!s_sd_mounted) {
        if (!s_ram_buffer) return false;
        if (s_ram_written + len > s_ram_buffer_size) {
            ESP_LOGE(TAG, "RAM spool overflow: %d + %d > %d", (int)s_ram_written, (int)len, (int)s_ram_buffer_size);
            return false;
        }
        memcpy(s_ram_buffer + s_ram_written, data, len);
        s_ram_written += len;
        return true;
    }

    if (!s_wfp) return false;
    size_t written = fwrite(data, 1, len, s_wfp);
    if (written != len) {
        ESP_LOGE(TAG, "spool_write short write: %d/%d", (int)written, (int)len);
        return false;
    }
    return true;
}

void spool_close_write() {
    if (!s_sd_mounted) {
        ESP_LOGI(TAG, "RAM spool write closed. Size: %d bytes", (int)s_ram_written);
        return;
    }

    if (s_wfp) {
        fflush(s_wfp);
        fclose(s_wfp);
        s_wfp = nullptr;
        ESP_LOGI(TAG, "Spool write closed");
    }
}

bool spool_open_read(const char* transfer_id) {
    if (!s_sd_mounted) {
        if (!s_ram_buffer) return false;
        s_ram_read_pos = 0;
        ESP_LOGI(TAG, "RAM spool read opened");
        return true;
    }

    char path[80];
    snprintf(path, sizeof(path), "%s/%s.bin", SD_MOUNT, transfer_id);
    s_rfp = fopen(path, "rb");
    if (!s_rfp) {
        ESP_LOGE(TAG, "Cannot open %s for read: %s", path, strerror(errno));
        return false;
    }
    return true;
}

size_t spool_read(uint8_t* out, size_t max_len) {
    if (!s_sd_mounted) {
        if (!s_ram_buffer) return 0;
        size_t available = s_ram_written - s_ram_read_pos;
        size_t to_read = (max_len < available) ? max_len : available;
        if (to_read == 0) return 0;
        memcpy(out, s_ram_buffer + s_ram_read_pos, to_read);
        s_ram_read_pos += to_read;
        return to_read;
    }

    if (!s_rfp) return 0;
    return fread(out, 1, max_len, s_rfp);
}

void spool_close_read() {
    if (!s_sd_mounted) {
        ESP_LOGI(TAG, "RAM spool read closed");
        return;
    }

    if (s_rfp) { fclose(s_rfp); s_rfp = nullptr; }
}

void spool_delete(const char* transfer_id) {
    if (!s_sd_mounted) {
        if (s_ram_buffer) {
            free(s_ram_buffer);
            s_ram_buffer      = nullptr;
            s_ram_buffer_size = 0;
            s_ram_written     = 0;
            s_ram_read_pos    = 0;
            ESP_LOGI(TAG, "RAM spool buffer freed");
        }
        return;
    }

    char path[80];
    snprintf(path, sizeof(path), "%s/%s.bin", SD_MOUNT, transfer_id);
    if (remove(path) == 0) {
        ESP_LOGI(TAG, "Spool deleted: %s", path);
    }
}

size_t spool_file_size(const char* transfer_id) {
    if (!s_sd_mounted) {
        return s_ram_written;
    }
    char path[80];
    snprintf(path, sizeof(path), "%s/%s.bin", SD_MOUNT, transfer_id);
    FILE* f = fopen(path, "rb");
    if (!f) return 0;
    fseek(f, 0, SEEK_END);
    size_t sz = (size_t)ftell(f);
    fclose(f);
    return sz;
}

// ── CRC32 verification (streaming, 512-byte chunks) ─────────────
bool verify_crc32(const char* transfer_id, size_t file_size, uint32_t expected_crc) {
    if (!spool_open_read(transfer_id)) return false;

    uint32_t crc = 0xFFFFFFFF;
    uint8_t chunk[512];
    size_t remaining = file_size;
    while (remaining > 0) {
        size_t to_read = (remaining < sizeof(chunk)) ? remaining : sizeof(chunk);
        size_t n = spool_read(chunk, to_read);
        if (n == 0) {
            ESP_LOGE(TAG, "CRC32: unexpected EOF");
            spool_close_read();
            return false;
        }
        for (size_t i = 0; i < n; i++) {
            crc ^= chunk[i];
            for (int j = 0; j < 8; j++) {
                crc = (crc & 1) ? (crc >> 1) ^ 0xEDB88320 : (crc >> 1);
            }
        }
        remaining -= n;
    }
    spool_close_read();

    crc ^= 0xFFFFFFFF;
    bool ok = (crc == expected_crc);
    if (!ok) {
        ESP_LOGE(TAG, "CRC32 mismatch: got 0x%08lX, expected 0x%08lX", (unsigned long)crc, (unsigned long)expected_crc);
    } else {
        ESP_LOGI(TAG, "CRC32 OK: 0x%08lX", (unsigned long)crc);
    }
    return ok;
}

// ── Session management ────────────────────────────────────────
void begin_session(TransferSession* session) {
    s_active = session;
    spool_open_write(session->transfer_id);
    ESP_LOGI(TAG, "Session begun: %s (%lu bytes)", session->filename, (unsigned long)session->file_size);
}

void end_session() {
    spool_close_write();
    spool_close_read();
    if (s_active) {
        spool_delete(s_active->transfer_id);
        s_active = nullptr;
    }
}

void task(void* /*arg*/) {
    // Lightweight monitor — main work is in stp_server task
    while (true) {
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
}

} // namespace seadrop::transfer
