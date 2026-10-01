"""MolmoPoint sidecar: video pointing with (object_id, time, x, y).

POST /point {"video_path": "...mp4", "prompt": "...", "start_s": 12.0, "end_s": 13.5}
→ {"points": [{"object_id": "...", "time_s": 12.7, "x": 1489, "y": 836, "confidence": 0.6, "label": "..."}]}

Run:  uvicorn molmo_server:app --host 127.0.0.1 --port 8002
Env:  AVAG_MOLMO_MODEL=allenai/Molmo2-VideoPoint-4B   (see MOLMO_POINT_README.md in allenai/molmo2)
The clip is cut with FFmpeg first so the model only sees the audio-reference window at full resolution.
"""
import os
import subprocess
import tempfile
import torch
from fastapi import FastAPI
from pydantic import BaseModel
from transformers import AutoModelForImageTextToText, AutoProcessor

app = FastAPI()
MODEL_ID = os.environ.get("AVAG_MOLMO_MODEL", "allenai/Molmo2-VideoPoint-4B")
processor = AutoProcessor.from_pretrained(MODEL_ID, trust_remote_code=True)
model = AutoModelForImageTextToText.from_pretrained(MODEL_ID, trust_remote_code=True, torch_dtype=torch.bfloat16).to("cuda").eval()


class Req(BaseModel):
    video_path: str
    prompt: str
    start_s: float
    end_s: float


def _cut(video_path: str, start: float, end: float) -> str:
    out = tempfile.NamedTemporaryFile(suffix=".mp4", delete=False).name
    subprocess.run(["ffmpeg", "-y", "-loglevel", "error", "-ss", f"{start:.3f}", "-to", f"{end:.3f}", "-i", video_path,
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-an", out], check=True)
    return out


@app.post("/point")
def point(req: Req):
    clip = _cut(req.video_path, req.start_s, req.end_s)
    try:
        messages = [{"role": "user", "content": [{"type": "text", "text": req.prompt}, {"type": "video", "video": clip}]}]
        inputs = processor.apply_chat_template(messages, tokenize=True, add_generation_prompt=True, return_tensors="pt",
                                               return_dict=True, padding=True, return_pointing_metadata=True)
        metadata = inputs.pop("metadata")
        inputs = {k: v.to("cuda") for k, v in inputs.items()}
        with torch.inference_mode(), torch.autocast("cuda", dtype=torch.bfloat16):
            output = model.generate(**inputs, logits_processor=model.build_logit_processor_from_inputs(inputs), max_new_tokens=200)
        generated = output[:, inputs["input_ids"].size(1):]
        text = processor.post_process_image_text_to_text(generated, skip_special_tokens=False, clean_up_tokenization_spaces=False)[0]
        pts = model.extract_video_points(text, metadata["token_pooling"], metadata["subpatch_mapping"],
                                         metadata["timestamps"], metadata["video_size"])
        # pts: iterable of (object_id, frame/time, x, y) in source pixel coordinates of the clip; shift time back to media time
        points = []
        for p in pts:
            obj_id, t, x, y = p[0], float(p[1]), float(p[2]), float(p[3])
            points.append({"object_id": str(obj_id), "time_s": req.start_s + t, "x": int(round(x)), "y": int(round(y)),
                           "confidence": 0.6, "label": req.prompt[:60]})
        return {"points": points, "raw": text}
    finally:
        os.unlink(clip)
