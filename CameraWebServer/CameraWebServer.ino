#include <Arduino.h>
#include "esp_camera.h"
#include <WiFi.h>

// =====================================================
// CAMERA MODEL
// Chỉ bật đúng MỘT model.
// =====================================================

// #define CAMERA_MODEL_WROVER_KIT
// #define CAMERA_MODEL_ESP_EYE
#define CAMERA_MODEL_ESP32S3_EYE
// #define CAMERA_MODEL_M5STACK_PSRAM
// #define CAMERA_MODEL_M5STACK_V2_PSRAM
// #define CAMERA_MODEL_M5STACK_WIDE
// #define CAMERA_MODEL_M5STACK_ESP32CAM
// #define CAMERA_MODEL_M5STACK_UNITCAM
// #define CAMERA_MODEL_AI_THINKER
// #define CAMERA_MODEL_TTGO_T_JOURNAL
// #define CAMERA_MODEL_XIAO_ESP32S3
// #define CAMERA_MODEL_ESP32_CAM_BOARD
// #define CAMERA_MODEL_ESP32S2_CAM_BOARD
// #define CAMERA_MODEL_ESP32S3_CAM_LCD
// #define CAMERA_MODEL_DFRobot_FireBeetle2_ESP32S3
// #define CAMERA_MODEL_DFRobot_Romeo_ESP32S3

#include "camera_pins.h"

// =====================================================
// WI-FI
// Không commit mật khẩu thật lên GitHub.
// =====================================================

const char* ssid = "AABW3";
const char* password = "AGENTIC2026";

// Các hàm này nằm trong ví dụ CameraWebServer của ESP32.
void startCameraServer();
void setupLedFlash(int pin);

// =====================================================
// CẤU HÌNH
// =====================================================

static const uint32_t SERIAL_BAUD = 115200;
static const uint32_t WIFI_TIMEOUT_MS = 30000;

// QVGA: 320x240 — nhanh và nhẹ.
// VGA: 640x480 — rõ hơn nhưng FPS thấp hơn.
static const framesize_t START_FRAME_SIZE = FRAMESIZE_QVGA;

// Không bật flash LED mặc định.
// Để true chỉ khi đã chắc LED_GPIO_NUM đúng.
static const bool ENABLE_FLASH_LED = false;

// =====================================================
// HÀM HỖ TRỢ
// =====================================================

void stopWithError(const char* message) {
  Serial.println();
  Serial.println("=================================");
  Serial.println("FATAL ERROR");
  Serial.println(message);
  Serial.println("Board halted.");
  Serial.println("=================================");

  while (true) {
    delay(1000);
  }
}

void printCameraErrorHelp(esp_err_t error) {
  Serial.printf("Camera init failed with error: 0x%x\n", error);
  Serial.println();
  Serial.println("Check these items:");
  Serial.println("1. Correct CAMERA_MODEL");
  Serial.println("2. Correct camera GPIO mapping");
  Serial.println("3. Camera ribbon cable orientation");
  Serial.println("4. Ribbon cable is fully inserted and locked");
  Serial.println("5. PSRAM setting in Arduino IDE");
  Serial.println("6. Stable USB cable and power");
  Serial.println("7. OV2640 module is not damaged");
}

bool connectWiFi() {
  WiFi.mode(WIFI_STA);
  WiFi.setSleep(false);

  Serial.print("Connecting to Wi-Fi: ");
  Serial.println(ssid);

  WiFi.begin(ssid, password);

  const unsigned long startedAt = millis();

  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    Serial.print(".");

    if (millis() - startedAt >= WIFI_TIMEOUT_MS) {
      Serial.println();
      Serial.println("Wi-Fi connection timed out.");

      WiFi.disconnect(true);
      return false;
    }
  }

  Serial.println();
  Serial.println("Wi-Fi connected.");

  Serial.print("IP address: ");
  Serial.println(WiFi.localIP());

  Serial.print("RSSI: ");
  Serial.print(WiFi.RSSI());
  Serial.println(" dBm");

  return true;
}

// =====================================================
// SETUP CAMERA
// =====================================================

void setupCamera() {
  camera_config_t config = {};

  config.ledc_channel = LEDC_CHANNEL_0;
  config.ledc_timer = LEDC_TIMER_0;

  // Camera data pins.
  config.pin_d0 = Y2_GPIO_NUM;
  config.pin_d1 = Y3_GPIO_NUM;
  config.pin_d2 = Y4_GPIO_NUM;
  config.pin_d3 = Y5_GPIO_NUM;
  config.pin_d4 = Y6_GPIO_NUM;
  config.pin_d5 = Y7_GPIO_NUM;
  config.pin_d6 = Y8_GPIO_NUM;
  config.pin_d7 = Y9_GPIO_NUM;

  // Camera control pins.
  config.pin_xclk = XCLK_GPIO_NUM;
  config.pin_pclk = PCLK_GPIO_NUM;
  config.pin_vsync = VSYNC_GPIO_NUM;
  config.pin_href = HREF_GPIO_NUM;

  // SCCB/I2C camera configuration pins.
  config.pin_sccb_sda = SIOD_GPIO_NUM;
  config.pin_sccb_scl = SIOC_GPIO_NUM;

  config.pin_pwdn = PWDN_GPIO_NUM;
  config.pin_reset = RESET_GPIO_NUM;

  // OV2640 thường hoạt động tốt ở 20 MHz.
  config.xclk_freq_hz = 20000000;

  // JPEG là lựa chọn phù hợp nhất để MJPEG streaming.
  config.pixel_format = PIXFORMAT_JPEG;

  // Thiết lập an toàn ban đầu.
  config.frame_size = START_FRAME_SIZE;
  config.jpeg_quality = 14;
  config.fb_count = 1;
  config.grab_mode = CAMERA_GRAB_WHEN_EMPTY;
  config.fb_location = CAMERA_FB_IN_DRAM;

  Serial.print("PSRAM detected: ");
  Serial.println(psramFound() ? "YES" : "NO");

  if (psramFound()) {
    /*
     * ESP32-S3 N16R8 thường có 8 MB PSRAM.
     * Hai framebuffer giúp stream ổn định hơn.
     */
    config.frame_size = FRAMESIZE_VGA;
    config.jpeg_quality = 12;
    config.fb_count = 2;
    config.grab_mode = CAMERA_GRAB_LATEST;
    config.fb_location = CAMERA_FB_IN_PSRAM;
  } else {
    /*
     * Không có PSRAM thì giữ QVGA để tránh thiếu RAM.
     */
    config.frame_size = FRAMESIZE_QVGA;
    config.jpeg_quality = 16;
    config.fb_count = 1;
    config.grab_mode = CAMERA_GRAB_WHEN_EMPTY;
    config.fb_location = CAMERA_FB_IN_DRAM;
  }

  Serial.println("Initializing camera...");

  esp_err_t error = esp_camera_init(&config);

  if (error != ESP_OK) {
    printCameraErrorHelp(error);
    stopWithError("Camera initialization failed.");
  }

  Serial.println("Camera initialized successfully.");

  sensor_t* sensor = esp_camera_sensor_get();

  if (sensor == nullptr) {
    stopWithError("Cannot obtain camera sensor.");
  }

  Serial.print("Camera PID: 0x");
  Serial.println(sensor->id.PID, HEX);

  // Thiết lập riêng cho OV3660.
  if (sensor->id.PID == OV3660_PID) {
    sensor->set_vflip(sensor, 1);
    sensor->set_brightness(sensor, 1);
    sensor->set_saturation(sensor, -2);
  }

#if defined(CAMERA_MODEL_ESP32S3_EYE)
  /*
   * Một số module ESP32-S3-EYE cần lật dọc.
   * Nếu ảnh của board bạn đúng chiều rồi, đổi 1 thành 0.
   */
  sensor->set_vflip(sensor, 1);
#endif

#if defined(CAMERA_MODEL_M5STACK_WIDE) || \
    defined(CAMERA_MODEL_M5STACK_ESP32CAM)

  sensor->set_vflip(sensor, 1);
  sensor->set_hmirror(sensor, 1);

#endif

  /*
   * Sau khi đã cấp framebuffer, hạ độ phân giải xuống QVGA.
   * Điều này tăng FPS cho MediaPipe và giảm độ trễ.
   *
   * Khi chạy ổn, bạn có thể đổi thành FRAMESIZE_VGA.
   */
  sensor->set_framesize(sensor, START_FRAME_SIZE);

  // Các chỉnh sửa nhẹ, an toàn.
  sensor->set_quality(sensor, 12);
  sensor->set_brightness(sensor, 0);
  sensor->set_contrast(sensor, 0);
  sensor->set_saturation(sensor, 0);

  // Không bật mirror mặc định.
  sensor->set_hmirror(sensor, 0);

#if defined(LED_GPIO_NUM)
  if (ENABLE_FLASH_LED) {
    setupLedFlash(LED_GPIO_NUM);
    Serial.println("Flash LED enabled.");
  } else {
    Serial.println("Flash LED disabled for safety.");
  }
#else
  Serial.println("No flash LED pin defined.");
#endif
}

// =====================================================
// SETUP
// =====================================================

void setup() {
  Serial.begin(SERIAL_BAUD);
  Serial.setDebugOutput(true);

  delay(1500);

  Serial.println();
  Serial.println("=================================");
  Serial.println("ESP32-S3-CAM MJPEG Server");
  Serial.println("=================================");

  setupCamera();

  if (!connectWiFi()) {
    stopWithError("Cannot connect to Wi-Fi.");
  }

  Serial.println("Starting camera web server...");

  startCameraServer();

  const String ip = WiFi.localIP().toString();

  Serial.println();
  Serial.println("=================================");
  Serial.println("Camera server is ready");
  Serial.println("=================================");

  Serial.printf(
    "Control page : http://%s\n",
    ip.c_str()
  );

  Serial.printf(
    "MJPEG stream: http://%s:81/stream\n",
    ip.c_str()
  );

  Serial.println();
  Serial.println("Recommended app.py command:");

  Serial.printf(
    "python app.py --camera "
    "\"http://%s:81/stream\" --language vi\n",
    ip.c_str()
  );

  Serial.println();
}

// =====================================================
// LOOP
// =====================================================

void loop() {
  static unsigned long lastStatusAt = 0;

  if (millis() - lastStatusAt >= 10000) {
    lastStatusAt = millis();

    if (WiFi.status() == WL_CONNECTED) {
      Serial.printf(
        "Status | RSSI: %d dBm | Free heap: %u | Free PSRAM: %u\n",
        WiFi.RSSI(),
        ESP.getFreeHeap(),
        ESP.getFreePsram()
      );
    } else {
      Serial.println("Wi-Fi disconnected. Reconnecting...");

      WiFi.disconnect();
      WiFi.begin(ssid, password);
    }
  }

  delay(50);
}
