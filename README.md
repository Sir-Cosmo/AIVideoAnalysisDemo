# AI Video Analysis Demo (AvAg)

AvAg turns a **screen-recording tutorial** into two things:

1. **An auditable event timeline**: what was said, where on the screen it was clicked, and how sure the system is
   about each statement. It never claims a pixel coordinate that the video does not show.
2. **A step-by-step manual** (Word, web page/PDF, Markdown) that explains what you have to do to repeat what the
   presenter does. It has one screenshot per step, with the click marked where it was observed. You can also
   request a text-only version for private videos.

Everything runs **on your own machine**. The C# application does the deterministic work (video decoding, click
detection, fusion, output). Speech recognition and the manual writer are local AI models, reached over
`127.0.0.1` only.

```
12,71 s: Klick bei (1489, 836) auf „Speichern“ …; zugeordnet zur Audioäußerung „Klicken Sie hier“ bei 12,22–12,58 s.
```

---

## Contents

1. [Quick start (Windows)](#1-quick-start-windows)
2. [How it works: the analysis pipeline](#2-how-it-works-the-analysis-pipeline)
3. [How it works: the manual](#3-how-it-works-the-manual)
4. [Private videos and data handling](#4-private-videos-and-data-handling)
5. [Web app and HTTP API](#5-web-app-and-http-api)
6. [Command line](#6-command-line)
7. [Configuration reference](#7-configuration-reference)
8. [Processes, ports and hardware](#8-processes-ports-and-hardware)
9. [Project layout and tests](#9-project-layout-and-tests)
10. [Troubleshooting](#10-troubleshooting)
11. [Advanced: more model services, evaluation, calibration](#11-advanced-more-model-services-evaluation-calibration)
12. [Licensing](#12-licensing)

---

## 1. Quick start (Windows)

### 1.1 Prerequisites

| What | Why | Install |
|---|---|---|
| .NET 8 SDK (Visual Studio 2022 17.8+ or VS 2026) | builds and runs the C# projects | `winget install Microsoft.DotNet.SDK.8` |
| FFmpeg ≥ 6 (`ffmpeg`, `ffprobe` on `PATH`) | decodes video, extracts audio and screenshots | `winget install Gyan.FFmpeg` (restart VS / terminal afterwards) |
| Python 3.11 | runs the speech-recognition service (WhisperX) | python.org or `winget install Python.Python.3.11` |
| NVIDIA GPU with CUDA (optional) | speeds up speech recognition; without a GPU it runs on the CPU | current NVIDIA driver |
| Ollama (optional) | local language model that writes the manual text | `winget install Ollama.Ollama` |

### 1.2 Speech-recognition service (one time)

```powershell
cd sidecars
python -m venv .venv
.venv\Scripts\python -m pip install --upgrade pip
# PyTorch with CUDA first (skip the index URL for CPU-only), then WhisperX and the web server
.venv\Scripts\python -m pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu128
.venv\Scripts\python -m pip install whisperx fastapi "uvicorn[standard]"
```

Tested with `torch 2.8.0+cu128` and `whisperx 3.8.6`. You don't have to start the service yourself: the web app
starts it when it needs it (see [2.2](#22-speech-recognition-whisperx-sidecar)).

### 1.3 Manual writer (optional, one time)

```powershell
ollama pull qwen2.5:7b
ollama create avag-manual -f sidecars/manual-llm.Modelfile
```

`manual-llm.Modelfile` sets `num_gpu 0`, so the model runs **only on the CPU** and never competes with speech
recognition for a small laptop GPU. Without Ollama the manual is still created, but rule-based
(see [3.2](#32-rule-based-steps-always-available)).

### 1.4 Run

* **Visual Studio:** open `AvAg.sln`, set **AvAg.Web** as the startup project and press **F5**. The page
  http://localhost:5080 opens.
* **Terminal:** `dotnet run --project src/AvAg.Web`

Drop a video on the page, press **Analyse video**, then **Create manual**.

### 1.5 Check the installation

```powershell
dotnet build
dotnet run --project tests/AvAg.Tests     # 19 tests, incl. an end-to-end run on a synthetic screen recording
```

---

## 2. How it works: the analysis pipeline

```
video ─► FFmpeg probe (real PTS time base)
      ├─► 16 kHz mono WAV ─► WhisperX: speech → text + word timings (+ speakers) ─► AudioRefParser: "click here" …
      ├─► coarse gray frames (3 fps, 960 px) ─► StateChangeDetector + CursorTracker ─► click candidates
      ├─► fine gray frames (20 fps, native) around every spoken instruction ─► better click candidates
      ├─► (optional) PNG frames ─► OmniParser + OCR ─► UiElementRegistry (stable UI element ids, button texts)
      └─► (optional) MolmoPoint / Qwen3-VL pointing, SAM2 tracking
                                    ▼
          CrossModalResolver: binds spoken instructions ↔ visual events
                                    ▼
          EventGraph (JSON schema 1.0, evidence, grounding_status) ─► DE/EN timeline ─► manual
```

`src/AvAg.Pipeline/PipelineRunner.cs` runs these steps in order for one video.

### 2.1 Ingest (FFmpeg)

`FfmpegService` reads duration, size and frame rate with `ffprobe`. It extracts the audio as 16 kHz mono WAV
and decodes frames as grayscale bitmaps, taking the time stamps from FFmpeg's `showinfo` filter (`pts_time`) so
variable-frame-rate recordings keep correct times. Intermediate frames and the WAV live in a temporary work
folder that is **deleted after the run**.

### 2.2 Speech recognition (WhisperX sidecar)

`sidecars/whisperx_server.py` is a small FastAPI service (`POST /transcribe {audio_path, language, diarize}`)
that the C# side calls with the path of the WAV:

1. **Transcription:** faster-whisper. The model is chosen automatically: `large-v3` on GPUs with ≥ 8 GB or on the
   CPU, `medium` on smaller GPUs.
2. **Word alignment:** a wav2vec2 alignment model gives every word a start and end time.
3. **Speaker separation (optional):** pyannote. It only runs if `HF_TOKEN` is set; otherwise it is skipped.

Safety on small GPUs (< 8 GB):
* The model weights are loaded as `int8_float16` and processed in batches of 4.
* Only **one transcription runs at a time** (a lock), and GPU memory is released after each request.
* The model loads in the background *after* the service started, so an accidental second copy fails on the busy
  port before it can put a second model on the GPU.

**Auto-start:** `src/AvAg.Web/SidecarLauncher.cs` starts every configured service that names a `LocalModule`
(here `whisperx_server:app`) with `sidecars/.venv` when the web app
starts or when a job needs it, unless something already listens on the configured address. It never starts a
second copy. Output goes to `sidecars/whisperx_server.log`.

### 2.3 Spoken instructions (AudioRefParser)

`src/AvAg.Core/AudioRefParser.cs` scans the word-timed transcript (German and English) for action verbs
(click/klicken, double-click, right-click, drag, type, select, scroll …). It also records deictic words
("hier", "here", "this") and named targets ("auf Speichern", "„OK“"), and splits sentences such as
"zuerst …, dann …" into ordered sub-actions. Each hit is an `AudioRef` with an exact time anchor: the end of the
deictic word, or the end of the clause.

### 2.4 Visual events (Vision/)

* `StateChangeDetector` compares frames block by block and reports *where* and *when* the screen changed.
* `CursorTracker` finds the mouse pointer, either with a cursor template (`--cursor`, most accurate) or as a
  small moving blob.
* `ClickCandidateDetector` reports a click when the cursor **rests** and the screen **changes** at that spot
  shortly afterwards. The cursor's own movement is ignored.

A coarse pass at 3 fps covers the whole video. Around every spoken instruction, a fine pass at 20 fps and full
resolution re-checks the window from 1.5 s before to 2.5 s after the instruction.

### 2.5 Fusion and grounding status

`src/AvAg.Core/Fusion.cs` scores every pair (spoken instruction, visual event):
`S = 0.35·time + 0.20·meaning + 0.15·pointer + 0.15·UI element + 0.15·screen change`, and picks the best
compatible event in spoken order. The constants live in `FusionConfig`. Each event in the output gets a
`grounding_status`:

| Source of the coordinate | `grounding_status` |
|---|---|
| visible cursor, telemetry, or UI change at a resting cursor | `observed` |
| SAM2-propagated box | `tracked` |
| AI pointing only (MolmoPoint / Qwen3-VL) | `inferred` |
| no cursor, no telemetry, no localised change | `unobservable`: the position is **not** claimed |

`EventGraphBuilder` writes the JSON (schema 1.0, see [11.3](#113-output-format)). `Describer` writes the German and
English timeline, and only names a position where the evidence supports one.

---

## 3. How it works: the manual

`src/AvAg.Pipeline/Manuals/ManualService.cs` creates the manual from a finished analysis, in four stages.

### 3.1 Sentences

`ManualBuilder.Sentences` splits the transcript into sentences using the word timings. Long unpunctuated
run-ons are cut at 40 words, and only short fragments are glued back to the sentence before. Filler words
("äh", "um", leading "so/also/okay") are removed.

### 3.2 Rule-based steps (always available)

`ManualBuilder.Build` (in `src/AvAg.Core/Manuals/ManualBuilder.cs`) works without any AI model:

* A sentence becomes a **step** if the parser found a pointer action in it, or if it contains an instruction verb
  (open, go to, save, copy, press, select … / öffnen, speichern, kopieren, drücken, wählen …) and addresses the
  viewer ("you", "Sie", "just").
* Sentences that follow a step and are not instructions become that step's **details** (at most 3).
* The first sentence, if it isn't an instruction, becomes the **overview**.
* Sign-offs ("like and subscribe", "bis zum nächsten Mal" …) are dropped.

### 3.3 AI-written steps (when a language model is configured)

`LlmManualWriter` works with whichever language model `Services:TextGenerator` selects (default: `avag-manual` =
Qwen2.5 7B in Ollama, CPU only). The model receives the **whole transcript as numbered
sentences** (`[12] (00:45) Select this from rectangle to window …`). It is asked to:

* write the manual for the whole video, with exactly one viewer action per step, in video order;
* use short imperative sentences ("Klicken Sie …"), write key combinations as `Ctrl + C`, and keep program and
  button names as spoken;
* give each step a `section` when the video shows several ways (e.g. "Method 1: Keyboard shortcut");
* put things that are only mentioned into `tips`;
* list for every step the **numbers of the transcript sentences it comes from**.

Those sentence numbers, not times invented by the model, put each step in the video. The answer is checked:
it must have at least 2 steps, and at least half of them must point to real sentences. If not, or if the model
is not reachable, the rule-based manual from 3.2 is used and the web page says so. On a laptop CPU this takes
about 1–5 minutes, depending on the video's length.

### 3.4 Screenshots and click markers

For every step (`ManualBuilder.PlaceScreenshots`, `ManualScreenshotService.RefineTimesAsync`):

1. **Click marker:** if an observed click belongs to an instruction spoken *inside this step*, the screenshot is
   taken right at that click, with a red box around it (and around the UI element, if one was found). A click
   only counts if the instruction was a click or the click visibly changed the screen. Clicks from neighbouring
   steps are never used.
2. **Otherwise:** FFmpeg decodes small thumbnails (2 fps, 160 px wide) of the step's own part of the video. The
   chosen frame is **settled** (it barely changes in the next half second, so menus are fully open) and
   **differs most** from the previous step's screenshot. Frames near the spoken instruction win ties.
3. If every candidate looks like the previous screenshot, the step gets **no picture** instead of a repeat.
4. Screenshots are always in video order and at least 1.5 s apart. They are extracted at up to 1280 px wide.

### 3.5 Output

| Format | Details |
|---|---|
| Word `.docx` | built-in Title/Heading styles (navigation pane works), embedded screenshots, key combinations in bold. Written directly as Open XML, no extra library. |
| HTML | one self-contained file (screenshots embedded), light/dark, print → PDF, key combinations as `<kbd>` keys |
| Markdown | text only |

Each step shows its title, the instruction, the details, the screenshot and "Shown in the video at mm:ss".

---

## 4. Private videos and data handling

### 4.1 Private (text-only) manuals

Tick **Private video** when uploading, or **Private – text only** next to *Create manual* (CLI: `--private`).
The manual is then text only: **no frame is extracted from the video** and no click position is kept. A video
marked private at upload can never get screenshots later.

### 4.2 Where data goes

| Data | Where | How long |
|---|---|---|
| Uploaded video (and transcript/UI JSON if uploaded) | `%TEMP%\avag-web\<job id>\` | deleted `RetentionMinutes` (60) after the job finished, **only while the web app is running**. Uploads left over from earlier sessions stay until you delete them. |
| Audio WAV, analysis frames | temporary work folder inside the job folder | deleted at the end of each analysis |
| Screenshots for the manual | temp files, read into memory, then deleted | the manual exists only in memory until the job is deleted |
| Transcript, event graph, manual | memory of the web app | until the job is deleted or the app stops |

Network traffic:
* The web app listens on `localhost:5080` only. WhisperX runs on `127.0.0.1:8011` and Ollama on
  `127.0.0.1:11434`.
* **No video, audio or text is sent to any external service.**
* Internet access only happens for one-time model downloads (Hugging Face, Ollama registry), possible
  version-check requests from those libraries, and the Google Fonts stylesheet of the demo page.

Speakers are labelled `SPEAKER_00` etc.; there is no face or voice identification. The outputs contain the
transcript and OCR text of clicked elements, so check whether these can contain personal data in your domain
(GDPR / DSGVO).

---

## 5. Web app and HTTP API

The page (`src/AvAg.Web/wwwroot/index.html`):
1. Choose a video and, optionally, mark it **Private**. Under *Services and analysis settings* you can set the
   spoken language (default: auto-detect), service URLs and frame rates. You can also upload a WhisperX
   transcript JSON and/or a UI-elements JSON to skip the corresponding services.
2. **Analyse video.** The player shows each detected click as a crosshair and the target element as a box,
   coloured by grounding status. The right column lists instructions and events in order, and the bottom shows
   the evidence, the raw JSON and the full transcript (`.txt` / `.srt`).
3. **Create manual.** Choose the language (same as spoken / German / English), private or not, and whether the
   language model writes it. A timer runs while the model works. The preview appears below, with downloads for
   Word, the web page and Markdown.

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/config` | default service URLs, retention, configured manual model |
| POST | `/api/jobs` | multipart: `video` or `use_demo=true`, optional `transcript`, `ui_json`, `cursor`, `private`, `lang`, `asr_url`, `ui_url`, `molmo_url`, `sam2_url`, `qwen_url`, `coarse_fps`, `fine_fps`, `diarize` → `{id}` |
| GET | `/api/jobs/{id}` | status, log, event graph, transcript |
| GET | `/api/jobs/{id}/graph.json`, `/timeline.{de\|en}.txt`, `/transcript.{txt\|srt}` | results |
| GET | `/api/jobs/{id}/video` | the uploaded video (range requests for seeking) |
| POST | `/api/jobs/{id}/manual` | form: `lang` (`de`/`en`/empty), `llm` (`true`/`false`), `private` → creates the manual |
| GET | `/api/jobs/{id}/manual.{docx\|html\|md}` | the manual |
| DELETE | `/api/jobs/{id}` | deletes the job and its files immediately |

---

## 6. Command line

```powershell
dotnet run --project src/AvAg.Cli -- run --video tutorial.mp4 --out out/tutorial.events.json --lang auto `
    --manual out/tutorial.docx --llm-url http://127.0.0.1:11434/v1 --llm-model avag-manual
```

| Option | Meaning |
|---|---|
| `--video`, `--out` | input video; event graph JSON (timelines `.timeline.de/.en.txt` are written next to it) |
| `--transcript <whisperx.json>` | use an existing transcript instead of the speech service |
| `--asr-url` | WhisperX service (default `http://127.0.0.1:8011`) |
| `--lang de\|en\|auto` | spoken language (default `de`; `auto` = detect) |
| `--manual <file.docx>` | also write the manual as `.docx`, `.html` and `.md` |
| `--manual-lang de\|en` | manual language (default: spoken language) |
| `--llm-url`, `--llm-model`, `--llm-key` | OpenAI-compatible model for the manual text |
| `--private` | text-only manual, no screenshots |
| `--ui-url`, `--ui-json`, `--molmo-url`, `--qwen-url`, `--sam2-url`, `--cursor` | optional services / inputs, see [11](#11-advanced-more-model-services-evaluation-calibration) |
| `--no-diarize`, `--coarse-fps`, `--fine-fps`, `--coarse-width`, `--keep`, `--describe` | analysis settings |

Other commands: `avag eval --pred <graph.json> --gt <groundtruth.json>` and `avag timeline --graph <graph.json>`.
Visual Studio launch profiles for AvAg.Cli: *run (offline demo)*, *eval (offline demo)*, *run (with sidecars)*, *help*.

---

## 7. Configuration reference

`src/AvAg.Web/appsettings.json`, section `AvAg`:

```json
"AvAg": {
  "RetentionMinutes": 60,
  "AutoStartSidecars": true,
  "Services": {
    "Asr":           { "Provider": "whisperx", "Url": "http://127.0.0.1:8011", "LocalModule": "whisperx_server:app" },
    "UiParser":      { "Provider": "none" },
    "Grounder":      { "Provider": "none" },
    "Tracker":       { "Provider": "none" },
    "ClipDescriber": { "Provider": "none" },
    "TextGenerator": { "Provider": "openai-compatible", "Url": "http://127.0.0.1:11434/v1", "Model": "avag-manual", "ApiKey": "", "TimeoutSeconds": 900 }
  }
}
```

| Key | Meaning |
|---|---|
| `RetentionMinutes` | delete finished jobs after this time (default 60) |
| `AutoStartSidecars` | start local sidecars (services with a `LocalModule` and a local `Url`) when they are not running |
| `Services:<Capability>` | which AI implements the capability, and where it runs – see [Replaceable AI services](#71-replaceable-ai-services) |

Every service entry has the same fields:

| Field | Meaning |
|---|---|
| `Provider` | provider name (table below); `none` switches the capability off; empty = the capability's default provider when `Url`/`Path` is set |
| `Url` | base URL of the service; for OpenAI-compatible endpoints include the version (`…/v1`) |
| `Model` | model name, where the provider needs one |
| `ApiKey` | only for hosted endpoints; sent as `Authorization: Bearer` and `api-key`. Put real keys in environment variables (`AvAg__Services__TextGenerator__ApiKey`) or user secrets, not in the file |
| `Path` | input file for file-based providers (`whisperx-json`, `ui-json`) |
| `TimeoutSeconds` | request timeout (default 1800) |
| `LocalModule` | Python module the web app starts from `sidecars/` when the service is local and not running (`whisperx_server:app`) |

Any setting can also come from environment variables, e.g. `AvAg__Services__Asr__Url=http://gpu-host:8011`.

### 7.1 Replaceable AI services

The pipeline never talks to a specific AI. It only uses six small interfaces
(`src/AvAg.Core/Abstractions/AiServices.cs`); `AiServiceFactory` (`src/AvAg.Pipeline/Services/`) picks the
implementation by the `Provider` name in the configuration:

| Capability (config key) | Interface | Built-in providers |
|---|---|---|
| Speech recognition (`Asr`) | `IAsrService` | `whisperx` (default), `whisperx-json` (transcript file) |
| UI elements + text (`UiParser`) | `IUiParser` | `omniparser` (default), `ui-json` (file) |
| Fallback pointing (`Grounder`) | `IVideoGrounder` | `molmo` (default), `qwen-vl` |
| Box tracking (`Tracker`) | `IObjectTracker` | `sam2` |
| Clip narratives (`ClipDescriber`) | `IClipDescriber` | `qwen-vl` |
| Manual writer (`TextGenerator`) | `ITextGenerator` | `openai-compatible` (aliases `ollama`, `openai`, `azure-openai`, `lm-studio`, `vllm`) |

**Switching to another AI that is already supported is configuration only.** For example, to write manuals with
a hosted OpenAI-compatible model instead of the local one:

```json
"TextGenerator": { "Provider": "openai", "Url": "https://api.openai.com/v1", "Model": "<model>", "ApiKey": "" }
```

**Adding a new AI** (a vendor with its own API, a different speech recogniser, …) takes three steps, explained with
a full example in [`docs/ADDING_AN_AI.md`](docs/ADDING_AN_AI.md):
1. write one class that implements the capability's interface;
2. register it with a provider name in `AiServiceFactory.CreateDefault()` (or on the factory in `AvAg.Web/Program.cs`);
3. select that name in `appsettings.json` (or with `--<cap>-provider` on the CLI).

Nothing else in the pipeline, the web app or the manual changes.

WhisperX service environment variables: `AVAG_WHISPER_MODEL` (e.g. `large-v3`, `medium`, `small`),
`AVAG_WHISPER_DEVICE` (`cuda`/`cpu`), `AVAG_WHISPER_COMPUTE` (`float16`/`int8_float16`/`int8`),
`AVAG_WHISPER_BATCH`, `HF_TOKEN` (enables speaker separation).

Manual model: `sidecars/manual-llm.Modelfile` (`FROM qwen2.5:7b`, `num_gpu 0`, `num_ctx 12288`,
`temperature 0.2`). After changing it, run `ollama create avag-manual -f sidecars/manual-llm.Modelfile` again.

---

## 8. Processes, ports and hardware

| Process | Port | Started by | Uses |
|---|---|---|---|
| AvAg.Web | 5080 (localhost) | Visual Studio / `dotnet run` | CPU, RAM for frames during analysis |
| WhisperX (`sidecars/whisperx_server.py`) | 8011 | AvAg.Web automatically, or manually | GPU (≈1 GB idle, ≈3 GB while transcribing with `medium`) or CPU |
| Ollama (`avag-manual`) | 11434 | Windows autostart of Ollama | CPU only, ≈5.5 GB RAM while loaded (unloads after 5 idle minutes) |
| FFmpeg | – | short-lived, per step | CPU |

The WhisperX port is **8011**, not 8001, because some headset drivers (e.g. Sennheiser/Mitel `secomsdk.exe`)
occupy 127.0.0.1:8001.

Tested on a laptop with a 4 GB NVIDIA RTX 500 Ada GPU and 64 GB RAM. For a 2-minute 1080p video: analysis
≈ 1–2 min, AI-written manual ≈ 2–3 min.

---

## 9. Project layout and tests

```
src/AvAg.Core                      pure logic, no I/O
  Abstractions/AiServices.cs       the replaceable AI interfaces (IAsrService, ITextGenerator, IManualWriter, …)
  Models.cs, AudioRefParser.cs, UiElementRegistry.cs, Fusion.cs, Describer.cs, Metrics.cs
  Manuals/                         Manual model, ManualBuilder (rule-based steps, screenshot planning),
                                   ManualRenderer (HTML, Markdown), ManualDocx (Word)
src/AvAg.Pipeline                  everything that touches files, processes or the network
  PipelineRunner.cs                the analysis, step by step
  Media/, Vision/                  FFmpeg, frame differencing, cursor tracking, click detection
  Adapters/<capability>/           one file per AI implementation (WhisperX, OmniParser, Molmo, SAM2, Qwen-VL,
                                   OpenAI-compatible text generation) + HttpServiceClient base
  Services/                        AiServicesOptions (configuration) + AiServiceFactory (provider registry)
  Manuals/                         ManualService (orchestration), LlmManualWriter (prompt + answer parsing),
                                   ManualScreenshotService (frame choice + extraction)
src/AvAg.Cli                       `avag run | eval | timeline`
src/AvAg.Web                       Program.cs (wiring only), Endpoints/ (jobs, manual), JobRunner, Jobs (store,
                                   settings, clean-up), SidecarLauncher, wwwroot/index.html
tests/AvAg.Tests                   NuGet-free test runner (19 tests)
sidecars/                          whisperx_server.py, manual-llm.Modelfile, optional molmo/omniparser/sam2 servers
deploy/                            docker-compose.yml for a Linux GPU host
samples/                           synthetic demo video, transcript, UI elements, ground truth
docs/                              ADDING_AN_AI.md, ROADMAP.md
```

Dependencies point one way: `Web`/`Cli` → `Pipeline` → `Core`. Core has no knowledge of any model, file or
service, so it can be tested and reused on its own.

`dotnet run --project tests/AvAg.Tests` runs all tests. The end-to-end test synthesises a 640×360 recording
where the cursor clicks a „Speichern“ button, and checks that the click is found at 12.70 ± 0.12 s, inside the
button, and bound to the spoken instruction. The manual tests cover step extraction, click markers, sections,
key combinations, Word output, and that a private manual never reads a frame from the video. The service tests
check provider selection by name, that a new AI can be registered, that the language-model writer ties every step
to real transcript sentences (using a fake model, no network), and that a failing model falls back to the
rule-based manual.

---

## 10. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| "A model service could not be reached (… 127.0.0.1:8011)" | The WhisperX service is not running and could not be started: check that `sidecars/.venv` exists ([1.2](#12-speech-recognition-service-one-time)) and read `sidecars/whisperx_server.log`. Or upload a transcript JSON. |
| English video transcribed as German | Set the spoken language to **Auto-detect** (the default) or `--lang auto`. |
| "GPU out of memory" / laptop freezes during transcription | Set `AVAG_WHISPER_BATCH=1`, a smaller model (`AVAG_WHISPER_MODEL=small`) or `AVAG_WHISPER_DEVICE=cpu`. Never run two copies of the service. |
| Manual says "kept the rule-based manual" | Ollama is not running, or the model answer was unusable. Check `ollama list` (needs `avag-manual`) and http://127.0.0.1:11434. |
| Manual takes very long | The model runs on the CPU on purpose. Use a smaller model in the Modelfile (e.g. `qwen2.5:3b`, faster but noticeably worse) or point `Services:TextGenerator` to a faster endpoint. |
| Build error "file is locked by AvAg.Web" | Stop the running web app (Visual Studio: Stop debugging) and build again. |
| `torchcodec` warnings in the WhisperX log | Harmless: audio is passed to pyannote as an in-memory waveform. |

---

## 11. Advanced: more model services, evaluation, calibration

### 11.1 Optional model services (Linux GPU host recommended)

```bash
cd sidecars
uvicorn omniparser_server:app --port 8003 &   # UI elements + OCR: OMNIPARSER_HOME=/opt/OmniParser, AVAG_OCR_LANG=german
uvicorn molmo_server:app      --port 8002 &   # AI pointing fallback: AVAG_MOLMO_MODEL=allenai/Molmo2-VideoPoint-4B
uvicorn sam2_server:app       --port 8004 &   # box tracking: SAM2_CFG / SAM2_CKPT
vllm serve Qwen/Qwen3-VL-8B-Instruct --port 8005 --allowed-local-media-path /data/videos
```

Or all at once with `cd deploy && HF_TOKEN=... docker compose up -d`. Full requirements:
`sidecars/requirements.txt`. With OmniParser the events name the clicked button („Speichern“), and manual
screenshots get a box around the element.

### 11.2 Cursor template

`--cursor` is a tight PNG crop of the recorded mouse pointer (hot-spot at the top-left). Without it the tracker
uses moving-blob detection, which works on clean recordings and less well on heavily compressed video.

### 11.3 Output format

```jsonc
{
  "schema_version": "1.0",
  "video": { "id": "demo", "duration_s": 16, "width_px": 640, "height_px": 360, "timebase": "seconds_from_media_pts" },
  "events": [{
    "event_id": "event_0000", "type": "click",
    "temporal": { "start_s": 11.9, "peak_s": 12.7, "end_s": 12.8, "confidence": 0.87 },
    "spatial":  { "coordinate_system": "pixel_xy_origin_top_left", "point_xy_px": [496, 264], "bbox_xyxy_px": [440, 240, 560, 290], "confidence": 0.75 },
    "target":   { "object_id": "ui_000", "class": "button", "text": "Speichern", "text_confidence": 0.98 },
    "audio_references": [{ "audio_ref_id": "audio_0000", "start_s": 12.22, "end_s": 12.58, "transcript": "Klicken Sie hier.", "relation": "deictic_instruction_for_event" }],
    "evidence": [{ "source": "cursor_tracker", "confidence": 0.7 }, { "source": "frame_state_change", "confidence": 0.98 }],
    "grounding_status": "observed", "overall_confidence": 0.81
  }],
  "unbound_audio_references": []
}
```

### 11.4 Evaluation and calibration

Annotate videos in the format of `samples/demo_groundtruth.json`, then run
`avag eval --pred out/x.json --gt gt/x.json --tol 0.25`. It reports AVAGA@250ms (speaker ∧ phrase ∧ action ∧
|Δt| ≤ 250 ms ∧ point ∈ target), phrase recall, action accuracy, time success, point-hit accuracy, pixel distances
and factual event precision. The fusion constants (`FusionConfig`) and detector thresholds (`StateChangeDetector`,
`CursorTracker`, `ClickCandidateDetector`) are starting values; tune them against AVAGA on your own videos.
See `docs/ROADMAP.md` for the phased plan.

---

## 12. Licensing

This repository: Apache-2.0 (`LICENSE`). Check the licence of each model before commercial use:

| Component | Licence |
|---|---|
| WhisperX | BSD-2-Clause; faster-whisper MIT; pyannote models have their own terms |
| Qwen2.5 (manual writer), Qwen3-VL, Molmo2, SAM2, PaddleOCR | Apache-2.0 (check datasets/checkpoints) |
| Ollama | MIT |
| OmniParser | repo CC-BY-4.0; current detector weights MIT, older Ultralytics weights AGPL – **check** |
