#include "display.hpp"
#include "protocol.hpp"
#include "globals.hpp"

#include "driver/gpio.h"
#include "esp_log.h"
#include "esp_rom_sys.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#include "soc/soc.h"
#include "soc/gpio_reg.h"

#include <cstring>
#include <cstdio>
#include <algorithm>

static const char* TAG = "seadrop_display";

namespace seadrop::display {

static const int LCD_DATA_PINS[8] = { LCD_D0, LCD_D1, LCD_D2, LCD_D3, LCD_D4, LCD_D5, LCD_D6, LCD_D7 };

static const uint32_t DATA_PINS_MASK = (1UL << 12) | (1UL << 13) | (1UL << 26) | (1UL << 25) | (1UL << 21) | (1UL << 22) | (1UL << 27) | (1UL << 14);

static inline uint32_t data_to_gpio_mask(uint8_t data) {
    uint32_t mask = 0;
    if (data & 0x01) mask |= (1UL << 12);
    if (data & 0x02) mask |= (1UL << 13);
    if (data & 0x04) mask |= (1UL << 26);
    if (data & 0x08) mask |= (1UL << 25);
    if (data & 0x10) mask |= (1UL << 21);
    if (data & 0x20) mask |= (1UL << 22);
    if (data & 0x40) mask |= (1UL << 27);
    if (data & 0x80) mask |= (1UL << 14);
    return mask;
}

static inline void write_data_bus(uint8_t data) {
    uint32_t set_mask = data_to_gpio_mask(data);
    uint32_t clear_mask = DATA_PINS_MASK & ~set_mask;
    REG_WRITE(GPIO_OUT_W1TS_REG, set_mask);
    REG_WRITE(GPIO_OUT_W1TC_REG, clear_mask);
    __asm__ __volatile__("memw");
}

static void tft_write_cmd(uint8_t cmd) {
    REG_WRITE(GPIO_OUT1_W1TC_REG, (1UL << (LCD_CS - 32))); // CS Low (GPIO 33)
    REG_WRITE(GPIO_OUT_W1TC_REG, (1UL << LCD_RS));        // RS Low (GPIO 2)
    __asm__ __volatile__("memw");
    
    write_data_bus(cmd);
    
    REG_WRITE(GPIO_OUT_W1TC_REG, (1UL << LCD_WR));        // WR Low (GPIO 4)
    __asm__ __volatile__("memw");
    esp_rom_delay_us(1);
    
    REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_WR));        // WR High
    __asm__ __volatile__("memw");
    esp_rom_delay_us(1);
    
    REG_WRITE(GPIO_OUT1_W1TS_REG, (1UL << (LCD_CS - 32))); // CS High
    __asm__ __volatile__("memw");
}

static void tft_write_data(uint8_t data) {
    REG_WRITE(GPIO_OUT1_W1TC_REG, (1UL << (LCD_CS - 32))); // CS Low (GPIO 33)
    REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_RS));        // RS High (GPIO 2)
    __asm__ __volatile__("memw");
    
    write_data_bus(data);
    
    REG_WRITE(GPIO_OUT_W1TC_REG, (1UL << LCD_WR));        // WR Low (GPIO 4)
    __asm__ __volatile__("memw");
    esp_rom_delay_us(1);
    
    REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_WR));        // WR High
    __asm__ __volatile__("memw");
    esp_rom_delay_us(1);
    
    REG_WRITE(GPIO_OUT1_W1TS_REG, (1UL << (LCD_CS - 32))); // CS High
    __asm__ __volatile__("memw");
}

static void tft_write_data16(uint16_t data) {
    tft_write_data(data >> 8);
    tft_write_data(data & 0xFF);
}

static void tft_cmd(uint8_t cmd) { tft_write_cmd(cmd); }
static void tft_data8(uint8_t d) { tft_write_data(d); }
static void tft_data16(uint16_t d) { tft_write_data16(d); }

// ── ILI9341 initialisation sequence ──────────────────────────
static void ili9341_init() {
    // Hardware reset with clean control line states
    gpio_set_level((gpio_num_t)LCD_CS, 1);
    gpio_set_level((gpio_num_t)LCD_WR, 1);
    gpio_set_level((gpio_num_t)LCD_RD, 1);

    gpio_set_level((gpio_num_t)LCD_RST, 0);
    vTaskDelay(pdMS_TO_TICKS(50));
    gpio_set_level((gpio_num_t)LCD_RST, 1);
    vTaskDelay(pdMS_TO_TICKS(150));

    tft_cmd(0x01); vTaskDelay(pdMS_TO_TICKS(150));  // SW Reset - wait 150ms
    tft_cmd(0x28);                                  // Display OFF

    tft_cmd(0xCF); tft_data8(0x00); tft_data8(0xC1); tft_data8(0x30);
    tft_cmd(0xED); tft_data8(0x64); tft_data8(0x03); tft_data8(0x12); tft_data8(0x81);
    tft_cmd(0xE8); tft_data8(0x85); tft_data8(0x00); tft_data8(0x78);
    tft_cmd(0xCB); tft_data8(0x39); tft_data8(0x2C); tft_data8(0x00); tft_data8(0x34); tft_data8(0x02);
    tft_cmd(0xF7); tft_data8(0x20);
    tft_cmd(0xEA); tft_data8(0x00); tft_data8(0x00);
    tft_cmd(0xC0); tft_data8(0x23);              // Power control
    tft_cmd(0xC1); tft_data8(0x10);              // Power control 2
    tft_cmd(0xC5); tft_data8(0x3E); tft_data8(0x28);  // VCOM
    tft_cmd(0xC7); tft_data8(0x86);
    tft_cmd(0x36); tft_data8(0x48);              // Memory access — portrait
    tft_cmd(0x3A); tft_data8(0x55);              // Pixel format: 16bpp RGB565
    tft_cmd(0xB1); tft_data8(0x00); tft_data8(0x18);
    tft_cmd(0xB6); tft_data8(0x08); tft_data8(0x82); tft_data8(0x27);
    tft_cmd(0xF2); tft_data8(0x00);
    tft_cmd(0x26); tft_data8(0x01);

    // Positive gamma
    tft_cmd(0xE0);
    const uint8_t pos[] = {0x0F,0x31,0x2B,0x0C,0x0E,0x08,0x4E,0xF1,
                           0x37,0x07,0x10,0x03,0x0E,0x09,0x00};
    for (auto b : pos) tft_data8(b);

    // Negative gamma
    tft_cmd(0xE1);
    const uint8_t neg[] = {0x00,0x0E,0x14,0x03,0x11,0x07,0x31,0xC1,
                           0x48,0x08,0x0F,0x0C,0x31,0x36,0x0F};
    for (auto b : neg) tft_data8(b);

    tft_cmd(0x11); vTaskDelay(pdMS_TO_TICKS(120)); // Sleep Out
    tft_cmd(0x29);                                  // Display ON
    ESP_LOGI(TAG, "ILI9341 8-bit parallel init complete");
}

// ── Drawing primitives ────────────────────────────────────────
static void set_window(uint16_t x0, uint16_t y0, uint16_t x1, uint16_t y1) {
    tft_cmd(0x2A);
    tft_data16(x0); tft_data16(x1);
    tft_cmd(0x2B);
    tft_data16(y0); tft_data16(y1);
    tft_cmd(0x2C);
}

// Fill a rectangle — optimized with persistent CS/RS select
static void fill_rect(uint16_t x, uint16_t y, uint16_t w, uint16_t h, uint16_t color) {
    if (w == 0 || h == 0) return;
    set_window(x, y, x + w - 1, y + h - 1);

    const uint8_t hi = color >> 8;
    const uint8_t lo = color & 0xFF;
    uint32_t total = (uint32_t)w * h;
    
    // Set CS low and RS high once for the entire batch to get maximum speed!
    REG_WRITE(GPIO_OUT1_W1TC_REG, (1UL << (LCD_CS - 32))); // CS Low
    REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_RS));        // RS High (data mode)
    __asm__ __volatile__("memw");

    uint32_t count = 0;
    while (total > 0) {
        uint32_t send = (total > 128) ? 128 : total;
        for (uint32_t i = 0; i < send; i++) {
            write_data_bus(hi);
            REG_WRITE(GPIO_OUT_W1TC_REG, (1UL << LCD_WR));        // WR Low
            __asm__ __volatile__("memw");
            esp_rom_delay_us(1);
            REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_WR));        // WR High
            __asm__ __volatile__("memw");
            esp_rom_delay_us(1);

            write_data_bus(lo);
            REG_WRITE(GPIO_OUT_W1TC_REG, (1UL << LCD_WR));        // WR Low
            __asm__ __volatile__("memw");
            esp_rom_delay_us(1);
            REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_WR));        // WR High
            __asm__ __volatile__("memw");
            esp_rom_delay_us(1);
        }
        total -= send;
        count += send;
        if (count >= 16384) {
            // Settle JTAG and yield FreeRTOS watchdog
            REG_WRITE(GPIO_OUT1_W1TS_REG, (1UL << (LCD_CS - 32))); // CS High
            __asm__ __volatile__("memw");
            vTaskDelay(1);
            REG_WRITE(GPIO_OUT1_W1TC_REG, (1UL << (LCD_CS - 32))); // CS Low
            REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_RS));        // RS High
            __asm__ __volatile__("memw");
            count = 0;
        }
    }
    REG_WRITE(GPIO_OUT1_W1TS_REG, (1UL << (LCD_CS - 32))); // CS High
    __asm__ __volatile__("memw");
}

// 5×7 ASCII font (chars 0x20–0x7A), 2× scaled.
static const uint8_t FONT5x7[][5] = {
    {0x00,0x00,0x00,0x00,0x00}, // 0x20 space
    {0x00,0x00,0x5F,0x00,0x00}, // 0x21 !
    {0x00,0x07,0x00,0x07,0x00}, // 0x22 "
    {0x14,0x7F,0x14,0x7F,0x14}, // 0x23 #
    {0x24,0x2A,0x7F,0x2A,0x12}, // 0x24 $
    {0x23,0x13,0x08,0x64,0x62}, // 0x25 %
    {0x36,0x49,0x55,0x22,0x50}, // 0x26 &
    {0x00,0x05,0x03,0x00,0x00}, // 0x27 '
    {0x00,0x1C,0x22,0x41,0x00}, // 0x28 (
    {0x00,0x41,0x22,0x1C,0x00}, // 0x29 )
    {0x14,0x08,0x3E,0x08,0x14}, // 0x2A *
    {0x08,0x08,0x3E,0x08,0x08}, // 0x2B +
    {0x00,0x50,0x30,0x00,0x00}, // 0x2C ,
    {0x08,0x08,0x08,0x08,0x08}, // 0x2D -
    {0x00,0x60,0x60,0x00,0x00}, // 0x2E .
    {0x20,0x10,0x08,0x04,0x02}, // 0x2F /
    {0x3E,0x51,0x49,0x45,0x3E}, // 0x30 0
    {0x00,0x42,0x7F,0x40,0x00}, // 0x31 1
    {0x42,0x61,0x51,0x49,0x46}, // 0x32 2
    {0x21,0x41,0x45,0x4B,0x31}, // 0x33 3
    {0x18,0x14,0x12,0x7F,0x10}, // 0x34 4
    {0x27,0x45,0x45,0x45,0x39}, // 0x35 5
    {0x3C,0x4A,0x49,0x49,0x30}, // 0x36 6
    {0x01,0x71,0x09,0x05,0x03}, // 0x37 7
    {0x36,0x49,0x49,0x49,0x36}, // 0x38 8
    {0x06,0x49,0x49,0x29,0x1E}, // 0x39 9
    {0x00,0x36,0x36,0x00,0x00}, // 0x3A :
    {0x00,0x56,0x36,0x00,0x00}, // 0x3B ;
    {0x08,0x14,0x22,0x41,0x00}, // 0x3C <
    {0x14,0x14,0x14,0x14,0x14}, // 0x3D =
    {0x00,0x41,0x22,0x14,0x08}, // 0x3E >
    {0x02,0x01,0x51,0x09,0x06}, // 0x3F ?
    {0x32,0x49,0x79,0x41,0x3E}, // 0x40 @
    {0x7E,0x11,0x11,0x11,0x7E}, // 0x41 A
    {0x7F,0x49,0x49,0x49,0x36}, // 0x42 B
    {0x3E,0x41,0x41,0x41,0x22}, // 0x43 C
    {0x7F,0x41,0x41,0x22,0x1C}, // 0x44 D
    {0x7F,0x49,0x49,0x49,0x41}, // 0x45 E
    {0x7F,0x09,0x09,0x09,0x01}, // 0x46 F
    {0x3E,0x41,0x49,0x49,0x7A}, // 0x47 G
    {0x7F,0x08,0x08,0x08,0x7F}, // 0x48 H
    {0x00,0x41,0x7F,0x41,0x00}, // 0x49 I
    {0x20,0x40,0x41,0x3F,0x01}, // 0x4A J
    {0x7F,0x08,0x14,0x22,0x41}, // 0x4B K
    {0x7F,0x40,0x40,0x40,0x40}, // 0x4C L
    {0x7F,0x02,0x0C,0x02,0x7F}, // 0x4D M
    {0x7F,0x04,0x08,0x10,0x7F}, // 0x4E N
    {0x3E,0x41,0x41,0x41,0x3E}, // 0x4F O
    {0x7F,0x09,0x09,0x09,0x06}, // 0x50 P
    {0x3E,0x41,0x51,0x21,0x5E}, // 0x51 Q
    {0x7F,0x09,0x19,0x29,0x46}, // 0x52 R
    {0x46,0x49,0x49,0x49,0x31}, // 0x53 S
    {0x01,0x01,0x7F,0x01,0x01}, // 0x54 T
    {0x3F,0x40,0x40,0x40,0x3F}, // 0x55 U
    {0x1F,0x20,0x40,0x20,0x1F}, // 0x56 V
    {0x3F,0x40,0x38,0x40,0x3F}, // 0x57 W
    {0x63,0x14,0x08,0x14,0x63}, // 0x58 X
    {0x07,0x08,0x70,0x08,0x07}, // 0x59 Y
    {0x61,0x51,0x49,0x45,0x43}, // 0x5A Z
    {0x00,0x7F,0x41,0x41,0x00}, // 0x5B [
    {0x02,0x04,0x08,0x10,0x20}, // 0x5C backslash
    {0x00,0x41,0x41,0x7F,0x00}, // 0x5D ]
    {0x04,0x02,0x01,0x02,0x04}, // 0x5E ^
    {0x40,0x40,0x40,0x40,0x40}, // 0x5F _
    {0x00,0x01,0x02,0x04,0x00}, // 0x60 `
    {0x20,0x54,0x54,0x54,0x78}, // 0x61 a
    {0x7F,0x48,0x44,0x44,0x38}, // 0x62 b
    {0x38,0x44,0x44,0x44,0x20}, // 0x63 c
    {0x38,0x44,0x44,0x48,0x7F}, // 0x64 d
    {0x38,0x54,0x54,0x54,0x18}, // 0x65 e
    {0x08,0x7E,0x09,0x01,0x02}, // 0x66 f
    {0x0C,0x52,0x52,0x52,0x3E}, // 0x67 g
    {0x7F,0x08,0x04,0x04,0x78}, // 0x68 h
    {0x00,0x44,0x7D,0x40,0x00}, // 0x69 i
    {0x20,0x40,0x44,0x3D,0x00}, // 0x6A j
    {0x7F,0x10,0x28,0x44,0x00}, // 0x6B k
    {0x00,0x41,0x7F,0x40,0x00}, // 0x6C l
    {0x7C,0x04,0x18,0x04,0x78}, // 0x6D m
    {0x7C,0x08,0x04,0x04,0x78}, // 0x6E n
    {0x38,0x44,0x44,0x44,0x38}, // 0x6F o
    {0x7C,0x14,0x14,0x14,0x08}, // 0x70 p
    {0x08,0x14,0x14,0x18,0x7C}, // 0x71 q
    {0x7C,0x08,0x04,0x04,0x08}, // 0x72 r
    {0x48,0x54,0x54,0x54,0x20}, // 0x73 s
    {0x04,0x3F,0x44,0x40,0x20}, // 0x74 t
    {0x3C,0x40,0x40,0x40,0x7C}, // 0x75 u
    {0x1C,0x20,0x40,0x20,0x1C}, // 0x76 v
    {0x3C,0x40,0x30,0x40,0x3C}, // 0x77 w
    {0x44,0x28,0x10,0x28,0x44}, // 0x78 x
    {0x0C,0x50,0x50,0x50,0x3C}, // 0x79 y
    {0x44,0x64,0x54,0x4C,0x44}, // 0x7A z
};

static void draw_char(uint16_t x, uint16_t y, char c, uint16_t fg, uint16_t bg) {
    if (c < 0x20 || c > 0x7A) c = ' ';
    const uint8_t* glyph = FONT5x7[c - 0x20];
    
    // Set window for the entire 12x14 character cell (10px wide char + 2px gap)
    set_window(x, y, x + 11, y + 13);
    
    REG_WRITE(GPIO_OUT1_W1TC_REG, (1UL << (LCD_CS - 32))); // CS Low
    REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_RS));        // RS High
    __asm__ __volatile__("memw");
    
    const uint8_t fg_hi = fg >> 8;
    const uint8_t fg_lo = fg & 0xFF;
    const uint8_t bg_hi = bg >> 8;
    const uint8_t bg_lo = bg & 0xFF;

    for (int row = 0; row < 7; row++) {
        for (int r_dup = 0; r_dup < 2; r_dup++) {
            for (int col = 0; col < 5; col++) {
                uint8_t line = glyph[col];
                bool active = (line & (1 << row)) != 0;
                uint8_t hi = active ? fg_hi : bg_hi;
                uint8_t lo = active ? fg_lo : bg_lo;
                
                // Write each pixel twice for 2x horizontal scaling
                for (int c_dup = 0; c_dup < 2; c_dup++) {
                    write_data_bus(hi);
                    REG_WRITE(GPIO_OUT_W1TC_REG, (1UL << LCD_WR)); // WR Low
                    __asm__ __volatile__("memw");
                    esp_rom_delay_us(1);
                    REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_WR)); // WR High
                    __asm__ __volatile__("memw");
                    esp_rom_delay_us(1);
                    
                    write_data_bus(lo);
                    REG_WRITE(GPIO_OUT_W1TC_REG, (1UL << LCD_WR)); // WR Low
                    __asm__ __volatile__("memw");
                    esp_rom_delay_us(1);
                    REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_WR)); // WR High
                    __asm__ __volatile__("memw");
                    esp_rom_delay_us(1);
                }
            }
            // 2px gap (1px gap scaled 2x horizontally)
            for (int col = 0; col < 2; col++) {
                write_data_bus(bg_hi);
                REG_WRITE(GPIO_OUT_W1TC_REG, (1UL << LCD_WR)); // WR Low
                __asm__ __volatile__("memw");
                esp_rom_delay_us(1);
                REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_WR)); // WR High
                __asm__ __volatile__("memw");
                esp_rom_delay_us(1);
                
                write_data_bus(bg_lo);
                REG_WRITE(GPIO_OUT_W1TC_REG, (1UL << LCD_WR)); // WR Low
                __asm__ __volatile__("memw");
                esp_rom_delay_us(1);
                REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_WR)); // WR High
                __asm__ __volatile__("memw");
                esp_rom_delay_us(1);
            }
        }
    }
    REG_WRITE(GPIO_OUT1_W1TS_REG, (1UL << (LCD_CS - 32))); // CS High
    __asm__ __volatile__("memw");
}

static void draw_string(uint16_t x, uint16_t y, const char* str, uint16_t fg, uint16_t bg) {
    while (*str) {
        draw_char(x, y, *str++, fg, bg);
        x += 12;
        if (x > LCD_W - 12) break;
    }
}

static void draw_char_small(uint16_t x, uint16_t y, char c, uint16_t fg, uint16_t bg) {
    if (c < 0x20 || c > 0x7A) c = ' ';
    const uint8_t* glyph = FONT5x7[c - 0x20];
    
    // Set window for the entire 6x7 character cell (5px wide char + 1px gap)
    set_window(x, y, x + 5, y + 6);
    
    REG_WRITE(GPIO_OUT1_W1TC_REG, (1UL << (LCD_CS - 32))); // CS Low
    REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_RS));        // RS High
    __asm__ __volatile__("memw");
    
    const uint8_t fg_hi = fg >> 8;
    const uint8_t fg_lo = fg & 0xFF;
    const uint8_t bg_hi = bg >> 8;
    const uint8_t bg_lo = bg & 0xFF;

    for (int row = 0; row < 7; row++) {
        for (int col = 0; col < 5; col++) {
            uint8_t line = glyph[col];
            bool active = (line & (1 << row)) != 0;
            uint8_t hi = active ? fg_hi : bg_hi;
            uint8_t lo = active ? fg_lo : bg_lo;
            
            write_data_bus(hi);
            REG_WRITE(GPIO_OUT_W1TC_REG, (1UL << LCD_WR)); // WR Low
            __asm__ __volatile__("memw");
            esp_rom_delay_us(1);
            REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_WR)); // WR High
            __asm__ __volatile__("memw");
            esp_rom_delay_us(1);
            
            write_data_bus(lo);
            REG_WRITE(GPIO_OUT_W1TC_REG, (1UL << LCD_WR)); // WR Low
            __asm__ __volatile__("memw");
            esp_rom_delay_us(1);
            REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_WR)); // WR High
            __asm__ __volatile__("memw");
            esp_rom_delay_us(1);
        }
        // 1px gap
        write_data_bus(bg_hi);
        REG_WRITE(GPIO_OUT_W1TC_REG, (1UL << LCD_WR)); // WR Low
        __asm__ __volatile__("memw");
        esp_rom_delay_us(1);
        REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_WR)); // WR High
        __asm__ __volatile__("memw");
        esp_rom_delay_us(1);
        
        write_data_bus(bg_lo);
        REG_WRITE(GPIO_OUT_W1TC_REG, (1UL << LCD_WR)); // WR Low
        __asm__ __volatile__("memw");
        esp_rom_delay_us(1);
        REG_WRITE(GPIO_OUT_W1TS_REG, (1UL << LCD_WR)); // WR High
        __asm__ __volatile__("memw");
        esp_rom_delay_us(1);
    }
    REG_WRITE(GPIO_OUT1_W1TS_REG, (1UL << (LCD_CS - 32))); // CS High
    __asm__ __volatile__("memw");
}

static void draw_string_small(uint16_t x, uint16_t y, const char* str, uint16_t fg, uint16_t bg) {
    while (*str) {
        draw_char_small(x, y, *str++, fg, bg);
        x += 6;
        if (x > LCD_W - 6) break;
    }
}

static void draw_metro_tile(uint16_t x, uint16_t y, uint16_t w, uint16_t h, uint16_t bg, const char* label, const char* val, uint16_t val_color = COLOR_WHITE) {
    fill_rect(x, y, w, h, bg);
    if (label && label[0]) {
        uint16_t label_fg = (bg == COLOR_TILE_DARK || bg == COLOR_NAVY) ? COLOR_TEXT_GREY : COLOR_LGRAY;
        draw_string_small(x + 8, y + 8, label, label_fg, bg);
    }
    if (val && val[0]) {
        if (strlen(val) * 12 > (size_t)(w - 16)) {
            draw_string_small(x + 8, y + h - 16, val, val_color, bg);
        } else {
            draw_string(x + 8, y + h - 22, val, val_color, bg);
        }
    }
}

static void draw_signal_bars(uint16_t x, uint16_t y, RssiTier tier) {
    uint16_t bar_color[3] = {COLOR_TILE_DARK, COLOR_TILE_DARK, COLOR_TILE_DARK};
    if (tier == RssiTier::CLOSE)  { bar_color[0] = COLOR_SUCCESS;   bar_color[1] = COLOR_SUCCESS;    bar_color[2] = COLOR_SUCCESS;    }
    if (tier == RssiTier::MEDIUM) { bar_color[0] = COLOR_SUCCESS;   bar_color[1] = COLOR_AMBER;      bar_color[2] = COLOR_TILE_DARK;  }
    if (tier == RssiTier::FAR)    { bar_color[0] = COLOR_ERROR_RED; bar_color[1] = COLOR_TILE_DARK;  bar_color[2] = COLOR_TILE_DARK;  }
    fill_rect(x,      y + 6, 4, 4,  bar_color[0]); // short bar
    fill_rect(x + 6,  y + 4, 4, 6,  bar_color[1]); // medium bar
    fill_rect(x + 12, y,     4, 10, bar_color[2]); // tall bar
}

// ── Screen renderers ──────────────────────────────────────────
static void render_idle(const char* ssid) {
    fill_rect(0, 0, LCD_W, LCD_H, COLOR_NAVY);
    draw_string(10, 10, "SEADROP", COLOR_WHITE, COLOR_NAVY);
    draw_string_small(102, 16, "v1.5", COLOR_TEXT_GREY, COLOR_NAVY);
    // Primary tile — SSID in Space Orange
    draw_metro_tile(10, 40, 220, 90, COLOR_SPACE_ORANGE, "HOTSPOT SSID", ssid);
    // Secondary info tiles on dark surface
    draw_metro_tile(10, 140, 105, 80, COLOR_TILE_DARK, "PORT", "4242");
    draw_metro_tile(125, 140, 105, 80, COLOR_TILE_DARK, "MODE", "APSTA");
    // Status tile
    draw_metro_tile(10, 230, 220, 80, COLOR_TILE_DARK, "STATUS", "WAITING FOR CONNECTION...");
}

static void render_single_connected(bool is_windows, const char* name, RssiTier tier) {
    fill_rect(0, 0, LCD_W, LCD_H, COLOR_NAVY);
    draw_string(10, 10, "SEADROP", COLOR_WHITE, COLOR_NAVY);
    draw_string_small(102, 16, "v1.5", COLOR_TEXT_GREY, COLOR_NAVY);
    // Windows = Space Orange, Android = Success green — both use white text
    uint16_t bg = is_windows ? COLOR_SPACE_ORANGE : COLOR_SUCCESS;
    draw_metro_tile(10, 40, 220, 100, bg, is_windows ? "WINDOWS LAPTOP" : "ANDROID PHONE", name);
    draw_signal_bars(190, 50, tier);
    draw_metro_tile(10, 150, 105, 80, COLOR_TILE_DARK, "HOTSPOT", g_ap_ssid);
    draw_metro_tile(125, 150, 105, 80, COLOR_TILE_DARK, is_windows ? "ANDROID" : "WINDOWS", "WAITING...");
    draw_metro_tile(10, 240, 220, 70, COLOR_TILE_DARK, "STATUS",
                    is_windows ? "WAITING FOR PHONE..." : "WAITING FOR LAPTOP...");
}

static void render_both_connected(const char* win_name, const char* and_name,
                                   RssiTier win_tier, RssiTier and_tier) {
    fill_rect(0, 0, LCD_W, LCD_H, COLOR_NAVY);
    draw_string(10, 10, "SEADROP", COLOR_WHITE, COLOR_NAVY);
    // Windows = Space Orange half, Android = Success green half
    draw_metro_tile(10, 40, 105, 120, COLOR_SPACE_ORANGE, "WINDOWS", win_name);
    draw_signal_bars(85, 50, win_tier);
    draw_metro_tile(125, 40, 105, 120, COLOR_SUCCESS, "ANDROID", and_name);
    draw_signal_bars(200, 50, and_tier);
    // Hotspot info below
    draw_metro_tile(10, 170, 220, 75, COLOR_SPACE_ORANGE, "HOTSPOT ACTIVE", g_ap_ssid);
    draw_metro_tile(10, 255, 220, 55, COLOR_SUCCESS, "SYSTEM", "READY TO TRANSFER");
}

static void render_transferring(const char* fname, int percent, bool first_render, bool pct_changed) {
    char trunc[17];
    memset(trunc, 0, sizeof(trunc));
    size_t fnl = std::min<size_t>(strlen(fname), sizeof(trunc) - 1);
    memcpy(trunc, fname, fnl);

    if (first_render) {
        fill_rect(0, 0, LCD_W, LCD_H, COLOR_NAVY);
        draw_string(10, 10, "TRANSFERRING", COLOR_SPACE_ORANGE, COLOR_NAVY);
        // File tile - Space Orange
        draw_metro_tile(10, 40, 220, 80, COLOR_SPACE_ORANGE, "FILE", trunc);
        // Progress area - dark tile
        fill_rect(10, 130, 220, 120, COLOR_TILE_DARK);
        draw_string_small(18, 138, "PROGRESS", COLOR_TEXT_GREY, COLOR_TILE_DARK);
        char pct_str[16];
        snprintf(pct_str, sizeof(pct_str), "%d%%", percent);
        draw_string(88, 158, pct_str, COLOR_WHITE, COLOR_TILE_DARK);

        const uint16_t bx = 18, by = 202, bw = 184, bh = 14;
        fill_rect(bx, by, bw, bh, COLOR_NAVY);
        uint16_t fill_w = (uint16_t)((bw * percent) / 100);
        if (fill_w > 0) fill_rect(bx, by, fill_w, bh, COLOR_SPACE_ORANGE);

        draw_metro_tile(10, 258, 220, 52, COLOR_TILE_DARK, "SPEED", "ROUTING VIA HOTSPOT...");
    } else if (pct_changed) {
        fill_rect(88, 158, 96, 22, COLOR_TILE_DARK);
        char pct_str[16];
        snprintf(pct_str, sizeof(pct_str), "%d%%", percent);
        draw_string(88, 158, pct_str, COLOR_WHITE, COLOR_TILE_DARK);

        const uint16_t bx = 18, by = 202, bw = 184, bh = 14;
        fill_rect(bx, by, bw, bh, COLOR_NAVY);
        uint16_t fill_w = (uint16_t)((bw * percent) / 100);
        if (fill_w > 0) fill_rect(bx, by, fill_w, bh, COLOR_SPACE_ORANGE);
    }
}

static void render_complete(const char* fname) {
    fill_rect(0, 0, LCD_W, LCD_H, COLOR_NAVY);
    draw_string(10, 10, "TRANSACTION", COLOR_SUCCESS, COLOR_NAVY);
    // Full-width success tile
    draw_metro_tile(10, 40, 220, 100, COLOR_SUCCESS, "STATUS", "TRANSFER COMPLETED");
    char trunc[17];
    memset(trunc, 0, sizeof(trunc));
    size_t fnl = std::min<size_t>(strlen(fname), sizeof(trunc) - 1);
    memcpy(trunc, fname, fnl);
    draw_metro_tile(10, 150, 220, 90, COLOR_SPACE_ORANGE, "FILE", trunc);
    draw_metro_tile(10, 250, 220, 60, COLOR_TILE_DARK, "LOCATION", "SAVED TO TARGET DEVICE");
}

static void render_error() {
    fill_rect(0, 0, LCD_W, LCD_H, COLOR_NAVY);
    draw_string(10, 10, "TRANSACTION", COLOR_ERROR_RED, COLOR_NAVY);
    // Full-width error tile
    draw_metro_tile(10, 40, 220, 110, COLOR_ERROR_RED, "STATUS", "TRANSFER ABORTED");
    draw_metro_tile(10, 160, 220, 150, COLOR_TILE_DARK, "DIAGNOSTICS", "CHECK CONNECTIONS AND RETRY");
}

static void render_registration(bool and_done, bool win_done, uint32_t secs_remaining,
                                 bool and_changed, bool win_changed, bool sec_changed) {
    if (and_changed && win_changed && sec_changed) {
        fill_rect(0, 0, LCD_W, LCD_H, COLOR_NAVY);
        draw_string(10, 10, "REGISTRATION", COLOR_SPACE_ORANGE, COLOR_NAVY);
        draw_string_small(152, 16, "MODE", COLOR_SPACE_ORANGE, COLOR_NAVY);
        // Android / Windows status tiles — green when done, dark when pending
        draw_metro_tile(10, 40, 105, 100,
            and_done ? COLOR_SUCCESS : COLOR_TILE_DARK, "ANDROID", and_done ? "OK" : "PENDING");
        draw_metro_tile(125, 40, 105, 100,
            win_done ? COLOR_SUCCESS : COLOR_TILE_DARK, "WINDOWS", win_done ? "OK" : "PENDING");
        char timer_str[32];
        snprintf(timer_str, sizeof(timer_str), "%lu SECONDS", (unsigned long)secs_remaining);
        draw_metro_tile(10, 150, 220, 90, COLOR_SPACE_ORANGE, "TIME REMAINING", timer_str);
        draw_metro_tile(10, 250, 220, 60, COLOR_TILE_DARK, "INSTRUCTION", "START APP ON PHONE/PC");
    } else {
        if (and_changed) {
            draw_metro_tile(10, 40, 105, 100,
                and_done ? COLOR_SUCCESS : COLOR_TILE_DARK, "ANDROID", and_done ? "OK" : "PENDING");
        }
        if (win_changed) {
            draw_metro_tile(125, 40, 105, 100,
                win_done ? COLOR_SUCCESS : COLOR_TILE_DARK, "WINDOWS", win_done ? "OK" : "PENDING");
        }
        if (sec_changed) {
            char timer_str[32];
            snprintf(timer_str, sizeof(timer_str), "%lu SECONDS", (unsigned long)secs_remaining);
            draw_metro_tile(10, 150, 220, 90, COLOR_SPACE_ORANGE, "TIME REMAINING", timer_str);
        }
    }
}

static void render_confirm(const char* fname, bool android_to_windows, RssiTier sender_tier,
                            int countdown_s, bool first_render, bool countdown_changed) {
    // CLOSE tier: success-green border; MEDIUM: amber; FAR or no border: navy sentinel
    uint16_t border_color = COLOR_NAVY;
    if (sender_tier == RssiTier::CLOSE)  border_color = COLOR_SUCCESS;
    else if (sender_tier == RssiTier::MEDIUM) border_color = COLOR_AMBER;

    char trunc[17];
    memset(trunc, 0, sizeof(trunc));
    size_t fnl = std::min<size_t>(strlen(fname), sizeof(trunc) - 1);
    memcpy(trunc, fname, fnl);

    uint16_t action_bg = (sender_tier == RssiTier::CLOSE) ? COLOR_SUCCESS : COLOR_AMBER;
    char action_str[32];
    if (sender_tier == RssiTier::CLOSE && countdown_s > 0) {
        snprintf(action_str, sizeof(action_str), "CONFIRMED IN %ds", countdown_s);
    } else {
        snprintf(action_str, sizeof(action_str), "TAP SCREEN TO ACCEPT");
    }

    if (first_render) {
        fill_rect(0, 0, LCD_W, LCD_H, COLOR_NAVY);
        if (border_color != COLOR_NAVY) {
            fill_rect(0, 0, LCD_W, 3, border_color);
            fill_rect(0, LCD_H - 3, LCD_W, 3, border_color);
            fill_rect(0, 0, 3, LCD_H, border_color);
            fill_rect(LCD_W - 3, 0, 3, LCD_H, border_color);
        }
        draw_string(10, 10, "INCOMING FILE", COLOR_SPACE_ORANGE, COLOR_NAVY);
        draw_metro_tile(10, 40, 220, 95, COLOR_SPACE_ORANGE, "FILENAME", trunc);
        draw_metro_tile(10, 145, 220, 100, COLOR_TILE_DARK, "DIRECTION",
                        android_to_windows ? "PHONE -> LAPTOP" : "LAPTOP -> PHONE");
        draw_metro_tile(10, 255, 220, 55, action_bg, "ACTION", action_str);
    } else if (countdown_changed) {
        draw_metro_tile(10, 255, 220, 55, action_bg, "ACTION", action_str);
    }
}

// ── Public API ──────────────────────────────────────────
bool init() {
    // Reset all display pins to default GPIO mode, detaching JTAG and other functions
    gpio_reset_pin((gpio_num_t)LCD_RST);
    gpio_reset_pin((gpio_num_t)LCD_CS);
    gpio_reset_pin((gpio_num_t)LCD_RS);
    gpio_reset_pin((gpio_num_t)LCD_WR);
    gpio_reset_pin((gpio_num_t)LCD_RD);
    for (int i = 0; i < 8; i++) {
        gpio_reset_pin((gpio_num_t)LCD_DATA_PINS[i]);
    }

    // 1. Configure all display GPIO pins as outputs
    // LCD_RST(32), LCD_CS(33), LCD_RS(2), LCD_WR(4), LCD_RD(15) + D0-D7(12-14,26,25,21,22,27)
    gpio_config_t io = {};
    uint64_t mask = (1ULL << LCD_RST) | (1ULL << LCD_CS) | (1ULL << LCD_RS) | (1ULL << LCD_WR) | (1ULL << LCD_RD);
    for (int i = 0; i < 8; i++) mask |= (1ULL << LCD_DATA_PINS[i]);
    io.pin_bit_mask = mask;
    io.mode         = GPIO_MODE_OUTPUT;
    io.pull_up_en   = GPIO_PULLUP_DISABLE;
    io.pull_down_en = GPIO_PULLDOWN_DISABLE;
    io.intr_type    = GPIO_INTR_DISABLE;
    ESP_ERROR_CHECK(gpio_config(&io));

    // Hold control pins in inactive states (HIGH) initially
    gpio_set_level((gpio_num_t)LCD_CS, 1);
    gpio_set_level((gpio_num_t)LCD_WR, 1);
    gpio_set_level((gpio_num_t)LCD_RD, 1);
    gpio_set_level((gpio_num_t)LCD_RST, 1);
    vTaskDelay(pdMS_TO_TICKS(20)); // Settle lines

    // 2. Run ILI9341 init sequence
    ili9341_init();
    ESP_LOGI(TAG, "Display init done — 8-bit parallel");
    return true;  // ILI9341 present and responding
}

void task(void* /*arg*/) {
    ESP_LOGI(TAG, "display_task started");

    seadrop::DisplayState last_state = seadrop::DisplayState::IDLE;
    int last_pct  = -1;
    char last_ssid[32] = {};
    size_t ssl = std::min<size_t>(strlen(g_ap_ssid), sizeof(last_ssid) - 1);
    memcpy(last_ssid, g_ap_ssid, ssl);

    bool last_and_registered = false;
    bool last_win_registered = false;
    uint32_t last_reg_remaining_s = 99999;
    int last_countdown_s = -1;

    render_idle(g_ap_ssid);

    while (true) {
        xSemaphoreTake(g_state_mutex, portMAX_DELAY);
        seadrop::DisplayState state = g_display_state;
        int pct = (g_session.file_size > 0)
            ? (int)((uint64_t)g_session.bytes_written * 100 / g_session.file_size)
            : 0;
        char fname[MAX_FILENAME_LEN] = {};
        size_t fl = std::min<size_t>(strlen(g_session.filename), sizeof(fname) - 1);
        memcpy(fname, g_session.filename, fl);

        char wname[64] = {};
        size_t wl = std::min<size_t>(strlen(g_windows_device_name), sizeof(wname) - 1);
        memcpy(wname, g_windows_device_name, wl);

        char aname[64] = {};
        size_t al = std::min<size_t>(strlen(g_android_device_name), sizeof(aname) - 1);
        memcpy(aname, g_android_device_name, al);
        RssiTier win_tier = g_session.windows_tier;
        RssiTier and_tier = g_session.android_tier;

        // Registration state
        bool reg_active   = g_reg_state.active;
        bool reg_and_done = g_reg_state.android_registered;
        bool reg_win_done = g_reg_state.windows_registered;
        uint32_t reg_elapsed = xTaskGetTickCount() - g_reg_state.start_tick;
        uint32_t reg_remaining_s = 0;
        if (reg_active && g_reg_state.timeout_ms > (reg_elapsed * portTICK_PERIOD_MS)) {
            reg_remaining_s = (g_reg_state.timeout_ms - reg_elapsed * portTICK_PERIOD_MS) / 1000;
        }
        
        int countdown_s = 3;

        xSemaphoreGive(g_state_mutex);

        bool redraw = false;
        bool first_render = false;
        
        bool and_changed = false;
        bool win_changed = false;
        bool sec_changed = false;
        bool pct_changed = false;
        bool countdown_changed = false;

        if (state != last_state) {
            redraw = true;
            first_render = true;
            and_changed = true;
            win_changed = true;
            sec_changed = true;
            pct_changed = true;
            countdown_changed = true;
        } else {
            switch (state) {
                case seadrop::DisplayState::TRANSFERRING:
                    if (pct != last_pct) {
                        redraw = true;
                        pct_changed = true;
                    }
                    break;
                case seadrop::DisplayState::REGISTRATION:
                    if (reg_and_done != last_and_registered) { redraw = true; and_changed = true; }
                    if (reg_win_done != last_win_registered) { redraw = true; win_changed = true; }
                    if (reg_remaining_s != last_reg_remaining_s) { redraw = true; sec_changed = true; }
                    break;
                case seadrop::DisplayState::CONFIRM:
                    if (countdown_s != last_countdown_s) {
                        redraw = true;
                        countdown_changed = true;
                    }
                    break;
                default:
                    break;
            }
        }

        if (redraw) {
            switch (state) {
                case seadrop::DisplayState::IDLE:
                    render_idle(g_ap_ssid);
                    break;
                case seadrop::DisplayState::ANDROID_CONNECTED:
                    render_single_connected(false, aname, and_tier);
                    break;
                case seadrop::DisplayState::WINDOWS_CONNECTED:
                    render_single_connected(true, wname, win_tier);
                    break;
                case seadrop::DisplayState::BOTH_CONNECTED:
                    render_both_connected(wname, aname, win_tier, and_tier);
                    break;
                case seadrop::DisplayState::REGISTRATION:
                    render_registration(reg_and_done, reg_win_done, reg_remaining_s, and_changed, win_changed, sec_changed);
                    break;
                case seadrop::DisplayState::TRANSFERRING:
                    render_transferring(fname, pct, first_render, pct_changed);
                    break;
                case seadrop::DisplayState::CONFIRM:
                    render_confirm(fname, g_session.android_to_windows, 
                                   g_session.android_to_windows ? and_tier : win_tier,
                                   countdown_s, first_render, countdown_changed);
                    break;
                case seadrop::DisplayState::COMPLETE:
                    render_complete(fname);
                    break;
                case seadrop::DisplayState::ERROR:
                    render_error();
                    break;
            }
            last_state = state;
            last_pct   = pct;
            last_and_registered = reg_and_done;
            last_win_registered = reg_win_done;
            last_reg_remaining_s = reg_remaining_s;
            last_countdown_s = countdown_s;
        }

        vTaskDelay(pdMS_TO_TICKS(100));
    }
}

} // namespace seadrop::display
