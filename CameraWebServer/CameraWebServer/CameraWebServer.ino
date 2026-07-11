#include <Arduino.h>
#include "esp_camera.h"
#include <WiFi.h>

// =====================================================
// Camera model và pin nằm trong board_config.h
// Trong board_config.h chỉ bật:
// #define CAMERA_MODEL_ESP32S3_EYE
// =====================================================
#include "board_config.h"

// =====================================================
// Wi-Fi
// ESP32 chỉ kết nối Wi-Fi 2.4 GHz.
// =====================================================

const char *WIFI_SSID = "Wifi";
const char *WIFI_PASSWORD = "niggawhatsmyname";

// Các hàm nằm trong app_httpd.cpp của CameraWebServer.
void startCameraServer();
void setupLedFlash();

// =====================================================
// Cấu hình
// =====================================================

static const uint32_t SERIAL_BAUD = 115200;
static const uint32_t WIFI_TIMEOUT_MS = 30000;

// Chạy QVGA trước để giảm tải và tăng FPS.
static const framesize_t STREAM_FRAME_SIZE = FRAMESIZE_QVGA;

// Không bật flash trong lần test đầu.
static const bool ENABLE_LED_FLASH = false;

// =====================================================
// UART log
// Dùng Serial0 để log luôn đi qua cổng TTL.
// =====================================================

#define LOG_PORT Serial0

// =====================================================
// Hỗ trợ
// =====================================================

void haltBoard(const char *message) {
  LOG_PORT.println();
  LOG_PORT.println("================================");
  LOG_PORT.println("FATAL ERROR");
  LOG_PORT.println(message);
  LOG_PORT.println("Board halted.");
  LOG_PORT.println("================================");

  while (true) {
    delay(1000);
  }
}

const char *wifiStatusToText(wl_status_t status) {
  switch (status) {
    case WL_IDLE_STATUS:
      return "WL_IDLE_STATUS";

    case WL_NO_SSID_AVAIL:
      return "WL_NO_SSID_AVAIL";

    case WL_SCAN_COMPLETED:
      return "WL_SCAN_COMPLETED";

    case WL_CONNECTED:
      return "WL_CONNECTED";

    case WL_CONNECT_FAILED:
      return "WL_CONNECT_FAILED";

    case WL_CONNECTION_LOST:
      return "WL_CONNECTION_LOST";

    case WL_DISCONNECTED:
      return "WL_DISCONNECTED";

    default:
      return "UNKNOWN_STATUS";
  }
}

// =====================================================
// Camera
// =====================================================

void setupCamera() {
  camera_config_t config = {};

  config.ledc_channel = LEDC_CHANNEL_0;
  config.ledc_timer = LEDC_TIMER_0;

  config.pin_d0 = Y2_GPIO_NUM;
  config.pin_d1 = Y3_GPIO_NUM;
  config.pin_d2 = Y4_GPIO_NUM;
  config.pin_d3 = Y5_GPIO_NUM;
  config.pin_d4 = Y6_GPIO_NUM;
  config.pin_d5 = Y7_GPIO_NUM;
  config.pin_d6 = Y8_GPIO_NUM;
  config.pin_d7 = Y9_GPIO_NUM;

  config.pin_xclk = XCLK_GPIO_NUM;
  config.pin_pclk = PCLK_GPIO_NUM;
  config.pin_vsync = VSYNC_GPIO_NUM;
  config.pin_href = HREF_GPIO_NUM;

  config.pin_sccb_sda = SIOD_GPIO_NUM;
  config.pin_sccb_scl = SIOC_GPIO_NUM;

  config.pin_pwdn = PWDN_GPIO_NUM;
  config.pin_reset = RESET_GPIO_NUM;

  config.xclk_freq_hz = 20000000;
  config.pixel_format = PIXFORMAT_JPEG;

  // Mặc định an toàn nếu không có PSRAM.
  config.frame_size = FRAMESIZE_QVGA;
  config.jpeg_quality = 16;
  config.fb_count = 1;
  config.grab_mode = CAMERA_GRAB_WHEN_EMPTY;
  config.fb_location = CAMERA_FB_IN_DRAM;

  LOG_PORT.print("PSRAM detected: ");
  LOG_PORT.println(psramFound() ? "YES" : "NO");

  if (psramFound()) {
    /*
     * N16R8 thường có 8 MB PSRAM.
     * VGA được dùng lúc cấp framebuffer,
     * sau init sẽ hạ xuống QVGA.
     */
    config.frame_size = FRAMESIZE_VGA;
    config.jpeg_quality = 12;
    config.fb_count = 2;
    config.grab_mode = CAMERA_GRAB_LATEST;
    config.fb_location = CAMERA_FB_IN_PSRAM;
  }

#if defined(CAMERA_MODEL_ESP_EYE)
  pinMode(13, INPUT_PULLUP);
  pinMode(14, INPUT_PULLUP);
#endif

  LOG_PORT.println("Initializing camera...");

  esp_err_t error = esp_camera_init(&config);

  if (error != ESP_OK) {
    LOG_PORT.printf(
      "Camera init failed with error 0x%x\n",
      error
    );

    LOG_PORT.println("Check:");
    LOG_PORT.println("- CAMERA_MODEL in board_config.h");
    LOG_PORT.println("- Camera ribbon cable");
    LOG_PORT.println("- Camera pin mapping");
    LOG_PORT.println("- PSRAM setting");
    LOG_PORT.println("- USB power/cable");

    haltBoard("Camera initialization failed.");
  }

  LOG_PORT.println("Camera initialized successfully.");

  sensor_t *sensor = esp_camera_sensor_get();

  if (sensor == nullptr) {
    haltBoard("Cannot get camera sensor.");
  }

  LOG_PORT.print("Camera sensor PID: 0x");
  LOG_PORT.println(sensor->id.PID, HEX);

  if (sensor->id.PID == OV3660_PID) {
    sensor->set_vflip(sensor, 1);
    sensor->set_brightness(sensor, 1);
    sensor->set_saturation(sensor, -2);
  }

#if defined(CAMERA_MODEL_ESP32S3_EYE)
  // Nếu ảnh bị ngược, giữ 1.
  // Nếu ảnh đang đúng chiều, đổi thành 0.
  sensor->set_vflip(sensor, 1);
#endif

#if defined(CAMERA_MODEL_M5STACK_WIDE) || \
    defined(CAMERA_MODEL_M5STACK_ESP32CAM)

  sensor->set_vflip(sensor, 1);
  sensor->set_hmirror(sensor, 1);

#endif

  // Hạ xuống QVGA để tăng FPS cho MediaPipe.
  sensor->set_framesize(
    sensor,
    STREAM_FRAME_SIZE
  );

  sensor->set_quality(sensor, 12);
  sensor->set_brightness(sensor, 0);
  sensor->set_contrast(sensor, 0);
  sensor->set_saturation(sensor, 0);
  sensor->set_hmirror(sensor, 0);

#if defined(LED_GPIO_NUM)
  if (ENABLE_LED_FLASH) {
    setupLedFlash();
    LOG_PORT.println("LED flash enabled.");
  } else {
    LOG_PORT.println("LED flash disabled.");
  }
#else
  LOG_PORT.println("No LED flash pin defined.");
#endif
}

// =====================================================
// Wi-Fi
// =====================================================

bool connectToWiFi() {
  WiFi.mode(WIFI_STA);
  WiFi.setSleep(false);

  LOG_PORT.println();
  LOG_PORT.print("Connecting to Wi-Fi: ");
  LOG_PORT.println(WIFI_SSID);

  WiFi.begin(
    WIFI_SSID,
    WIFI_PASSWORD
  );

  const unsigned long startedAt = millis();
  unsigned long lastStatusAt = 0;

  while (WiFi.status() != WL_CONNECTED) {
    delay(250);

    if (millis() - lastStatusAt >= 1000) {
      lastStatusAt = millis();

      wl_status_t status = WiFi.status();

      LOG_PORT.print(".");
      LOG_PORT.print(" status=");
      LOG_PORT.print((int)status);
      LOG_PORT.print(" ");
      LOG_PORT.println(
        wifiStatusToText(status)
      );
    }

    if (millis() - startedAt >= WIFI_TIMEOUT_MS) {
      LOG_PORT.println();
      LOG_PORT.println(
        "Wi-Fi connection timed out after 30 seconds."
      );

      wl_status_t status = WiFi.status();

      LOG_PORT.print("Final Wi-Fi status: ");
      LOG_PORT.print((int)status);
      LOG_PORT.print(" ");
      LOG_PORT.println(
        wifiStatusToText(status)
      );

      LOG_PORT.println();
      LOG_PORT.println("Possible causes:");
      LOG_PORT.println("- Wi-Fi is not 2.4 GHz");
      LOG_PORT.println("- Wrong SSID or password");
      LOG_PORT.println("- Hotspot client limit reached");
      LOG_PORT.println("- Weak signal");
      LOG_PORT.println("- Captive portal/network login required");

      WiFi.disconnect(true);
      return false;
    }
  }

  LOG_PORT.println();
  LOG_PORT.println("Wi-Fi connected.");

  LOG_PORT.print("IP address: ");
  LOG_PORT.println(WiFi.localIP());

  LOG_PORT.print("Gateway: ");
  LOG_PORT.println(WiFi.gatewayIP());

  LOG_PORT.print("RSSI: ");
  LOG_PORT.print(WiFi.RSSI());
  LOG_PORT.println(" dBm");

  return true;
}

// =====================================================
// Setup
// =====================================================

void setup() {
  /*
   * Serial0 = UART0 qua chip USB-to-UART ở cổng TTL.
   * Không dùng Serial để tránh log đi sang USB native.
   */
  LOG_PORT.begin(SERIAL_BAUD);
  LOG_PORT.setDebugOutput(true);

  delay(2000);

  LOG_PORT.println();
  LOG_PORT.println("================================");
  LOG_PORT.println("ESP32-S3-CAM STARTING");
  LOG_PORT.println("UART0 / TTL logging enabled");
  LOG_PORT.println("================================");

  setupCamera();

  if (!connectToWiFi()) {
    haltBoard("Wi-Fi connection failed.");
  }

  LOG_PORT.println("Starting camera web server...");

  startCameraServer();

  const String ip = WiFi.localIP().toString();

  LOG_PORT.println();
  LOG_PORT.println("================================");
  LOG_PORT.println("CAMERA SERVER READY");
  LOG_PORT.println("================================");

  LOG_PORT.printf(
    "Control page : http://%s\n",
    ip.c_str()
  );

  LOG_PORT.printf(
    "MJPEG stream: http://%s:81/stream\n",
    ip.c_str()
  );

  LOG_PORT.println();
  LOG_PORT.println("Use this command on laptop:");

  LOG_PORT.printf(
    "python app.py --camera "
    "\"http://%s:81/stream\" --language vi\n",
    ip.c_str()
  );

  LOG_PORT.println("================================");
}

// =====================================================
// Loop
// =====================================================

void loop() {
  static unsigned long lastStatusAt = 0;

  if (millis() - lastStatusAt >= 10000) {
    lastStatusAt = millis();

    wl_status_t status = WiFi.status();

    LOG_PORT.printf(
      "Status | WiFi=%s | RSSI=%d dBm | "
      "Heap=%u | PSRAM=%u\n",
      wifiStatusToText(status),
      WiFi.RSSI(),
      ESP.getFreeHeap(),
      ESP.getFreePsram()
    );

    if (status != WL_CONNECTED) {
      LOG_PORT.println(
        "Wi-Fi disconnected. Trying to reconnect..."
      );

      WiFi.disconnect();
      WiFi.begin(
        WIFI_SSID,
        WIFI_PASSWORD
      );
    }
  }

  delay(50);
}
