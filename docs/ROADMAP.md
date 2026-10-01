# Implementation roadmap

Engineering estimates for an experienced ML/CV team (2 people). What already exists in this repo is marked ✅.

## Phase 1 — Proof of Concept (2–4 person-weeks)
Goal: video → FFmpeg → WhisperX → action references → click candidates → JSON. Zero-/few-shot, no fine-tuning, 30–50 annotated videos.
- ✅ FFmpeg ingest with real PTS, 16 kHz mono audio, coarse (2–4 fps) + fine (15–30 fps) decode
- ✅ WhisperX adapter (sidecar + JSON file), word timing, speaker labels
- ✅ German/English audio-reference parser (verb, deixis, explicit target, ordinals)
- ✅ Cursor tracker (template / motion), UI state-change detector, click candidates
- ✅ Deterministic cross-modal resolver, event graph schema 1.0, DE/EN describer, CLI
- ✅ End-to-end test on a synthetic recording
- ☐ Collect 30–50 real recordings, annotate with `samples/demo_groundtruth.json` format, run `avag eval`
- ☐ Record cursor templates for the OSes in your corpus

Hardware: 1× 24 GB GPU (48 GB preferred), 64–128 GB RAM, 16–32 threads, 1–2 TB NVMe, Linux + CUDA, Docker.

## Phase 2 — Robust UI grounding (+3–5 weeks)
- ✅ OmniParser + PaddleOCR sidecar; stable `ui_element_id`s by IoU + text similarity
- ✅ SAM2 sidecar for target-box stabilisation (`tracked`)
- ☐ Cursor-hidden handling: UI diff localisation → `inferred`/`unobservable` labelling at scale (policy exists, needs data)
- ☐ Scroll/drag detection (block-motion estimation between frames), text-entry detection (OCR diff in input fields)
- ☐ Benchmark: ScreenSpot for static GUI grounding; own dataset (500–2 000 videos) for audio→click→time
- ☐ Threshold calibration sweep (FusionConfig, detector thresholds) against AVAGA

## Phase 3 — Physical hand/touch interaction (+3–6 weeks, only if camera footage is in scope)
- ☐ MediaPipe Hands/Pose sidecar → fingertip landmarks
- ☐ Screen/device plane detection (Grounding DINO "tablet"/"monitor" + corner refinement) → homography
- ☐ Contact detection: approach + depth/extent minimum + release; UI change as corroboration
- ☐ Occlusion-aware target OCR (frames before contact / after release)

## Phase 4 — Domain fine-tuning + learned cross-modal matcher (+6–12 weeks)
- ☐ Tens of thousands of audio↔event pairs; train text/ASR embedding + time-coded frame features + target features → P(audio_ref ↔ visual_event)
- ☐ Public data: Molmo2 VideoPoint/VideoTrack, Charades-STA/ActivityNet, ScreenSpot/SeeClick/OS-Atlas, AVA-ActiveSpeaker
- ☐ Light-ASD for "which visible face is speaking" when multiple people are on camera

## Phase 5 — Production hardening (total ≈ 3–4 months to a first robust release)
- ☐ Job queue, N parallel videos, per-component latency/VRAM metrics (RTF, s/video-min, GPU-s/video-min, time-to-first-event)
- ☐ Long videos: InternVideo3 agent layer selecting windows; 30–120 s overlapping blocks
- ☐ Failure analysis dashboard by slice; regression suite from golden runs
- ☐ Privacy controls: TTL on caches, encryption at rest, role separation (video access vs. event metadata), DPIA if biometrics

## Key design principle
"Precise" coordinates must come from observable spatial evidence. The text generator may only claim what the
event graph grounds; `observed` / `tracked` / `inferred` / `unobservable` are carried into every output.
