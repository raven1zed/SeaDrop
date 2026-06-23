#pragma once

namespace seadrop::ncsi {

// Start the NCSI spoof subsystem:
//   1. DNS UDP/53 task — responds to ALL queries with AP IP (192.168.4.1)
//   2. HTTP server on port 80 — serves Windows + Android NCSI probe URLs
//
// MUST be called before esp_wifi_start() — Windows caches negative BSSID
// results and will not retest the network if the spoof is not live on first join.
void init();

} // namespace seadrop::ncsi
