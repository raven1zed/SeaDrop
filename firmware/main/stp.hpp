#pragma once
#include <cstdint>
#include <cstring>

namespace seadrop::stp {

// ── Wire constants ────────────────────────────────────────────
static const uint8_t MAGIC[4] = { 0x53, 0x54, 0x50, 0x21 }; // "STP!"
static const int     PORT     = 7788;
static const int     CHUNK    = 16384; // 16 KB relay chunk

// ── Frame types ───────────────────────────────────────────────
enum FrameType : uint8_t {
    HELLO   = 0x01,  // payload: device_name (C string)
    OFFER   = 0x02,  // payload: JSON {transfer_id,filename,size,mime}
    ACCEPT  = 0x03,  // payload: transfer_id (C string)
    DECLINE = 0x04,  // payload: transfer_id
    DATA    = 0x05,  // payload: raw bytes
    DONE    = 0x06,  // payload: transfer_id
    ERROR   = 0x07,  // payload: error message
    PING    = 0x08,  // payload: empty
    PONG    = 0x09,  // payload: empty
};

// ── 9-byte frame header ───────────────────────────────────────
#pragma pack(push, 1)
struct FrameHeader {
    uint8_t  magic[4];
    uint8_t  type;
    uint32_t length; // big-endian, payload bytes
};
#pragma pack(pop)

static_assert(sizeof(FrameHeader) == 9, "FrameHeader must be 9 bytes");

// ── Encode length as big-endian ───────────────────────────────
inline void encode_u32(uint8_t* out, uint32_t val) {
    out[0] = (val >> 24) & 0xFF;
    out[1] = (val >> 16) & 0xFF;
    out[2] = (val >>  8) & 0xFF;
    out[3] = (val      ) & 0xFF;
}

inline uint32_t decode_u32(const uint8_t* in) {
    return ((uint32_t)in[0] << 24)
         | ((uint32_t)in[1] << 16)
         | ((uint32_t)in[2] <<  8)
         |  (uint32_t)in[3];
}

} // namespace seadrop::stp
