#include "ncsi.hpp"
#include "protocol.hpp"

#include "esp_log.h"
#include "esp_http_server.h"
#include "lwip/sockets.h"
#include "lwip/netdb.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#include <cstring>
#include <cstdint>

static const char* TAG = "seadrop_ncsi";

// The AP gateway IP that all DNS queries resolve to.
// Android and Windows will probe connectivity through this IP.
// 192.168.4.1 in network byte order:
static const uint32_t AP_IP_BYTES = 0xC0A80401; // 192.168.4.1 big-endian

namespace seadrop::ncsi {

// ── HTTP NCSI handlers ────────────────────────────────────────

// Windows NCSI: GET www.msftconnecttest.com/connecttest.txt
// Must respond exactly "Microsoft Connect Test" with status 200.
static esp_err_t handle_connecttest(httpd_req_t* req) {
    httpd_resp_set_type(req, "text/plain");
    httpd_resp_sendstr(req, "Microsoft Connect Test");
    ESP_LOGD(TAG, "NCSI: /connecttest.txt → 200");
    return ESP_OK;
}

// Windows NCSI redirect probe: GET /redirect
static esp_err_t handle_redirect(httpd_req_t* req) {
    httpd_resp_set_status(req, "302 Found");
    httpd_resp_set_hdr(req, "Location", "http://192.168.4.1");
    httpd_resp_sendstr(req, "");
    ESP_LOGD(TAG, "NCSI: /redirect → 302");
    return ESP_OK;
}

// Windows NCSI alternate: GET /ncsi.txt
static esp_err_t handle_ncsi_txt(httpd_req_t* req) {
    httpd_resp_set_type(req, "text/plain");
    httpd_resp_sendstr(req, "Microsoft NCSI");
    return ESP_OK;
}

// Android NCSI: GET /generate_204  (connectivitycheck.gstatic.com)
// Also: GET /gen_204
// Must respond HTTP 204 No Content with empty body.
static esp_err_t handle_generate_204(httpd_req_t* req) {
    httpd_resp_set_status(req, "204 No Content");
    httpd_resp_set_type(req, "text/plain");
    httpd_resp_sendstr(req, "");
    ESP_LOGD(TAG, "NCSI: /generate_204 → 204");
    return ESP_OK;
}

// Apple captive portal detection (future iOS support)
static esp_err_t handle_hotspot_detect(httpd_req_t* req) {
    httpd_resp_set_type(req, "text/html");
    httpd_resp_sendstr(req,
        "<HTML><HEAD><TITLE>Success</TITLE></HEAD>"
        "<BODY>Success</BODY></HTML>");
    return ESP_OK;
}

// Catch-all for any other path — return 200 so no portal is triggered
static esp_err_t handle_catchall(httpd_req_t* req) {
    httpd_resp_set_status(req, "200 OK");
    httpd_resp_sendstr(req, "");
    return ESP_OK;
}

// ── DNS spoof task (UDP/53) ───────────────────────────────────
// Minimal DNS responder:
//   - Reads any DNS query on UDP port 53
//   - Copies the transaction ID and question section from the request
//   - Builds a response with a single A record → 192.168.4.1
//   - Sends the response back to the querier
//
// We bind to INADDR_ANY so it handles both Android (on SoftAP interface,
// 192.168.4.x) and any query arriving on the STA interface.
static void dns_task(void* /*arg*/) {
    int sock = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (sock < 0) {
        ESP_LOGE(TAG, "DNS socket create failed");
        vTaskDelete(nullptr);
        return;
    }

    int opt = 1;
    setsockopt(sock, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt));

    struct sockaddr_in addr = {};
    addr.sin_family      = AF_INET;
    addr.sin_port        = htons(53);
    addr.sin_addr.s_addr = htonl(INADDR_ANY);

    if (bind(sock, (struct sockaddr*)&addr, sizeof(addr)) < 0) {
        ESP_LOGE(TAG, "DNS bind failed — port 53 may require elevated privileges");
        close(sock);
        vTaskDelete(nullptr);
        return;
    }

    ESP_LOGI(TAG, "DNS spoof listening on UDP/53");

    uint8_t buf[512];
    while (true) {
        struct sockaddr_in client;
        socklen_t client_len = sizeof(client);

        int n = recvfrom(sock, buf, sizeof(buf) - 1, 0,
                         (struct sockaddr*)&client, &client_len);
        if (n < 12) continue;  // DNS header is 12 bytes minimum

        // Build minimal DNS response:
        //   Copy first 12 bytes of query (header), flip QR bit,
        //   set ANCOUNT=1, append question + answer sections.
        uint8_t resp[512];
        memcpy(resp, buf, n);  // start with query

        // Flags: QR=1 (response), AA=1 (authoritative), RCODE=0 (no error)
        resp[2] = 0x81;  // QR=1, Opcode=0, AA=1, TC=0, RD=1
        resp[3] = 0x80;  // RA=1, Z=0, RCODE=0

        // ANCOUNT = 1 (one answer)
        resp[6] = 0x00;
        resp[7] = 0x01;

        // NSCOUNT, ARCOUNT = 0
        resp[8] = resp[9] = resp[10] = resp[11] = 0x00;

        // Append answer record after the question section.
        // The question section ends at offset n (we copied the whole query).
        // Answer: pointer to question name (0xC00C), TYPE=A, CLASS=IN, TTL=60s, RDLEN=4
        int pos = n;
        resp[pos++] = 0xC0; resp[pos++] = 0x0C;  // name pointer → offset 12
        resp[pos++] = 0x00; resp[pos++] = 0x01;   // TYPE = A (host address)
        resp[pos++] = 0x00; resp[pos++] = 0x01;   // CLASS = IN
        resp[pos++] = 0x00; resp[pos++] = 0x00;   // TTL high
        resp[pos++] = 0x00; resp[pos++] = 0x3C;   // TTL low = 60 seconds
        resp[pos++] = 0x00; resp[pos++] = 0x04;   // RDLENGTH = 4
        resp[pos++] = 192; resp[pos++] = 168;      // 192.168.4.1
        resp[pos++] = 4;   resp[pos++] = 1;

        sendto(sock, resp, pos, 0, (struct sockaddr*)&client, client_len);
    }
}

// ── Public init ───────────────────────────────────────────────
void init() {
    // 1. Start DNS spoof task on Core 0
    xTaskCreatePinnedToCore(dns_task, "ncsi_dns", 4096, nullptr, 5, nullptr, 0);

    // 2. Start HTTP server on port 80
    httpd_config_t cfg = HTTPD_DEFAULT_CONFIG();
    cfg.server_port      = 80;
    cfg.max_open_sockets = 5;
    cfg.lru_purge_enable = true;
    cfg.uri_match_fn     = httpd_uri_match_wildcard;

    httpd_handle_t server = nullptr;
    if (httpd_start(&server, &cfg) != ESP_OK) {
        ESP_LOGE(TAG, "NCSI HTTP server failed to start");
        return;
    }

    // Windows NCSI probes
    httpd_uri_t u_connecttest = {};
    u_connecttest.uri     = "/connecttest.txt";
    u_connecttest.method  = HTTP_GET;
    u_connecttest.handler = handle_connecttest;
    httpd_register_uri_handler(server, &u_connecttest);

    httpd_uri_t u_redirect = {};
    u_redirect.uri     = "/redirect";
    u_redirect.method  = HTTP_GET;
    u_redirect.handler = handle_redirect;
    httpd_register_uri_handler(server, &u_redirect);

    httpd_uri_t u_ncsi = {};
    u_ncsi.uri     = "/ncsi.txt";
    u_ncsi.method  = HTTP_GET;
    u_ncsi.handler = handle_ncsi_txt;
    httpd_register_uri_handler(server, &u_ncsi);

    // Android NCSI probes
    httpd_uri_t u_204 = {};
    u_204.uri     = "/generate_204";
    u_204.method  = HTTP_GET;
    u_204.handler = handle_generate_204;
    httpd_register_uri_handler(server, &u_204);

    httpd_uri_t u_gen204 = {};
    u_gen204.uri     = "/gen_204";
    u_gen204.method  = HTTP_GET;
    u_gen204.handler = handle_generate_204;
    httpd_register_uri_handler(server, &u_gen204);

    // Apple captive portal
    httpd_uri_t u_apple = {};
    u_apple.uri     = "/hotspot-detect.html";
    u_apple.method  = HTTP_GET;
    u_apple.handler = handle_hotspot_detect;
    httpd_register_uri_handler(server, &u_apple);

    // Catch-all wildcard — must be registered last
    httpd_uri_t u_any = {};
    u_any.uri     = "/*";
    u_any.method  = HTTP_GET;
    u_any.handler = handle_catchall;
    httpd_register_uri_handler(server, &u_any);

    ESP_LOGI(TAG, "NCSI spoof active: DNS/53 + HTTP/80 "
                  "(/connecttest.txt /redirect /generate_204 /hotspot-detect.html)");
}

} // namespace seadrop::ncsi
