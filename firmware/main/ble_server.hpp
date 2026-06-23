#pragma once

namespace seadrop::ble {

// Start NimBLE host task
void task(void* arg);

// Trigger advertisement update with current connection state
void update_advertisement();

} // namespace seadrop::ble
