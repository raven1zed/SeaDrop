#pragma once
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "protocol.hpp"

// ============================================================
// Global shared state — declared in main.cpp
// All accesses from tasks outside main.cpp must hold g_state_mutex
// ============================================================
extern SemaphoreHandle_t        g_state_mutex;
extern seadrop::TransferSession g_session;
extern seadrop::DisplayState    g_display_state;
extern seadrop::RegistrationState g_reg_state;
extern bool                     g_windows_connected;
extern bool                     g_android_connected;
extern char                     g_windows_device_name[64];
extern char                     g_android_device_name[64];
extern char                     g_ap_ssid[32];
