from __future__ import annotations

import argparse
import logging
import os
import signal
import json
import queue
import threading
import time
from collections import deque
from pathlib import Path
import tempfile

os.environ.setdefault(
    "OPENCV_FFMPEG_CAPTURE_OPTIONS",
    "fflags;nobuffer|flags;low_delay|analyzeduration;0|probesize;32"
)

import cv2
import numpy as np
import torch
import torch.nn as nn
from flask import Flask, jsonify, request, render_template_string
from faster_whisper import WhisperModel
import mediapipe as mp
from mediapipe.tasks import python
from mediapipe.tasks.python import vision

HAND_FEATURES = 63
RAW_FEATURES = 126

HAND_CONNECTIONS = [
    # Thumb
    (0, 1), (1, 2), (2, 3), (3, 4),
    # Index finger
    (0, 5), (5, 6), (6, 7), (7, 8),
    # Middle finger
    (9, 10), (10, 11), (11, 12),
    # Ring finger
    (13, 14), (14, 15), (15, 16),
    # Pinky
    (0, 17), (17, 18), (18, 19), (19, 20),
    # Knuckle and palm connections
    (5, 9), (9, 13), (13, 17), (2, 5)
]

HTML = """
<!doctype html>
<html lang="vi">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Communication Assistant</title>
<style>
body{font-family:Arial,sans-serif;max-width:800px;margin:auto;padding:18px;background:#111;color:#eee}
.card{background:#1d1d1d;padding:16px;border-radius:14px;margin-bottom:14px}
button{padding:12px 16px;border:0;border-radius:10px;margin:4px;font-size:16px}
textarea{width:100%;min-height:100px;border-radius:10px;padding:10px}
.item{padding:10px;border-bottom:1px solid #444}
small{color:#aaa}
</style>
</head>
<body>
<h1>Communication Assistant</h1>
<div class="card">
<h2>Ghi âm câu nói</h2>
<input id="audio" type="file" accept="audio/*" capture>
<button onclick="uploadAudio()">Chuyển thành chữ</button>
<p id="status"></p>
</div>
<div class="card">
<h2>Tóm tắt</h2>
<button onclick="loadSummary()">Tạo tóm tắt</button>
<textarea id="summary"></textarea>
</div>
<div class="card">
<h2>Lịch sử hội thoại</h2>
<div id="history"></div>
</div>
<script>
async function refresh(){
  const r=await fetch('/api/history');
  const data=await r.json();
  document.getElementById('history').innerHTML=data.map(x=>
    `<div class="item"><b>${x.source}</b>: ${x.text}<br><small>${x.time}</small></div>`
  ).join('');
}
async function uploadAudio(){
  const f=document.getElementById('audio').files[0];
  if(!f){alert('Chọn hoặc ghi âm trước');return;}
  const fd=new FormData(); fd.append('audio',f);
  document.getElementById('status').innerText='Đang xử lý...';
  const r=await fetch('/api/transcribe',{method:'POST',body:fd});
  const data=await r.json();
  document.getElementById('status').innerText=data.text || data.error;
  refresh();
}
async function loadSummary(){
  const r=await fetch('/api/summary');
  const data=await r.json();
  document.getElementById('summary').value=data.summary;
}
refresh(); setInterval(refresh,3000);
</script>
</body>
</html>
"""

class GestureBiGRU(nn.Module):
    def __init__(self,input_size,num_classes,hidden_size=128,num_layers=2,dropout=0.4,bidirectional=True):
        super().__init__()
        self.input_projection=nn.Sequential(
            nn.Linear(input_size,hidden_size),
            nn.LayerNorm(hidden_size),
            nn.GELU(),
            nn.Dropout(dropout),
        )
        self.gru=nn.GRU(
            hidden_size,hidden_size,num_layers=num_layers,
            batch_first=True,
            dropout=dropout if num_layers>1 else 0,
            bidirectional=bidirectional,
        )
        output_size=hidden_size*2 if bidirectional else hidden_size
        self.attention=nn.Sequential(
            nn.Linear(output_size,hidden_size),
            nn.Tanh(),
            nn.Linear(hidden_size,1),
        )
        self.classifier=nn.Sequential(
            nn.LayerNorm(output_size),
            nn.Dropout(dropout),
            nn.Linear(output_size,num_classes),
        )

    def forward(self,x):
        x=self.input_projection(x)
        seq,_=self.gru(x)
        weights=torch.softmax(self.attention(seq).squeeze(-1),dim=1).unsqueeze(-1)
        return self.classifier(torch.sum(seq*weights,dim=1))

class State:
    def __init__(self):
        self.history=[]
        self.lock=threading.Lock()

    def add(self,source,text,extra=None):
        item={
            "source":source,
            "text":text,
            "time":time.strftime("%H:%M:%S"),
        }
        if extra:
            item.update(extra)
        with self.lock:
            self.history.append(item)
            self.history=self.history[-100:]

    def get(self):
        with self.lock:
            return list(self.history)

state=State()
web=Flask(__name__)
whisper_model=None
whisper_lock=threading.Lock()
shutdown_event=threading.Event()

logging.basicConfig(
    level=os.getenv("LOG_LEVEL", "INFO").upper(),
    format="%(asctime)s | %(levelname)s | %(threadName)s | %(message)s",
)
logger=logging.getLogger("communication-assistant")

@web.get("/")
def home():
    return render_template_string(HTML)

@web.get("/api/history")
def history():
    return jsonify(state.get())

@web.post("/api/transcribe")
def transcribe():
    global whisper_model
    if "audio" not in request.files:
        return jsonify({"error":"Missing audio"}),400

    upload=request.files["audio"]
    temp=Path("temp_audio")
    temp.mkdir(exist_ok=True)
    path=temp/f"{time.time_ns()}_{upload.filename or 'audio.webm'}"
    upload.save(path)

    try:
        with whisper_lock:
            if whisper_model is None:
                whisper_model=WhisperModel(
                    os.getenv("WHISPER_MODEL", "small"),
                    device=os.getenv("WHISPER_DEVICE", "cpu"),
                    compute_type=os.getenv("WHISPER_COMPUTE_TYPE", "int8"),
                )

            segments,info=whisper_model.transcribe(
                str(path),
                vad_filter=True,
                beam_size=int(os.getenv("WHISPER_BEAM_SIZE", "3")),
            )
            text=" ".join(s.text.strip() for s in segments).strip()
    except Exception:
        logger.exception("Audio transcription failed")
        return jsonify({"error":"Không thể xử lý âm thanh."}),500
    finally:
        path.unlink(missing_ok=True)

    if not text:
        text="Không nhận được nội dung rõ ràng."

    state.add("speech",text,{"language":info.language})
    return jsonify({"text":text,"language":info.language})

@web.get("/api/summary")
def summary():
    items=state.get()
    texts=[x["text"] for x in items if x["text"]]

    if not texts:
        return jsonify({"summary":"Chưa có nội dung hội thoại."})

    # Lightweight extractive summary for hackathon:
    # keeps the latest distinct messages instead of hallucinating.
    distinct=[]
    for text in texts:
        if not distinct or text.lower()!=distinct[-1].lower():
            distinct.append(text)

    selected=distinct[-6:]
    result=" • ".join(selected)
    return jsonify({"summary":result})

def run_web(host,port):
    try:
        from waitress import serve
        logger.info("Web server listening on http://%s:%s", host, port)
        serve(web, host=host, port=port, threads=4)
    except ImportError:
        logger.warning("waitress is not installed; using Flask development server")
        web.run(
            host=host,
            port=port,
            debug=False,
            use_reloader=False,
            threaded=True,
        )

def normalize_hand(points):
    points=points.astype(np.float32).copy()
    points-=points[0]
    scale=np.max(np.linalg.norm(points,axis=1))
    if scale>1e-6:
        points/=scale
    return points

def frame_features(result, force_right_hand=True):
    output=np.zeros(RAW_FEATURES,dtype=np.float32)
    if not result.hand_landmarks:
        return output

    # If force_right_hand is enabled and only one hand is detected,
    # we treat it as the right hand (the dominant hand in WLASL dataset).
    if force_right_hand and len(result.hand_landmarks) == 1:
        points=np.array([[p.x,p.y,p.z] for p in result.hand_landmarks[0]],dtype=np.float32)
        values=normalize_hand(points).reshape(-1)
        output[HAND_FEATURES:2*HAND_FEATURES]=values
        return output

    used=set()
    for i,landmarks in enumerate(result.hand_landmarks):
        points=np.array([[p.x,p.y,p.z] for p in landmarks],dtype=np.float32)
        values=normalize_hand(points).reshape(-1)

        hand=None
        if result.handedness and i<len(result.handedness) and result.handedness[i]:
            hand=result.handedness[i][0].category_name.lower()

        start=0 if hand=="left" else HAND_FEATURES
        if hand not in {"left","right"}:
            start=0 if i==0 else HAND_FEATURES
        if start in used:
            start=HAND_FEATURES if start==0 else 0

        output[start:start+HAND_FEATURES]=values
        used.add(start)

    return output

def resample(sequence,target):
    sequence=np.asarray(sequence,dtype=np.float32)
    if len(sequence)==1:
        return np.repeat(sequence,target,axis=0)

    old=np.linspace(0,1,len(sequence))
    new=np.linspace(0,1,target)
    result=np.empty((target,sequence.shape[1]),dtype=np.float32)

    for j in range(sequence.shape[1]):
        result[:,j]=np.interp(new,old,sequence[:,j])

    return result

def fill_missing_frames(sequence):
    if not sequence:
        return sequence
    filled = [arr.copy() for arr in sequence]
    n = len(filled)
    
    # Forward fill
    last_valid = None
    for i in range(n):
        if np.any(filled[i] != 0):
            last_valid = filled[i]
        elif last_valid is not None:
            filled[i] = last_valid.copy()
            
    # Backward fill
    last_valid = None
    for i in range(n - 1, -1, -1):
        if np.any(filled[i] != 0):
            last_valid = filled[i]
        elif last_valid is not None:
            filled[i] = last_valid.copy()
            
    return filled

def prepare(sequence,length,mean,std):
    filled_sequence = fill_missing_frames(sequence)
    sequence=resample(filled_sequence,length)
    velocity=np.diff(sequence,axis=0,prepend=sequence[:1])
    sequence=np.concatenate([sequence,velocity],axis=1)
    return ((sequence-mean)/std).astype(np.float32)

def convert_wav_to_32bit_stereo(wav_path):
    import wave
    import struct
    import os

    if not os.path.exists(wav_path):
        return None

    try:
        with wave.open(wav_path, "rb") as w:
            nchannels = w.getnchannels()
            sampwidth = w.getsampwidth()
            framerate = w.getframerate()
            nframes = w.getnframes()
            frames = w.readframes(nframes)

        if not frames:
            return None

        # Unpack samples to signed 16-bit
        samples = []
        if sampwidth == 2:
            fmt = f"<{len(frames)//2}h"
            samples = list(struct.unpack(fmt, frames))
        elif sampwidth == 1:
            samples = [int(b - 128) * 256 for b in frames]
        else:
            return None

        # Convert stereo to mono
        if nchannels == 2:
            samples = [(samples[i] + samples[i+1]) // 2 for i in range(0, len(samples), 2)]

        # Resample to 16000Hz using linear interpolation
        target_rate = 16000
        if framerate != target_rate:
            duration = len(samples) / framerate
            num_target_samples = int(duration * target_rate)
            resampled = []
            for i in range(num_target_samples):
                pos = i * (len(samples) - 1) / (num_target_samples - 1) if num_target_samples > 1 else 0
                idx = int(pos)
                frac = pos - idx
                if idx + 1 < len(samples):
                    val = int((1 - frac) * samples[idx] + frac * samples[idx+1])
                else:
                    val = samples[idx]
                resampled.append(val)
            samples = resampled

        # Convert to 32-bit stereo PCM bytes
        # Multiply by a volume factor to prevent clipping on the MAX98357
        volume = 0.5
        out_bytes = bytearray()
        for s in samples:
            s_val = int(s * volume)
            s_32 = s_val << 16
            out_bytes.extend(struct.pack("<ii", s_32, s_32))

        return bytes(out_bytes)
    except Exception as e:
        logger.warning("Error converting WAV to 32-bit stereo PCM: %s", e)
        return None


def start_tts_worker(camera_source):
    items = queue.Queue()

    # Parse ESP32 IP from camera_source if it's a URL
    from urllib.parse import urlparse
    esp_ip = None
    if isinstance(camera_source, str) and camera_source.startswith("http"):
        try:
            parsed = urlparse(camera_source)
            esp_ip = parsed.hostname
        except Exception:
            pass

    def worker():
        logger.info("TTS worker starting")

        try:
            import pyttsx3
            import requests
            import os

            engine = pyttsx3.init(driverName="sapi5")
            engine.setProperty("rate", 155)
            engine.setProperty("volume", 1.0)

            voices = engine.getProperty("voices")

            logger.info(
                "TTS initialized with %d voices",
                len(voices),
            )

            for index, voice in enumerate(voices):
                logger.info(
                    "TTS voice %d: %s | %s",
                    index,
                    getattr(voice, "name", "unknown"),
                    getattr(voice, "id", "unknown"),
                )

        except Exception:
            logger.exception("TTS initialization failed")
            return

        while True:
            text = items.get()

            try:
                if text is None:
                    logger.info("TTS worker stopping")
                    return

                logger.info("TTS speaking: %s", text)

                # 1. Save to temp WAV file
                temp_wav = "temp_tts.wav"
                try:
                    if os.path.exists(temp_wav):
                        os.remove(temp_wav)
                except Exception:
                    pass

                engine.save_to_file(str(text), temp_wav)
                engine.runAndWait()

                # 2. Convert to I2S 32-bit stereo format
                pcm_data = convert_wav_to_32bit_stereo(temp_wav)

                # Clean up immediately
                try:
                    if os.path.exists(temp_wav):
                        os.remove(temp_wav)
                except Exception:
                    pass

                # 3. Try playing on ESP32 I2S speaker
                played_on_esp = False
                if esp_ip and pcm_data:
                    play_url = f"http://{esp_ip}/play"
                    logger.info(
                        "Sending PCM data (%d bytes) to ESP32: %s",
                        len(pcm_data),
                        play_url,
                    )
                    try:
                        resp = requests.post(play_url, data=pcm_data, timeout=5.0)
                        if resp.status_code == 200:
                            logger.info("ESP32 speaker played audio successfully")
                            played_on_esp = True
                        else:
                            logger.warning(
                                "ESP32 speaker failed with code %d",
                                resp.status_code,
                            )
                    except Exception as e:
                        logger.warning("Failed to send audio to ESP32: %s", e)

                # 4. Fallback to local playback if not played on ESP32
                if not played_on_esp:
                    logger.info("Playing audio locally on laptop speaker")
                    engine.stop()
                    engine.say(str(text))
                    engine.runAndWait()

                logger.info("TTS finished: %s", text)

            except Exception:
                logger.exception(
                    "TTS failed while speaking: %s",
                    text,
                )

            finally:
                items.task_done()

    thread = threading.Thread(
        target=worker,
        name="tts-worker",
        daemon=True,
    )
    thread.start()

    return items


class MicrophoneRecorder:
    """Record mono audio from the system microphone in a background thread."""

    def __init__(self, sample_rate: int = 16000, device=None):
        self._sample_rate = sample_rate
        self._device = device
        self._frames: list[np.ndarray] = []
        self._stream = None
        self._recording = False

    @property
    def recording(self) -> bool:
        return self._recording

    def start(self):
        import sounddevice as sd

        self._frames = []
        self._recording = True
        self._stream = sd.InputStream(
            samplerate=self._sample_rate,
            channels=1,
            dtype="float32",
            device=self._device,
            callback=self._callback,
        )
        self._stream.start()

    def _callback(self, indata, frame_count, time_info, status):
        if status:
            logger.warning("Microphone status: %s", status)
        self._frames.append(indata.copy())

    def stop(self) -> Path | None:
        import soundfile as sf

        self._recording = False

        if self._stream is not None:
            self._stream.stop()
            self._stream.close()
            self._stream = None

        if not self._frames:
            return None

        audio = np.concatenate(self._frames, axis=0)
        self._frames = []

        if np.max(np.abs(audio)) < 1e-4:
            logger.warning("Microphone recorded silence")
            return None

        fd, temp_path = tempfile.mkstemp(suffix=".wav")
        os.close(fd)
        path = Path(temp_path)

        sf.write(str(path), audio, self._sample_rate)
        logger.info(
            "Saved recording: %s (%.1f seconds)",
            path.name,
            len(audio) / self._sample_rate,
        )
        return path


def transcribe_audio_file(
    audio_path: Path,
    language: str = "en",
) -> str:
    """Transcribe a WAV file using the already-loaded faster-whisper model."""
    global whisper_model, whisper_lock

    with whisper_lock:
        if whisper_model is None:
            whisper_model = WhisperModel(
                os.getenv("WHISPER_MODEL", "small"),
                device=os.getenv("WHISPER_DEVICE", "cpu"),
                compute_type=os.getenv("WHISPER_COMPUTE_TYPE", "int8"),
            )

    segments, info = whisper_model.transcribe(
        str(audio_path),
        language=language,
        vad_filter=True,
        beam_size=3,
    )
    text = " ".join(s.text.strip() for s in segments).strip()
    return text


class LatestFrameCamera:
    """Continuously drains the source and exposes only the newest decoded frame."""

    def __init__(
        self,
        source,
        reconnect_initial: float = 0.25,
        reconnect_max: float = 4.0,
    ):
        self.source = source
        self.reconnect_initial = reconnect_initial
        self.reconnect_max = reconnect_max

        self._capture = None
        self._frame = None
        self._frame_id = 0
        self._last_frame_at = 0.0
        self._lock = threading.Lock()
        self._connected = threading.Event()
        self._local_stop = threading.Event()

        self._thread = threading.Thread(
            target=self._capture_loop,
            name="camera-capture",
            daemon=True,
        )
        self._thread.start()

    def _open(self):
        backend = cv2.CAP_FFMPEG if isinstance(self.source, str) else cv2.CAP_ANY
        capture = cv2.VideoCapture(self.source, backend)
        capture.set(cv2.CAP_PROP_BUFFERSIZE, 1)

        if not capture.isOpened():
            capture.release()
            return None

        return capture

    def _capture_loop(self):
        delay = self.reconnect_initial

        while not self._local_stop.is_set() and not shutdown_event.is_set():
            if self._capture is None:
                logger.info("Connecting to camera: %s", self.source)
                self._capture = self._open()

                if self._capture is None:
                    self._connected.clear()
                    self._local_stop.wait(delay)
                    delay = min(delay * 2, self.reconnect_max)
                    continue

                delay = self.reconnect_initial
                self._connected.set()
                logger.info("Camera connected")

            ok, frame = self._capture.read()

            if not ok or frame is None:
                logger.warning("Camera read failed; reconnecting")
                self._connected.clear()
                self._capture.release()
                self._capture = None
                self._local_stop.wait(delay)
                delay = min(delay * 2, self.reconnect_max)
                continue

            with self._lock:
                self._frame = frame
                self._frame_id += 1
                self._last_frame_at = time.monotonic()

        if self._capture is not None:
            self._capture.release()
            self._capture = None

        self._connected.clear()

    def read_latest(self, last_frame_id: int = -1):
        with self._lock:
            if self._frame is None or self._frame_id == last_frame_id:
                return False, last_frame_id, None

            return True, self._frame_id, self._frame.copy()

    def wait_until_connected(self, timeout: float) -> bool:
        return self._connected.wait(timeout)

    @property
    def connected(self) -> bool:
        return self._connected.is_set()

    @property
    def frame_age_seconds(self) -> float:
        with self._lock:
            if self._last_frame_at == 0:
                return float("inf")
            return time.monotonic() - self._last_frame_at

    def release(self):
        self._local_stop.set()
        self._thread.join(timeout=3.0)


def run_camera(args):
    torch.set_num_threads(max(1, args.torch_threads))

    checkpoint=torch.load(
        args.checkpoint,
        map_location="cpu",
        weights_only=False,
    )
    config=checkpoint["config"]

    model=GestureBiGRU(
        checkpoint["input_size"],
        checkpoint["num_classes"],
        config["hidden_size"],
        config["gru_layers"],
        config["dropout"],
        config["bidirectional"],
    )
    model.load_state_dict(checkpoint["model_state_dict"])
    model.eval()

    classes=checkpoint["classes"]
    mean=np.asarray(checkpoint["feature_mean"],dtype=np.float32).reshape(-1)
    std=np.asarray(checkpoint["feature_std"],dtype=np.float32).reshape(-1)
    std=np.where(std<1e-6,1,std)
    threshold=float(checkpoint.get("confidence_threshold",0.65))
    length=int(config["sequence_length"])
    min_frames=int(config["min_detected_frames"])

    conversation=json.loads(
        Path(args.conversation).read_text(encoding="utf-8")
    )
    sentence_map=conversation["intent_sentences"]

    options=vision.HandLandmarkerOptions(
        base_options=python.BaseOptions(
            model_asset_path=str(Path(args.hand_model))
        ),
        running_mode=vision.RunningMode.VIDEO,
        num_hands=2,
        min_hand_detection_confidence=args.hand_detection_confidence,
        min_hand_presence_confidence=args.hand_presence_confidence,
        min_tracking_confidence=args.hand_tracking_confidence,
    )
    detector=vision.HandLandmarker.create_from_options(options)

    source=int(args.camera) if str(args.camera).isdigit() else args.camera
    camera=LatestFrameCamera(source)
    camera.wait_until_connected(args.camera_connect_timeout)

    tts = start_tts_worker(args.camera)
    time.sleep(1)
    tts.put("Text to speech is ready")

    # Microphone recorder for speech input
    mic_device = None
    if hasattr(args, "audio_device") and args.audio_device is not None:
        try:
            mic_device = int(args.audio_device)
        except ValueError:
            mic_device = args.audio_device

    mic = MicrophoneRecorder(
        sample_rate=getattr(args, "sample_rate", 16000),
        device=mic_device,
    )
    speech_recording = False
    speech_transcripts: deque[str] = deque(maxlen=3)
    transcript_display_until = 0.0

    session_active = False
    segment_active = False
    sequence = []
    detected = 0
    segment_started_at = None
    last_hand_at = None

    label = "READY"
    confidence = 0.0

    last_frame_id = -1
    last_result = None
    last_features = np.zeros(RAW_FEATURES, dtype=np.float32)
    last_mp_at = 0.0
    mp_interval = 1.0 / max(1.0, args.mediapipe_fps)

    fps_started = time.monotonic()
    fps_frames = 0
    display_fps = 0.0

    last_stale_logged_at = 0.0

    def reset_segment():
        nonlocal segment_active, sequence, detected, segment_started_at, last_hand_at
        segment_active = False
        sequence = []
        detected = 0
        segment_started_at = None
        last_hand_at = None

    def predict_segment():
        nonlocal label, confidence

        if detected < min_frames:
            label = "TRY AGAIN"
            logger.info(
                "Segment ignored: only %d/%d hand frames",
                detected,
                min_frames,
            )
            return

        x = prepare(sequence, length, mean, std)

        with torch.inference_mode():
            logits = model(torch.from_numpy(x).unsqueeze(0))
            probs = torch.softmax(logits, dim=1)[0]

        confidence = float(probs.max())
        pred = int(probs.argmax())
        intent = classes[pred]

        if confidence < threshold:
            label = "UNKNOWN"
            logger.info(
                "Prediction UNKNOWN; best=%s confidence=%.2f",
                intent,
                confidence,
            )
            return

        label = intent.upper()
        sentence = sentence_map.get(intent, {}).get(args.language, intent)

        logger.info(
            "Prediction %s confidence=%.2f sentence=%s",
            intent,
            confidence,
            sentence,
        )

        state.add(
            "sign",
            sentence,
            {
                "intent": intent,
                "confidence": confidence,
            },
        )
        tts.put(sentence)

    try:
        while not shutdown_event.is_set():
            is_new, last_frame_id, frame = camera.read_latest(last_frame_id)

            if not is_new:
                age = camera.frame_age_seconds
                if age != float("inf") and age > args.camera_stale_timeout:
                    now = time.monotonic()
                    if now - last_stale_logged_at >= 5.0:
                        logger.warning(
                            "No fresh camera frame for %.1f seconds",
                            age,
                        )
                        last_stale_logged_at = now
                time.sleep(0.002)
                continue

            now = time.monotonic()
            should_run_mp = (
                last_result is None
                or now - last_mp_at >= mp_interval
            )

            if should_run_mp:
                rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
                timestamp_ms = int(now * 1000)

                result = detector.detect_for_video(
                    mp.Image(
                        image_format=mp.ImageFormat.SRGB,
                        data=rgb,
                    ),
                    timestamp_ms,
                )

                last_result = result
                last_features = frame_features(
                    result,
                    force_right_hand=not args.no_force_right,
                )
                last_mp_at = now
            else:
                result = last_result

            hand_present = bool(
                result is not None
                and result.hand_landmarks
                and np.any(last_features != 0)
            )

            if session_active:
                if hand_present:
                    last_hand_at = now

                    if not segment_active:
                        segment_active = True
                        segment_started_at = now
                        sequence = []
                        detected = 0
                        label = "CAPTURING"
                        confidence = 0.0
                        logger.info("Gesture segment started")

                    sequence.append(last_features.copy())
                    detected += 1

                elif segment_active:
                    sequence.append(last_features.copy())

                    gap = (
                        now - last_hand_at
                        if last_hand_at is not None
                        else 0.0
                    )
                    duration = (
                        now - segment_started_at
                        if segment_started_at is not None
                        else 0.0
                    )

                    if (
                        gap >= args.segment_end_gap
                        or duration >= args.max_segment_seconds
                    ):
                        logger.info(
                            "Gesture segment ended: %d frames, %.2fs",
                            len(sequence),
                            duration,
                        )
                        predict_segment()
                        reset_segment()

            h, w = frame.shape[:2]

            if result is not None:
                for landmarks in result.hand_landmarks or []:
                    points = [
                        (int(p.x * w), int(p.y * h))
                        for p in landmarks
                    ]

                    for start_idx, end_idx in HAND_CONNECTIONS:
                        if start_idx < len(points) and end_idx < len(points):
                            cv2.line(
                                frame,
                                points[start_idx],
                                points[end_idx],
                                (0, 255, 0),
                                1,
                            )

                    for point in points:
                        cv2.circle(frame, point, 2, (255, 0, 255), -1)

            fps_frames += 1
            elapsed = now - fps_started

            if elapsed >= 1.0:
                display_fps = fps_frames / elapsed
                fps_frames = 0
                fps_started = now

            if speech_recording:
                status_text = "LISTENING"
                status_color = (0, 165, 255)
            elif segment_active:
                status_text = "CAPTURING"
                status_color = (0, 0, 255)
            elif session_active:
                status_text = "SESSION ON"
                status_color = (0, 255, 0)
            else:
                status_text = "READY"
                status_color = (0, 255, 0)

            cv2.putText(
                frame,
                status_text,
                (20, 35),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.8,
                status_color,
                2,
            )
            cv2.putText(
                frame,
                f"FPS {display_fps:.1f} | AI {args.mediapipe_fps:.1f}",
                (20, 65),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.55,
                (255, 255, 255),
                1,
            )
            cv2.putText(
                frame,
                f"{label} {confidence:.0%}",
                (20, h - 25),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.75,
                (0, 255, 255),
                2,
            )

            # Control hints at bottom-right
            hints = [
                "SPACE: Sign session",
                "S: Speech recording",
                "Q: Quit",
            ]
            for i, hint in enumerate(hints):
                cv2.putText(
                    frame,
                    hint,
                    (w - 220, h - 10 - (len(hints) - 1 - i) * 22),
                    cv2.FONT_HERSHEY_SIMPLEX,
                    0.45,
                    (180, 180, 180),
                    1,
                )

            # Show speech transcripts overlay
            if speech_transcripts and now < transcript_display_until:
                y_offset = 95
                for line in speech_transcripts:
                    display_line = line if len(line) <= 60 else line[:57] + "..."
                    cv2.putText(
                        frame,
                        display_line,
                        (20, y_offset),
                        cv2.FONT_HERSHEY_SIMPLEX,
                        0.5,
                        (255, 200, 0),
                        1,
                    )
                    y_offset += 22

            cv2.imshow("Communication Assistant", frame)
            key = cv2.waitKey(1) & 0xFF

            if key in (ord("q"), 27):
                shutdown_event.set()
                break

            if key == 32:
                session_active = not session_active

                if session_active:
                    reset_segment()
                    label = "SESSION ON"
                    confidence = 0.0
                    logger.info("Continuous recognition session started")
                else:
                    if segment_active and detected >= min_frames:
                        predict_segment()
                    reset_segment()
                    label = "READY"
                    confidence = 0.0
                    logger.info("Continuous recognition session stopped")

            if key == ord("s"):
                speech_recording = not speech_recording

                if speech_recording:
                    try:
                        mic.start()
                        label = "LISTENING"
                        confidence = 0.0
                        logger.info("Speech recording started")
                    except Exception:
                        logger.exception("Failed to start microphone")
                        speech_recording = False
                else:
                    logger.info("Speech recording stopped")
                    label = "TRANSCRIBING"
                    confidence = 0.0

                    try:
                        audio_path = mic.stop()

                        if audio_path:
                            logger.info("Transcribing audio")
                            speech_lang = getattr(
                                args, "speech_language", "en"
                            )
                            text = transcribe_audio_file(
                                audio_path,
                                language=speech_lang,
                            )

                            # Clean up temp file
                            try:
                                audio_path.unlink(missing_ok=True)
                            except Exception:
                                pass

                            if text:
                                logger.info("Transcript: %s", text)
                                state.add(
                                    "speech",
                                    text,
                                    {"language": speech_lang},
                                )
                                speech_transcripts.append(text)
                                transcript_display_until = (
                                    time.monotonic() + 8.0
                                )
                                label = "SPEECH OK"
                                tts.put(text)
                            else:
                                label = "NO SPEECH"
                        else:
                            label = "NO SPEECH"

                    except Exception:
                        logger.exception(
                            "Speech transcription failed"
                        )
                        label = "MIC ERROR"

    finally:
        shutdown_event.set()
        if mic.recording:
            mic.stop()
        camera.release()
        detector.close()
        tts.put(None)
        cv2.destroyAllWindows()

def install_signal_handlers():
    def stop_handler(signum, frame):
        logger.info("Received signal %s; shutting down", signum)
        shutdown_event.set()

    signal.signal(signal.SIGINT, stop_handler)

    if hasattr(signal, "SIGTERM"):
        signal.signal(signal.SIGTERM, stop_handler)


def main():
    parser = argparse.ArgumentParser(
        description="Low-latency sign-language communication assistant"
    )
    parser.add_argument("--checkpoint", default="best_bigru_v2.pt")
    parser.add_argument("--conversation", default="conversation_config.json")
    parser.add_argument("--hand-model", default="hand_landmarker.task")
    parser.add_argument("--camera", default="0")
    parser.add_argument("--language", choices=["vi", "en"], default="vi")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=8000)
    parser.add_argument("--web-only", action="store_true")
    parser.add_argument("--no-force-right", action="store_true")

    parser.add_argument("--mediapipe-fps", type=float, default=10.0)
    parser.add_argument("--torch-threads", type=int, default=2)
    parser.add_argument("--camera-connect-timeout", type=float, default=8.0)
    parser.add_argument("--camera-stale-timeout", type=float, default=3.0)
    parser.add_argument("--segment-end-gap", type=float, default=0.45)
    parser.add_argument("--max-segment-seconds", type=float, default=4.0)

    parser.add_argument("--audio-device", default=None,
                        help="Microphone device index or name")
    parser.add_argument("--sample-rate", type=int, default=16000,
                        help="Microphone sample rate in Hz")
    parser.add_argument("--speech-language", default="en",
                        help="Language for speech transcription")

    parser.add_argument(
        "--hand-detection-confidence",
        type=float,
        default=0.30,
    )
    parser.add_argument(
        "--hand-presence-confidence",
        type=float,
        default=0.30,
    )
    parser.add_argument(
        "--hand-tracking-confidence",
        type=float,
        default=0.30,
    )

    args = parser.parse_args()
    install_signal_handlers()

    web_thread = threading.Thread(
        target=run_web,
        args=(args.host, args.port),
        name="web-server",
        daemon=True,
    )
    web_thread.start()

    logger.info(
        "Phone notes URL: http://YOUR_LAPTOP_IP:%d",
        args.port,
    )

    try:
        if args.web_only:
            while not shutdown_event.wait(1.0):
                pass
        else:
            run_camera(args)
    except KeyboardInterrupt:
        shutdown_event.set()
    except Exception:
        logger.exception("Fatal application error")
        shutdown_event.set()
        raise


if __name__ == "__main__":
    main()