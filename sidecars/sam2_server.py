"""SAM2 sidecar: propagate a target box/point through a time window → per-frame boxes.

POST /track {"video_path": "...mp4", "seed_time_s": 12.4, "point": [x,y]|null, "box": [x1,y1,x2,y2]|null, "start_s": 11.4, "end_s": 13.7}
→ {"samples": [{"time_s": 11.4, "bbox": [x1,y1,x2,y2], "confidence": 0.9}, ...]}

Run:  uvicorn sam2_server:app --host 127.0.0.1 --port 8004
Env:  SAM2_CFG=configs/sam2.1/sam2.1_hiera_s.yaml  SAM2_CKPT=checkpoints/sam2.1_hiera_small.pt  AVAG_TRACK_FPS=10
"""
import os
import shutil
import subprocess
import tempfile
import numpy as np
import torch
from fastapi import FastAPI
from pydantic import BaseModel
from sam2.build_sam import build_sam2_video_predictor

app = FastAPI()
predictor = build_sam2_video_predictor(os.environ.get("SAM2_CFG", "configs/sam2.1/sam2.1_hiera_s.yaml"),
                                       os.environ.get("SAM2_CKPT", "checkpoints/sam2.1_hiera_small.pt"))
FPS = float(os.environ.get("AVAG_TRACK_FPS", "10"))


class Req(BaseModel):
    video_path: str
    seed_time_s: float
    point: list[int] | None = None
    box: list[int] | None = None
    start_s: float
    end_s: float


@app.post("/track")
def track(req: Req):
    frames_dir = tempfile.mkdtemp()
    try:
        subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-ss", f"{req.start_s:.3f}", "-to", f"{req.end_s:.3f}", "-i", req.video_path,
                        "-vf", f"fps={FPS}", "-q:v", "2", f"{frames_dir}/%05d.jpg"], check=True)
        n = len(os.listdir(frames_dir))
        seed_idx = int(round((req.seed_time_s - req.start_s) * FPS))
        seed_idx = max(0, min(n - 1, seed_idx))
        with torch.inference_mode(), torch.autocast("cuda", dtype=torch.bfloat16):
            state = predictor.init_state(video_path=frames_dir)
            kwargs = {}
            if req.box:
                kwargs["box"] = np.array(req.box, dtype=np.float32)
            if req.point:
                kwargs["points"] = np.array([req.point], dtype=np.float32)
                kwargs["labels"] = np.array([1], dtype=np.int32)
            predictor.add_new_points_or_box(state, frame_idx=seed_idx, obj_id=1, **kwargs)
            samples = []
            for direction in (False, True):  # forward, then backward from the seed
                for fidx, _, masks in predictor.propagate_in_video(state, start_frame_idx=seed_idx, reverse=direction):
                    m = (masks[0] > 0).cpu().numpy().squeeze()
                    ys, xs = np.nonzero(m)
                    if len(xs) == 0:
                        continue
                    samples.append({"time_s": req.start_s + fidx / FPS,
                                    "bbox": [int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())],
                                    "confidence": float(min(0.95, 0.5 + 0.5 * m.mean() * 10))})
        samples.sort(key=lambda s: s["time_s"])
        return {"samples": samples}
    finally:
        shutil.rmtree(frames_dir, ignore_errors=True)
