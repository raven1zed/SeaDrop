#pragma once
#include <cstdint>

namespace seadrop::display {

// Initialize GPIO pins and send ILI9341 init sequence.
bool init();   // Returns true if ILI9341 is present and initialised

// FreeRTOS task — polls g_display_state every 100ms and redraws.
void task(void* arg);

} // namespace seadrop::display
