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

def start_tts_worker():
    items = queue.Queue()

    def worker():
        logger.info("TTS worker starting")

        try:
            import pyttsx3

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

    tts = start_tts_worker()
    time.sleep(1)
    tts.put("Text to speech is ready")

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

            status_text = (
                "CAPTURING"
                if segment_active
                else "SESSION ON"
                if session_active
                else "READY"
            )

            cv2.putText(
                frame,
                status_text,
                (20, 35),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.8,
                (0, 0, 255) if segment_active else (0, 255, 0),
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

    finally:
        shutdown_event.set()
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