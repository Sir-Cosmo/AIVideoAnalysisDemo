"""OmniParser + PaddleOCR sidecar: screenshot → UI elements with interactivity + German-capable OCR text.

POST /parse {"image_path": "...png"}
→ {"elements": [{"bbox": [x1,y1,x2,y2], "text": "Speichern"|null, "text_confidence": 0.98, "interactive_confidence": 0.9, "class": "button"}]}

Run:  uvicorn omniparser_server:app --host 127.0.0.1 --port 8003
Env:  OMNIPARSER_HOME=/opt/OmniParser  (weights under weights/icon_detect, weights/icon_caption_florence)
      AVAG_OCR_LANG=german
License note: check OmniParser detector weights (older Ultralytics weights are AGPL) before commercial use.
"""
import os
import sys
from fastapi import FastAPI
from pydantic import BaseModel
from PIL import Image
from paddleocr import PaddleOCR

OMNI = os.environ.get("OMNIPARSER_HOME", "/opt/OmniParser")
sys.path.insert(0, OMNI)
from util.utils import get_yolo_model, get_caption_model_processor, get_som_labeled_img, check_ocr_box  # noqa: E402

app = FastAPI()
yolo = get_yolo_model(model_path=f"{OMNI}/weights/icon_detect/model.pt")
captioner = get_caption_model_processor(model_name="florence2", model_name_or_path=f"{OMNI}/weights/icon_caption_florence")
ocr = PaddleOCR(use_angle_cls=True, lang=os.environ.get("AVAG_OCR_LANG", "german"), show_log=False)


class Req(BaseModel):
    image_path: str


@app.post("/parse")
def parse(req: Req):
    img = Image.open(req.image_path).convert("RGB")
    W, H = img.size
    elements = []

    # 1) PaddleOCR – authoritative for text (umlauts, small button labels)
    for line in (ocr.ocr(req.image_path, cls=True) or [[]])[0] or []:
        box, (text, conf) = line
        xs = [p[0] for p in box]; ys = [p[1] for p in box]
        elements.append({"bbox": [int(min(xs)), int(min(ys)), int(max(xs)), int(max(ys))], "text": text,
                         "text_confidence": float(conf), "interactive_confidence": 0.3, "class": "text"})

    # 2) OmniParser – interactive regions (icons/buttons), merged with OCR boxes by IoU
    _, _, parsed = get_som_labeled_img(req.image_path, yolo, BOX_TRESHOLD=0.05, output_coord_in_ratio=True,
                                       ocr_bbox=None, caption_model_processor=captioner, ocr_text=[], use_local_semantics=True)
    for el in parsed:
        x1, y1, x2, y2 = el["bbox"]
        bbox = [int(x1 * W), int(y1 * H), int(x2 * W), int(y2 * H)]
        inter = _max_iou(bbox, [e["bbox"] for e in elements])
        if inter[0] >= 0.5:
            e = elements[inter[1]]
            e["interactive_confidence"] = max(e["interactive_confidence"], 0.9 if el.get("interactivity") else 0.5)
            e["class"] = "button" if el.get("interactivity") else e["class"]
        else:
            elements.append({"bbox": bbox, "text": None if el.get("type") == "icon" else el.get("content"),
                             "text_confidence": 0.5, "interactive_confidence": 0.9 if el.get("interactivity") else 0.4,
                             "class": "icon" if el.get("type") == "icon" else "unknown"})
    return {"elements": elements}


def _max_iou(b, boxes):
    best, idx = 0.0, -1
    for i, o in enumerate(boxes):
        ix1, iy1, ix2, iy2 = max(b[0], o[0]), max(b[1], o[1]), min(b[2], o[2]), min(b[3], o[3])
        inter = max(0, ix2 - ix1) * max(0, iy2 - iy1)
        union = (b[2] - b[0]) * (b[3] - b[1]) + (o[2] - o[0]) * (o[3] - o[1]) - inter
        iou = inter / union if union else 0
        if iou > best:
            best, idx = iou, i
    return best, idx
