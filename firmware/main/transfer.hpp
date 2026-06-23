#pragma once
#include <cstdint>
#include <cstddef>
#include "protocol.hpp"

namespace seadrop::transfer {

// Mount SD card on VSPI using shield pins (SCK=25, MOSI=27, MISO=26, CS=14)
// Returns true on success.
bool   init_sd_spool();

bool   is_sd_mounted();
constexpr size_t MAX_RAM_SPOOL_SIZE = 128 * 1024; // 128 KB RAM spool buffer limit

// Open /sdcard/<transfer_id>.bin for writing. Call before spool_write().
bool   spool_open_write(const char* transfer_id);

// Append len bytes to the open spool file. Returns false on write error or SD full.
bool   spool_write(const uint8_t* data, size_t len);

// Close the write handle and flush FAT metadata.
void   spool_close_write();

// Open /sdcard/<transfer_id>.bin for reading. Call before spool_read().
bool   spool_open_read(const char* transfer_id);

// Read up to max_len bytes. Returns actual bytes read (0 = EOF or error).
size_t spool_read(uint8_t* out, size_t max_len);

// Close the read handle.
void   spool_close_read();

// Delete the spool file for a given transfer_id.
void   spool_delete(const char* transfer_id);

// Return file size in bytes. Returns 0 if not found.
size_t spool_file_size(const char* transfer_id);

// Compute CRC32 incrementally from SD card file.
// Does NOT require loading the full file into RAM — reads 512 bytes at a time.
bool   verify_crc32(const char* transfer_id, size_t file_size, uint32_t expected_crc);

// Set the active session. Opens spool file for writing.
void   begin_session(seadrop::TransferSession* session);

// Mark session complete, close spool, update session state.
void   end_session();

// FreeRTOS task — monitors session completion, triggers cleanup.
void   task(void* arg);

} // namespace seadrop::transfer
