#include <Arduino.h>
#include "esp_camera.h"
#include <WiFi.h>
#include <Wire.h>
#include <Adafruit_GFX.h>
#include <Adafruit_SSD1306.h>
#include <ESP_I2S.h>

#include "board_config.h"

const char *WIFI_SSID = "AABW 2";
const char *WIFI_PASSWORD = "AGENTIC2026";

void startCameraServer();
void setupLedFlash();

static constexpr uint32_t SERIAL_BAUD = 115200;
static constexpr uint32_t WIFI_TIMEOUT_MS = 30000;
static constexpr framesize_t STREAM_FRAME_SIZE = FRAMESIZE_QVGA;
static constexpr bool ENABLE_LED_FLASH = false;

#define LOG_PORT Serial0

// Shared I2S clock lines
static constexpr int PIN_I2S_BCLK = 47;
static constexpr int PIN_I2S_WS = 45;

// I2S amplifier and microphone
static constexpr int PIN_AMP_DOUT = 21;
static constexpr int PIN_MIC_DIN = 1;

// OLED
static constexpr int PIN_OLED_SDA = 41;
static constexpr int PIN_OLED_SCL = 42;

// Vibration motor driver signal
static constexpr int PIN_VIBRATION = 14;

static constexpr int OLED_WIDTH = 128;
static constexpr int OLED_HEIGHT = 64;

Adafruit_SSD1306 display(OLED_WIDTH, OLED_HEIGHT, &Wire, -1);
uint8_t oledAddress = 0;
bool oledReady = false;

static constexpr uint32_t AUDIO_SAMPLE_RATE = 16000;
static constexpr size_t MIC_BUFFER_SAMPLES = 128;

I2SClass audioI2S;
int32_t micBuffer[MIC_BUFFER_SAMPLES];

bool audioReady = false;
bool cameraReady = false;
bool serverReady = false;
uint32_t lastMicLevel = 0;
bool micStreaming = false;

unsigned long lastStatusAt = 0;
unsigned long lastOledAt = 0;
unsigned long lastMicLogAt = 0;

void haltBoard(const char *message) {
  LOG_PORT.println();
  LOG_PORT.println("================================");
  LOG_PORT.println("FATAL ERROR");
  LOG_PORT.println(message);
  LOG_PORT.println("Board halted.");
  LOG_PORT.println("================================");

  if (oledReady) {
    display.clearDisplay();
    display.setTextColor(SSD1306_WHITE);
    display.setTextSize(1);
    display.setCursor(0, 0);
    display.println("FATAL ERROR");
    display.setCursor(0, 18);
    display.println(message);
    display.display();
  }

  while (true) {
    digitalWrite(PIN_VIBRATION, LOW);
    delay(1000);
  }
}

const char *wifiStatusToText(wl_status_t status) {
  switch (status) {
    case WL_IDLE_STATUS: return "IDLE";
    case WL_NO_SSID_AVAIL: return "NO_SSID";
    case WL_SCAN_COMPLETED: return "SCAN_DONE";
    case WL_CONNECTED: return "CONNECTED";
    case WL_CONNECT_FAILED: return "AUTH_FAILED";
    case WL_CONNECTION_LOST: return "LOST";
    case WL_DISCONNECTED: return "DISCONNECTED";
    default: return "UNKNOWN";
  }
}

bool isI2CAddressPresent(uint8_t address) {
  Wire.beginTransmission(address);
  return Wire.endTransmission() == 0;
}

bool setupOLED() {
  if (!Wire.begin(PIN_OLED_SDA, PIN_OLED_SCL, 400000)) {
    LOG_PORT.println("[OLED] Wire.begin failed.");
    return false;
  }

  delay(50);

  if (isI2CAddressPresent(0x3C)) {
    oledAddress = 0x3C;
  } else if (isI2CAddressPresent(0x3D)) {
    oledAddress = 0x3D;
  } else {
    LOG_PORT.println("[OLED] No SSD1306 found at 0x3C or 0x3D.");
    return false;
  }

  if (!display.begin(SSD1306_SWITCHCAPVCC, oledAddress)) {
    LOG_PORT.println("[OLED] display.begin failed.");
    return false;
  }

  display.clearDisplay();
  display.setTextColor(SSD1306_WHITE);
  display.setTextSize(1);
  display.setCursor(0, 0);
  display.println("Flyntic starting...");
  display.display();

  LOG_PORT.printf("[OLED] Ready at 0x%02X.\n", oledAddress);
  return true;
}

void showOLED(const String &line1, const String &line2 = "", const String &line3 = "", const String &line4 = "") {
  if (!oledReady) return;

  display.clearDisplay();
  display.setTextColor(SSD1306_WHITE);
  display.setTextSize(1);
  display.setCursor(0, 0);
  display.println(line1);
  display.setCursor(0, 16);
  display.println(line2);
  display.setCursor(0, 32);
  display.println(line3);
  display.setCursor(0, 48);
  display.println(line4);
  display.display();
}

void updateStatusOLED() {
  if (!oledReady) return;

  String ipText = WiFi.status() == WL_CONNECTED ? WiFi.localIP().toString() : "No WiFi";

  display.clearDisplay();
  display.setTextColor(SSD1306_WHITE);
  display.setTextSize(1);
  display.setCursor(0, 0);
  display.println("Flyntic Camera");
  display.setCursor(0, 14);
  display.print("IP: ");
  display.println(ipText);
  display.setCursor(0, 28);
  display.print("Mic: ");
  display.println(lastMicLevel);
  display.setCursor(0, 42);
  display.print("RSSI: ");
  if (WiFi.status() == WL_CONNECTED) {
    display.print(WiFi.RSSI());
    display.println(" dBm");
  } else {
    display.println("--");
  }
  display.setCursor(0, 56);
  display.print("Cam:");
  display.print(cameraReady ? "OK" : "NO");
  display.print(" Aud:");
  display.print(audioReady ? "OK" : "NO");
  display.display();
}

void setupVibration() {
  pinMode(PIN_VIBRATION, OUTPUT);
  digitalWrite(PIN_VIBRATION, LOW);
  LOG_PORT.println("[VIBRATION] Output ready.");
}

void vibrate(uint32_t durationMs) {
  digitalWrite(PIN_VIBRATION, HIGH);
  delay(durationMs);
  digitalWrite(PIN_VIBRATION, LOW);
}

bool setupAudio() {
  audioI2S.setPins(PIN_I2S_BCLK, PIN_I2S_WS, PIN_AMP_DOUT, PIN_MIC_DIN);

  bool started = audioI2S.begin(
    I2S_MODE_STD,
    AUDIO_SAMPLE_RATE,
    I2S_DATA_BIT_WIDTH_32BIT,
    I2S_SLOT_MODE_STEREO
  );

  if (!started) {
    LOG_PORT.println("[I2S] Initialization failed.");
    return false;
  }

  LOG_PORT.println("[I2S] Ready.");
  LOG_PORT.printf("[I2S] BCLK=%d WS=%d DOUT=%d DIN=%d\n", PIN_I2S_BCLK, PIN_I2S_WS, PIN_AMP_DOUT, PIN_MIC_DIN);
  return true;
}

void playBeep(float frequencyHz, uint32_t durationMs, float volume = 0.10f) {
  if (!audioReady) return;

  volume = constrain(volume, 0.0f, 0.30f);
  const uint32_t totalFrames = (AUDIO_SAMPLE_RATE * durationMs) / 1000;
  static int32_t stereoBuffer[128 * 2];

  float phase = 0.0f;
  const float phaseStep = 2.0f * PI * frequencyHz / AUDIO_SAMPLE_RATE;
  uint32_t generated = 0;

  while (generated < totalFrames) {
    uint32_t remaining = totalFrames - generated;
    size_t frames = remaining > 128 ? 128 : remaining;

    for (size_t i = 0; i < frames; i++) {
      int32_t sample = static_cast<int32_t>(sinf(phase) * 2147483647.0f * volume);
      phase += phaseStep;
      if (phase >= 2.0f * PI) phase -= 2.0f * PI;

      stereoBuffer[i * 2] = sample;
      stereoBuffer[i * 2 + 1] = sample;
    }

    audioI2S.write(
      reinterpret_cast<uint8_t *>(stereoBuffer),
      frames * 2 * sizeof(int32_t)
    );

    generated += frames;
  }
}

uint32_t readMicrophoneLevel() {
  if (!audioReady) return 0;

  size_t bytesRead = audioI2S.readBytes(
    reinterpret_cast<char *>(micBuffer),
    sizeof(micBuffer)
  );

  size_t samplesRead = bytesRead / sizeof(int32_t);
  if (samplesRead == 0) return 0;

  uint64_t sum = 0;
  for (size_t i = 0; i < samplesRead; i++) {
    int64_t sample = static_cast<int64_t>(micBuffer[i] >> 8);
    if (sample < 0) sample = -sample;
    sum += static_cast<uint64_t>(sample);
  }

  return static_cast<uint32_t>(sum / samplesRead);
}

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

  config.frame_size = FRAMESIZE_QVGA;
  config.jpeg_quality = 16;
  config.fb_count = 1;
  config.grab_mode = CAMERA_GRAB_WHEN_EMPTY;
  config.fb_location = CAMERA_FB_IN_DRAM;

  LOG_PORT.print("[CAMERA] PSRAM: ");
  LOG_PORT.println(psramFound() ? "YES" : "NO");

  if (psramFound()) {
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

  LOG_PORT.println("[CAMERA] Initializing...");
  esp_err_t error = esp_camera_init(&config);

  if (error != ESP_OK) {
    LOG_PORT.printf("[CAMERA] Init failed: 0x%x\n", error);
    haltBoard("Camera init failed");
  }

  sensor_t *sensor = esp_camera_sensor_get();
  if (sensor == nullptr) haltBoard("Camera sensor missing");

  LOG_PORT.print("[CAMERA] Sensor PID: 0x");
  LOG_PORT.println(sensor->id.PID, HEX);

  if (sensor->id.PID == OV3660_PID) {
    sensor->set_vflip(sensor, 1);
    sensor->set_brightness(sensor, 1);
    sensor->set_saturation(sensor, -2);
  }

#if defined(CAMERA_MODEL_ESP32S3_EYE)
  sensor->set_vflip(sensor, 1);
#endif

#if defined(CAMERA_MODEL_M5STACK_WIDE) || defined(CAMERA_MODEL_M5STACK_ESP32CAM)
  sensor->set_vflip(sensor, 1);
  sensor->set_hmirror(sensor, 1);
#endif

  sensor->set_framesize(sensor, STREAM_FRAME_SIZE);
  sensor->set_quality(sensor, 12);
  sensor->set_brightness(sensor, 0);
  sensor->set_contrast(sensor, 0);
  sensor->set_saturation(sensor, 0);
  sensor->set_hmirror(sensor, 0);

#if defined(LED_GPIO_NUM)
  if (ENABLE_LED_FLASH) {
    setupLedFlash();
    LOG_PORT.println("[CAMERA] Flash LED enabled.");
  } else {
    LOG_PORT.println("[CAMERA] Flash LED disabled.");
  }
#endif

  cameraReady = true;
  LOG_PORT.println("[CAMERA] Ready.");
}

bool connectToWiFi() {
  WiFi.mode(WIFI_STA);
  WiFi.setSleep(false);

  LOG_PORT.println();
  LOG_PORT.print("[WIFI] Connecting to: ");
  LOG_PORT.println(WIFI_SSID);

  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);

  const unsigned long startedAt = millis();
  unsigned long lastMessageAt = 0;

  while (WiFi.status() != WL_CONNECTED) {
    delay(250);

    if (millis() - lastMessageAt >= 1000) {
      lastMessageAt = millis();
      wl_status_t status = WiFi.status();

      LOG_PORT.printf("[WIFI] status=%d %s\n", static_cast<int>(status), wifiStatusToText(status));

      if (oledReady) {
        showOLED(
          "Connecting WiFi",
          WIFI_SSID,
          wifiStatusToText(status),
          String((millis() - startedAt) / 1000) + " sec"
        );
      }
    }

    if (millis() - startedAt >= WIFI_TIMEOUT_MS) {
      wl_status_t status = WiFi.status();
      LOG_PORT.printf("[WIFI] Timeout. status=%d %s\n", static_cast<int>(status), wifiStatusToText(status));
      WiFi.disconnect(true);
      return false;
    }
  }

  LOG_PORT.println("[WIFI] Connected.");
  LOG_PORT.print("[WIFI] IP: ");
  LOG_PORT.println(WiFi.localIP());
  LOG_PORT.print("[WIFI] Gateway: ");
  LOG_PORT.println(WiFi.gatewayIP());
  LOG_PORT.printf("[WIFI] RSSI: %d dBm\n", WiFi.RSSI());
  return true;
}

void setup() {
  LOG_PORT.begin(SERIAL_BAUD);
  LOG_PORT.setDebugOutput(true);
  delay(2000);

  LOG_PORT.println();
  LOG_PORT.println("================================");
  LOG_PORT.println("FLYNTIC ESP32-S3 STARTING");
  LOG_PORT.println("TTL UART0 LOGGING");
  LOG_PORT.println("================================");

  setupVibration();
  oledReady = setupOLED();

  if (oledReady) {
    showOLED("Flyntic", "Booting...", "Camera + Audio", "Please wait");
  }

  setupCamera();

  if (oledReady) {
    showOLED("Camera ready", "Starting audio", "", "");
  }

  audioReady = setupAudio();

  LOG_PORT.println("[TEST] Vibration...");
  vibrate(150);
  delay(100);
  vibrate(150);

  if (audioReady) {
    LOG_PORT.println("[TEST] Speaker beep...");
    playBeep(700.0f, 180, 0.08f);
    delay(100);
    playBeep(1000.0f, 180, 0.08f);
  }

  if (!connectToWiFi()) {
    if (oledReady) {
      showOLED("WiFi failed", WIFI_SSID, "Use 2.4 GHz", "Check password");
    }
    haltBoard("WiFi connection failed");
  }

  if (oledReady) {
    showOLED("WiFi connected", WiFi.localIP().toString(), "Starting server", "");
  }

  LOG_PORT.println("[SERVER] Starting CameraWebServer...");
  startCameraServer();
  serverReady = true;

  const String ip = WiFi.localIP().toString();

  LOG_PORT.println();
  LOG_PORT.println("================================");
  LOG_PORT.println("CAMERA SERVER READY");
  LOG_PORT.println("================================");
  LOG_PORT.printf("Control page : http://%s\n", ip.c_str());
  LOG_PORT.printf("MJPEG stream: http://%s:81/stream\n", ip.c_str());
  LOG_PORT.println("================================");

  if (oledReady) {
    showOLED("Camera online", ip, "Stream port: 81", "Flyntic ready");
  }

  delay(1000);
}

void loop() {
  const unsigned long now = millis();

  if (audioReady && !micStreaming) {
    lastMicLevel = readMicrophoneLevel();
  }

  if (now - lastMicLogAt >= 1000) {
    lastMicLogAt = now;

    LOG_PORT.printf(
      "[STATUS] WiFi=%s RSSI=%d Mic=%u Heap=%u PSRAM=%u\n",
      wifiStatusToText(WiFi.status()),
      WiFi.status() == WL_CONNECTED ? WiFi.RSSI() : 0,
      lastMicLevel,
      ESP.getFreeHeap(),
      ESP.getFreePsram()
    );
  }

  if (now - lastOledAt >= 500) {
    lastOledAt = now;
    updateStatusOLED();
  }

  if (now - lastStatusAt >= 10000) {
    lastStatusAt = now;

    if (WiFi.status() != WL_CONNECTED) {
      LOG_PORT.println("[WIFI] Disconnected; reconnecting...");
      WiFi.disconnect();
      WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
    }
  }

  delay(5);
}
