
from __future__ import annotations

import argparse
import json
import queue
import threading
import time
from collections import deque
from pathlib import Path

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

    if whisper_model is None:
        whisper_model=WhisperModel(
            "small",
            device="cpu",
            compute_type="int8"
        )

    segments,info=whisper_model.transcribe(
        str(path),
        vad_filter=True,
        beam_size=3,
    )
    text=" ".join(s.text.strip() for s in segments).strip()
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
    web.run(host=host,port=port,debug=False,use_reloader=False)

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
    items=queue.Queue()

    def worker():
        try:
            import pyttsx3
            engine=pyttsx3.init()
            engine.setProperty("rate",155)
        except Exception:
            return

        while True:
            text=items.get()
            if text is None:
                break
            engine.say(text)
            engine.runAndWait()

    threading.Thread(target=worker,daemon=True).start()
    return items

def run_camera(args):
    checkpoint=torch.load(args.checkpoint,map_location="cpu",weights_only=False)
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

    conversation=json.loads(Path(args.conversation).read_text(encoding="utf-8"))
    sentence_map=conversation["intent_sentences"]

    hand_model=Path(args.hand_model)
    options=vision.HandLandmarkerOptions(
        base_options=python.BaseOptions(model_asset_path=str(hand_model)),
        running_mode=vision.RunningMode.VIDEO,
        num_hands=2,
        min_hand_detection_confidence=0.30,
        min_hand_presence_confidence=0.30,
        min_tracking_confidence=0.30,
    )
    detector=vision.HandLandmarker.create_from_options(options)

    source=int(args.camera) if str(args.camera).isdigit() else args.camera
    cap=cv2.VideoCapture(source)
    if not cap.isOpened():
        raise RuntimeError(f"Cannot open camera: {source}")

    tts=start_tts_worker()
    recording=False
    sequence=[]
    detected=0
    label="READY"
    confidence=0.0
    frame_timestamp_ms = 0

    while True:
        ok,frame=cap.read()
        if not ok:
            break

        rgb=cv2.cvtColor(frame,cv2.COLOR_BGR2RGB)
        frame_timestamp_ms += 33
        result=detector.detect_for_video(mp.Image(
            image_format=mp.ImageFormat.SRGB,
            data=rgb
        ), frame_timestamp_ms)
        features=frame_features(result, force_right_hand=not args.no_force_right)

        if recording:
            sequence.append(features)
            if np.any(features!=0):
                detected+=1

        # Draw hand skeleton using MediaPipe normalized coordinates.
        h,w=frame.shape[:2]
        for landmarks in result.hand_landmarks or []:
            points=[(int(p.x*w),int(p.y*h)) for p in landmarks]
            for p in points:
                cv2.circle(frame,p,4,(255,0,255),-1)
            xs=[p[0] for p in points]
            ys=[p[1] for p in points]
            cv2.rectangle(
                frame,
                (max(0,min(xs)-15),max(0,min(ys)-15)),
                (min(w-1,max(xs)+15),min(h-1,max(ys)+15)),
                (0,255,255),2
            )

        cv2.putText(frame,"REC" if recording else "READY",(20,40),
                    cv2.FONT_HERSHEY_SIMPLEX,1,(0,0,255) if recording else (0,255,0),2)
        cv2.putText(frame,f"{label} {confidence:.0%}",(20,h-30),
                    cv2.FONT_HERSHEY_SIMPLEX,1,(0,255,255),2)
        cv2.imshow("Communication Assistant",frame)

        key=cv2.waitKey(1)&0xFF
        if key in (ord("q"),27):
            break
        if key==32:
            if not recording:
                recording=True
                sequence=[]
                detected=0
                label="RECORDING"
                confidence=0
                print("Recording started...")
            else:
                recording=False
                print(f"Recording stopped: {len(sequence)} frames, {detected} hand frames.")

                if detected<min_frames:
                    label="TRY AGAIN"
                    print(f"Not enough hand frames detected (need at least {min_frames}).")
                    continue

                x=prepare(sequence,length,mean,std)
                with torch.inference_mode():
                    probs=torch.softmax(
                        model(torch.from_numpy(x).unsqueeze(0)),
                        dim=1
                    )[0]
                confidence=float(probs.max())
                pred=int(probs.argmax())
                intent=classes[pred]

                if confidence<threshold:
                    label="UNKNOWN"
                    print(f"Prediction: UNKNOWN (best: {intent} with {confidence:.2%})")
                else:
                    label=intent.upper()
                    sentence=sentence_map.get(intent,{}).get(args.language,intent)
                    print(f"Prediction: {intent} ({confidence:.2%}) -> Sentence: {sentence}")
                    state.add("sign",sentence,{
                        "intent":intent,
                        "confidence":confidence
                    })
                    tts.put(sentence)

    cap.release()
    detector.close()
    cv2.destroyAllWindows()

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--checkpoint",default="best_bigru_v2.pt")
    parser.add_argument("--conversation",default="conversation_config.json")
    parser.add_argument("--hand-model",default="hand_landmarker.task")
    parser.add_argument("--camera",default="0")
    parser.add_argument("--language",choices=["vi","en"],default="vi")
    parser.add_argument("--host",default="0.0.0.0")
    parser.add_argument("--port",type=int,default=8000)
    parser.add_argument("--web-only",action="store_true")
    parser.add_argument("--no-force-right",action="store_true")
    args=parser.parse_args()

    threading.Thread(
        target=run_web,
        args=(args.host,args.port),
        daemon=True
    ).start()

    print(f"Phone notes: http://YOUR_LAPTOP_IP:{args.port}")

    if args.web_only:
        while True:
            time.sleep(3600)
    else:
        run_camera(args)

if __name__=="__main__":
    main()
