"""WhisperX sidecar: ASR + word-level forced alignment + optional pyannote diarization.

POST /transcribe {"audio_path": "...wav", "language": "de"|null, "diarize": true}
→ WhisperX JSON {"language": "de", "segments": [{start,end,text,speaker,words:[{word,start,end,score,speaker}]}]}
POST /align {"audio_path": "...wav", "language": "de", "segments": [{start,end,text,speaker}]}
→ the same JSON with word times – aligns a transcript from another recogniser (e.g. OpenAI) to the audio.

Run:  uvicorn whisperx_server:app --host 127.0.0.1 --port 8011
Env:  AVAG_WHISPER_MODEL=large-v3 (default: large-v3 on GPUs with >= 8 GB, else medium)  HF_TOKEN=<token for pyannote diarization models>
      AVAG_WHISPER_DEVICE=cuda|cpu  AVAG_WHISPER_BATCH=<batch size>  AVAG_WHISPER_COMPUTE=float16|int8_float16|int8
Small GPUs (< 8 GB, e.g. 4 GB laptop GPUs that also drive the display) get int8 weights and a small batch so a
transcription cannot exhaust video memory. Requests are processed one at a time for the same reason.
The speech model is loaded on the first /transcribe, so a sidecar that only aligns never puts it on the GPU.
Pin whisperx to a tested version in requirements.txt – its API changes between releases.
"""
import gc
import os
import threading

import torch
import whisperx
from whisperx.diarize import DiarizationPipeline  # not re-exported from the package root in whisperx >= 3.4
from fastapi import FastAPI, HTTPException
from pydantic import BaseModel

app = FastAPI()
DEVICE = os.environ.get("AVAG_WHISPER_DEVICE") or ("cuda" if torch.cuda.is_available() else "cpu")
_VRAM_GB = torch.cuda.get_device_properties(0).total_memory / 2**30 if DEVICE == "cuda" else 0
_SMALL_GPU = DEVICE == "cuda" and _VRAM_GB < 8
MODEL_NAME = os.environ.get("AVAG_WHISPER_MODEL") or ("medium" if _SMALL_GPU else "large-v3")
COMPUTE = os.environ.get("AVAG_WHISPER_COMPUTE") or ("int8" if DEVICE == "cpu" else "int8_float16" if _SMALL_GPU else "float16")
BATCH = int(os.environ.get("AVAG_WHISPER_BATCH") or (4 if _SMALL_GPU else 16))

_asr = None
_align_cache: dict[str, tuple] = {}
_diarizer = None
_lock = threading.Lock()          # one request at a time: parallel requests would double GPU memory use


def _model():
    """The speech model, loaded on first use (inside _lock)."""
    global _asr
    if _asr is None:
        try:
            _asr = whisperx.load_model(MODEL_NAME, device=DEVICE, compute_type=COMPUTE)
        except Exception as e:
            raise HTTPException(500, f"model {MODEL_NAME} could not be loaded: {e}")
        print(f"whisperx: {MODEL_NAME} on {DEVICE} ({COMPUTE}, batch {BATCH})", flush=True)
    return _asr


def _aligner(lang: str):
    if lang not in _align_cache:
        _align_cache[lang] = whisperx.load_align_model(language_code=lang, device=DEVICE)
    return _align_cache[lang]


def _guarded(work):
    with _lock:
        try:
            return work()
        except torch.cuda.OutOfMemoryError:
            raise HTTPException(507, "GPU out of memory – set AVAG_WHISPER_BATCH=1 or AVAG_WHISPER_DEVICE=cpu")
        finally:
            gc.collect()
            if DEVICE == "cuda":
                torch.cuda.empty_cache()


class Req(BaseModel):
    audio_path: str
    language: str | None = None
    diarize: bool = True


class AlignReq(BaseModel):
    audio_path: str
    language: str
    segments: list[dict]


@app.post("/transcribe")
def transcribe(req: Req):
    return _guarded(lambda: _transcribe(req))


@app.post("/align")
def align(req: AlignReq):
    """Word times for a transcript made elsewhere. Speaker labels of the input segments are kept."""
    def work():
        audio = whisperx.load_audio(req.audio_path)
        segs = [{"start": float(s["start"]), "end": float(s["end"]), "text": s["text"]} for s in req.segments if s.get("text")]
        align_model, metadata = _aligner(req.language)
        aligned = whisperx.align(segs, align_model, metadata, audio, DEVICE, return_char_alignments=False)
        # whisperx may split segments: give every aligned piece the speaker of the input segment it overlaps most.
        for seg in aligned["segments"]:
            best = max(req.segments, key=lambda s: min(seg["end"], float(s["end"])) - max(seg["start"], float(s["start"])), default=None)
            seg["speaker"] = best.get("speaker") if best else None
            for w in seg.get("words", []):
                w.setdefault("speaker", seg["speaker"])
        return _normalise(aligned, req.language)
    return _guarded(work)


def _transcribe(req: Req):
    global _diarizer
    audio = whisperx.load_audio(req.audio_path)
    result = _model().transcribe(audio, batch_size=BATCH, language=req.language)
    lang = result["language"]

    align_model, metadata = _aligner(lang)
    aligned = whisperx.align(result["segments"], align_model, metadata, audio, DEVICE, return_char_alignments=False)

    if req.diarize and os.environ.get("HF_TOKEN"):  # pyannote models are gated; without a token skip diarization
        if _diarizer is None:
            _diarizer = DiarizationPipeline(token=os.environ.get("HF_TOKEN"), device=DEVICE)
        diar = _diarizer(audio)
        aligned = whisperx.assign_word_speakers(diar, aligned)

    return _normalise(aligned, lang)


def _normalise(aligned, lang):
    """Every word gets {word,start,end,score,speaker}; unaligned words keep start/end = null."""
    segments = []
    for seg in aligned["segments"]:
        words = [{"word": w.get("word", ""), "start": w.get("start"), "end": w.get("end"),
                  "score": w.get("score"), "speaker": w.get("speaker")} for w in seg.get("words", [])]
        segments.append({"start": seg["start"], "end": seg["end"], "text": seg["text"].strip(),
                         "speaker": seg.get("speaker"), "words": words})
    return {"language": lang, "segments": segments}
