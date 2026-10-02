# AI Video Analysis Demo (AvAg)

**Support call recording in → wiki article out.**

Our hotline records support calls: a customer describes a problem, a supporter fixes it, usually with screen
sharing. AvAg takes such a recording and works out

* **what the problem was:** the symptoms and the exact error message,
* **why it happened:** if the call makes it clear,
* **how it was fixed:** step by step, in the order the customer can repeat it,

and writes a **wiki article in Markdown**. The next customer with the same problem can then fix it themselves.

* **Normal video:** every solution step gets a screenshot from the recording, with the click marked where it was
  observed.
* **Private video:** the article is text only, no frame is ever taken from the video, and only local services
  (this machine or the local network) see the call – nothing goes to OpenAI.

**AI used (configurable):**
* **OpenAI:** `gpt-4o-transcribe-diarize` for speech recognition with customer/supporter labels, and `gpt-5.5` for
  the article. The article model also sees frames of the supporter's screen.
* **Local fallback:** if OpenAI is not reachable or out of credit, the program automatically uses local WhisperX
  and Ollama. Those also work completely offline when no OpenAI key is configured.
* **API keys:** they live in .NET user secrets or environment variables, never in the repository. A test and a
  pre-commit hook make sure of that.

```markdown
## Problem
Die Rechnungen können nicht mehr gespeichert werden.
**Fehlermeldung:** `Speichern nicht möglich`
## Ursache
Im Profil ist kein Speicherpfad hinterlegt.
## Lösung
### 1. Speicherpfad hinterlegen
Öffnen Sie die Einstellungen und wählen Sie unter Profil den Ordner Dokumente aus.
![Bildschirm bei Schritt 1](images/step-01.jpg)
### 2. Speichern
Klicken Sie auf die Schaltfläche Speichern.
…
```

AvAg stands for *Audio-Visual Action Grounding*: it links what is said ("klicken Sie hier") to where and when it
was clicked on screen. That grounding is what makes the screenshots and click markers trustworthy.

---

## Contents

1. [Quick start (Windows)](#1-quick-start-windows)
2. [How it works: from recording to wiki article](#2-how-it-works-from-recording-to-wiki-article)
3. [The analysis pipeline in detail](#3-the-analysis-pipeline-in-detail)
4. [How the article is written](#4-how-the-article-is-written)
5. [Privacy: private videos, personal data, where data goes](#5-privacy-private-videos-personal-data-where-data-goes)
6. [The output: Markdown for the wiki](#6-the-output-markdown-for-the-wiki)
7. [Web app and HTTP API](#7-web-app-and-http-api)
8. [Command line](#8-command-line)
9. [Configuration and replaceable AI services](#9-configuration-and-replaceable-ai-services)
10. [Processes, ports and hardware](#10-processes-ports-and-hardware)
11. [Project layout and tests](#11-project-layout-and-tests)
12. [Troubleshooting](#12-troubleshooting)
13. [Advanced: more model services, evaluation](#13-advanced-more-model-services-evaluation)
14. [Licensing](#14-licensing)

---

## 1. Quick start (Windows)

### 1.1 Prerequisites

| What | Why | Install |
|---|---|---|
| .NET 8 SDK (Visual Studio 2022 17.8+ or VS 2026) | builds and runs the C# projects | `winget install Microsoft.DotNet.SDK.8` |
| FFmpeg ≥ 6 (`ffmpeg`, `ffprobe` on `PATH`) | decodes the video, extracts audio and screenshots | `winget install Gyan.FFmpeg` (restart VS / terminal afterwards) |
| Python 3.11 | runs the speech-recognition service (WhisperX) | python.org or `winget install Python.Python.3.11` |
| NVIDIA GPU with CUDA (optional) | faster speech recognition; without a GPU it runs on the CPU | current NVIDIA driver |
| Ollama (recommended) | local language model that writes the article | `winget install Ollama.Ollama` |

### 1.2 Speech-recognition service (one time)

```powershell
cd sidecars
python -m venv .venv
.venv\Scripts\python -m pip install --upgrade pip
# PyTorch with CUDA first (skip the index URL for CPU-only), then WhisperX and the web server
.venv\Scripts\python -m pip install torch torchaudio --index-url https://download.pytorch.org/whl/cu128
.venv\Scripts\python -m pip install whisperx fastapi "uvicorn[standard]"
```

Tested with `torch 2.8.0+cu128` and `whisperx 3.8.6`. You don't need to start the service yourself: the web app
starts it when a job needs it.

**Customer vs. supporter:** WhisperX can label the speakers (`SPEAKER_00`, `SPEAKER_01`) so the language model
knows who says what. That needs a free Hugging Face token with access to the pyannote models: set `HF_TOKEN`
before starting the web app. Without it, the model tells customer and supporter apart from what they say, which
works well in practice.

### 1.3 OpenAI key (recommended)

Store the key **outside the repository**, in .NET user secrets (`%APPDATA%\Microsoft\UserSecrets\avag-web\secrets.json`).
The web app reads them when it runs in Development, which is the case for F5 and `dotnet run`:

```powershell
dotnet user-secrets set "AvAg:Services:TextGenerator:ApiKey" "<your key>" --project src/AvAg.Web
```

One key is enough: every service on `api.openai.com` without a key of its own (speech recognition, screen reading,
article) uses the key of another OpenAI service, or the `OPENAI_API_KEY` environment variable.
On a server, use environment variables instead (`AvAg__Services__TextGenerator__ApiKey` or `OPENAI_API_KEY`).
The CLI reads `OPENAI_API_KEY`. Enable the key check for your commits once per clone:

```powershell
git config core.hooksPath tools/hooks     # refuses commits that contain something that looks like an API key
```

Without a key, or without credit on the account, everything still works with the local models below.

### 1.4 Local article writer (fallback, one time)

```powershell
ollama pull qwen2.5:7b
ollama create avag-article -f sidecars/article-llm.Modelfile
```

`article-llm.Modelfile` sets `num_gpu 0`, so the model runs **only on the CPU** and never competes with speech
recognition for a small laptop GPU. Without Ollama you still get an article, written rule-based
([4.2](#42-rule-based-draft-always-available)).

### 1.5 Run

* **Visual Studio:** open `AvAg.sln`, set **AvAg.Web** as the startup project and press **F5**. The page
  http://localhost:5080 opens.
* **Terminal:** `dotnet run --project src/AvAg.Web`

Drop the call recording on the page (tick **Private video** if needed) and press **Analyse video**. Then press
**Create wiki article**, and download the `.zip` (Markdown + images) or copy the Markdown.

### 1.6 Check the installation

```powershell
dotnet build
dotnet run --project tests/AvAg.Tests     # 40 tests, incl. an end-to-end run on a synthetic screen recording
```

---

## 2. How it works: from recording to wiki article

```
support call recording (.mp4)
   │
   ├─ 1. Analysis (PipelineRunner) ─────────────────────────────────────────────────────────────────────────────┐
   │     FFmpeg: audio + frames                                                                                 │
   │     Speech: OpenAI gpt-4o-transcribe-diarize (who said what) + local WhisperX word alignment               │
   │             – or fully local WhisperX when OpenAI is not available                                        │
   │     Spoken instructions ("klicken Sie auf Speichern") ↔ observed clicks on screen (cursor rests + screen    │
   │     changes) → event graph with evidence and a grounding status per click                                  │
   │                                                                                                            │
   ├─ 2. Article (ArticleService) ◄──────────────────────────────────────────────────────────────────────────────┘
   │     a) rule-based draft: problem, error message, cause, steps, verification
   │     b) language model (gpt-5.5, fallback: local Ollama) writes the article from the whole numbered
   │        transcript, the observed clicks and – for non-private videos – up to 12 frames of the screen; steps
   │        point to the sentences they come from. Observed click steps the model left out are put back.
   │        On any failure: (a).
   │     c) personal data removed (e-mail, phone, IBAN, customer numbers, names, companies)
   │     d) screenshots: one per step, at the observed click (red box) or at a settled, distinct frame
   │        ── skipped entirely for private videos
   │
   └─ 3. Output: article.md (front matter + Markdown) + images/step-NN.jpg  →  your wiki
```

The analysis and the article are separate steps. You can create the article several times (different language,
with or without the model, private or not) without analysing the video again.

---

## 3. The analysis pipeline in detail

`src/AvAg.Pipeline/PipelineRunner.cs` runs these steps for one video.

### 3.1 Ingest (FFmpeg)

`FfmpegService` reads duration, size and frame rate with `ffprobe`. It extracts the audio as 16 kHz mono WAV
and decodes frames as grayscale bitmaps, taking the time stamps from FFmpeg (`showinfo` → `pts_time`) so
variable-frame-rate recordings keep correct times. The WAV and frames live in a temporary work folder that is
**deleted after the run**.

### 3.2 Speech recognition

**OpenAI (default)** – `Adapters/Asr/OpenAiTranscriber.cs`:
* The audio is uploaded as compressed mono MP3 (32 kbit/s: about 14 h fit into the 25 MB limit).
* `gpt-4o-transcribe-diarize` returns the text with **speaker labels** (`SPEAKER_A`, `SPEAKER_B`), so the article
  model knows who is the customer and who is the supporter. It is clearly better than the local model,
  especially for Swiss German and technical terms.
* It gives times per segment, not per word. The local WhisperX sidecar therefore aligns the text to **exact word
  times** (`POST /align`, configured as `AlignUrl`); these tie "klicken Sie hier" to the click on screen. If the
  aligner isn't running, word times are spread over the segment and the log says so.
* Alternatives: `whisper-1` gives word times itself but no speakers. `Prompt` passes product names and
  abbreviations (not supported by the diarize model).
* **Long calls:** one request takes at most 1400 s (23 min). A longer call is cut at pauses into parts of about
  20 min. The first part is transcribed alone; its speakers (a few seconds of each voice) go to the other parts as
  `known_speaker_references`, so `SPEAKER_A` stays the same person for the whole call. The other parts run in parallel.
* If OpenAI fails (no network, no credit, invalid key, timeout), the configured **fallback**, the local WhisperX,
  transcribes instead. The job log says why.

**Local WhisperX (fallback, or the only engine without OpenAI):**
`sidecars/whisperx_server.py` is a small FastAPI service (`POST /transcribe {audio_path, language, diarize}`):

1. **Transcription:** faster-whisper. `large-v3` on GPUs with ≥ 8 GB, `large-v3-turbo` on smaller GPUs and the CPU
   (nearly as accurate as `large-v3`, several times faster, and clearly better than the former `medium`).
2. **Word alignment:** every word gets a start and end time. These timings tie speech to clicks.
3. **Speakers (optional):** pyannote, when `HF_TOKEN` is set.

On small GPUs (< 8 GB) the weights load as `int8_float16` with batch 4, only one request runs at a time, and GPU
memory is released after every request. That keeps a 4 GB laptop GPU from running out of memory. The speech model
loads on the first `/transcribe`; a sidecar that only aligns OpenAI transcripts never loads it.

### 3.3 Spoken instructions (AudioRefParser)

`src/AvAg.Core/AudioRefParser.cs` finds instructions in German and English: click/klicken, double-click,
right-click, drag, type, select, scroll …. It notes deictic words ("hier", "this") and named targets
("auf Speichern"). Each hit gets an exact time anchor.

### 3.4 Clicks on screen (Vision/)

* `StateChangeDetector` finds where and when the screen changed.
* `CursorTracker` finds the mouse pointer.
* `ClickCandidateDetector` reports a click when the cursor rests and the screen changes right there.

A coarse pass (3 fps) covers the whole video. A fine pass (20 fps, full resolution) re-checks the window around
every spoken instruction.

### 3.4a Reading the screen (UiParser)

The default `openai-vision` provider sends the region around each click (640 × 400 px of the frame before the click)
to GPT-5.5 (`ReasoningEffort: low`). It returns the UI elements there with their exact texts and boxes, so a click is
named ("„Speichern“ anklicken") even when nobody said the button's name, and the article model gets the real menu
and button names. Six frames are read at a time; a frame that fails after three retries is skipped and counted in
the log. Private videos never use it (local services only); the local alternative is the OmniParser sidecar
(`omniparser`), or `none`.

### 3.4b Speed

* Speech recognition runs **at the same time** as the coarse visual pass.
* Frames are **streamed** from ffmpeg and analysed as they arrive: memory stays at about 100 MB however long the
  video is (before: every frame of the coarse pass in memory, about 1.5 GB for 5 min of 1080p).
* The fine-pass windows, the frames for screen reading and the article screenshots are decoded in parallel.
* The article reuses the 1-fps thumbnails of the analysis instead of decoding the whole video again.
* The log shows the time of every stage.

On a 5-minute 1080p test recording (16 cores) the analysis plus a rule-based article went from 24 s to under 10 s,
with identical results.

### 3.5 Fusion and grounding status

`src/AvAg.Core/Fusion.cs` binds spoken instructions to clicks:
`S = 0.35·time + 0.20·meaning + 0.15·pointer + 0.15·UI element + 0.15·screen change`. Every click gets a
`grounding_status`:

| Source of the coordinate | `grounding_status` |
|---|---|
| visible cursor, telemetry, or UI change at a resting cursor | `observed` |
| SAM2-propagated box | `tracked` |
| AI pointing only (MolmoPoint / Qwen3-VL) | `inferred` |
| no cursor, no localised change | `unobservable`: no position is claimed |

---

## 4. How the article is written

`src/AvAg.Pipeline/Articles/ArticleService.cs` orchestrates the steps below.

### 4.1 Sentences

`TranscriptSentences.Split` (`src/AvAg.Core/Transcripts/`) turns the word-timed transcript into sentences, with the
speaker label when available. Long unpunctuated run-ons are cut at 40 words, a change of speaker always starts a new
sentence, and filler words ("äh", "um", leading "also/okay") are removed.

### 4.2 Rule-based draft (always available)

`ArticleBuilder` (`src/AvAg.Core/Articles/`) works without any AI. It is the fallback, and the evidence the model's
article is checked against:

| Part | Rule |
|---|---|
| Small talk | greetings, "danke", "auf Wiederhören", "sehr gut" … are dropped |
| **Problem** | the first sentences before the solution that say something doesn't work ("kann nicht", "Fehlermeldung", "doesn't work" …) |
| **Error message** | quoted text in those sentences, or what follows "Meldung:" / "error:" |
| **Cause** | the first sentence that explains why ("das liegt daran, dass …", "because …") |
| **Steps** | every sentence that tells the customer to do something: a recognised click, or an instruction verb (öffnen, wählen, speichern, open, select …) addressed to the customer ("Sie", "bitte", "you") |
| Details | up to 2 explaining sentences after a step |
| **Verification** | the last "funktioniert jetzt wieder" / "works now" after the steps |
| Keywords | the UI elements named in the instructions |

### 4.3 AI-written article (when a language model is configured)

`LlmArticleWriter` works with whatever language model `Services:TextGenerator` selects:
* **Default:** OpenAI `gpt-5.5` with `ReasoningEffort: medium`.
* **Fallback:** local `avag-article` (Qwen2.5 7B in Ollama, CPU only).

The model gets:
* the **whole call as numbered sentences**, e.g. `[5] (00:33) SPEAKER_A: Öffnen Sie bitte die Einstellungen …`;
* the **clicks observed in the video**, e.g. `[00:41] Klick bei (496, 264) – gesagt: „Klicken Sie hier auf Speichern“`;
* for models that read images (`Vision: true`) and **non-private videos only**: up to **12 frames of the
  supporter's screen**. They are picked where the screen changed and settled, or where a click happened. They
  let the model name menus, tabs, buttons and error dialogs exactly, even when the supporter only says "hier".

It asks for JSON with:

`title`, `problem`, `error_messages` (verbatim), `cause`, `applies_to` (product/module/version), `steps`
(one action each, imperative, with `actor`, `section` for alternative solutions, and the `sentences` it comes from),
`verification`, `notes`, `keywords`, `resolved`.

The prompt tells the model to:
* write for a customer reading the wiki, not to retell the call;
* describe **only the way that finally worked**, and leave out failed attempts, questions and diagnosis;
* give menu paths in full ("Datei › Einstellungen › Profil");
* name the element instead of saying "hier";
* keep program, menu and button names and error messages exactly as spoken;
* **include no personal data**;
* mark steps only support can do (licence, server, account) as `actor: support`. They appear as *(nur durch den
  Support)*.

Checks after the answer:
* **Anchoring:** each step's place in the video comes from its sentence numbers, not from times the model
  invents. At least half the steps must point to real sentences.
* **Nothing observed is lost:** a draft step with an observed click that none of the model's steps cover is
  added back (`RestoreObservedSteps`).
* **Gaps filled:** if the model leaves the cause, verification, error message or keywords empty, the draft's are
  used.
* **Unsolved calls:** if the call ended without a solution (`resolved: false`), the article says so.
* **Model fallback:** if OpenAI fails (out of credit, offline, timeout), the configured fallback model writes the
  article, and the log names the model that did.
* **Draft fallback:** if every model fails or the answer is unusable, the rule-based draft is used and the page
  shows why.

### 4.4 Screenshots (not for private videos)

`ArticleBuilder.PlaceScreenshots` and `ArticleScreenshotService`:

1. If an observed click belongs to an instruction spoken **in this step**, the screenshot is taken at the click,
   with a red box around the click point (and the UI element, if known). It only counts if the instruction was a
   click or the click visibly changed the screen.
2. Otherwise the step's own part of the video is sampled as thumbnails (2 fps, 160 px). The chosen frame is
   *settled* (menus fully open) and *differs most* from the previous screenshot.
3. A step whose screen looks like the previous step's gets no picture instead of a duplicate.
4. Screenshots are in video order, ≥ 1.5 s apart, at most 1280 px wide.

---

## 5. Privacy: private videos, personal data, where data goes

### 5.1 Private videos

Tick **Private video** when uploading, or **Private – text only** next to *Create wiki article* (CLI:
`--private`). The article is then text only: **no frame is extracted from the video** and no click position is
kept. A video marked private at upload can never get screenshots later.

Private also means **local services only**: every cloud entry (OpenAI, any URL outside `localhost` / the private
network ranges) is dropped from the service chains, so speech recognition runs on the local WhisperX and the
article is written by the local model (Ollama) or by the rule-based builder. A private upload is refused if no
local speech recognition is configured; upload a transcript JSON in that case.

### 5.2 Personal data in the article

Support calls are full of personal data. Three layers keep it out of the wiki:

1. **The model is told** to leave out names, companies, phone numbers, e-mail addresses, customer/licence/contract
   numbers, addresses and passwords.
2. **`PersonalDataRedactor`** removes whatever can be recognised by its shape and replaces it with `[entfernt]` /
   `[removed]`. The count is shown on the page. It catches:
   * e-mail addresses, phone numbers (Swiss and international), IBANs, AHV and card numbers;
   * the value after "Kundennummer", "Lizenzschlüssel", "Passwort" …;
   * names after "Herr/Frau/Mr/Ms" or "mein Name ist" / "hier ist X von …";
   * company names ("Firma X", "X AG/GmbH").
3. **The article is marked `status: draft`**, and the page says to review it before publishing. A redactor can't
   recognise every name, and **screenshots of a non-private video show the screen as it was**: customer data
   visible on screen ends up in the images. If that's a risk, use private mode.

The article never mentions the video's file name, the date of the call or who called.

### 5.3 Where data goes

| Data | Where | How long |
|---|---|---|
| Uploaded video (and transcript/UI JSON if uploaded) | `%TEMP%\avag-web\<job id>\` | deleted `RetentionMinutes` (60) after the job finished, **only while the web app is running**. Leftovers from earlier sessions stay until deleted. |
| Audio WAV, analysis frames, screenshot temp files | temporary files | deleted at the end of each step |
| Transcript, event graph, article | memory of the web app | until the job is deleted or the app stops |

* The web app listens on `localhost:5080` only; WhisperX runs on `127.0.0.1:8011` and Ollama on `127.0.0.1:11434`.
* **With the default configuration, OpenAI receives:**

  | | Normal video | Private video |
  |---|---|---|
  | Audio of the call (MP3) | yes | **never** |
  | Transcript with speaker labels | yes | **never** |
  | Observed clicks (time and position) | yes | **never** |
  | Frames of the screen | up to 12 | **never** |
  | The video file | never | never |

  Check that this is allowed for your customers' data (data-processing agreement with OpenAI, data residency).
  If it isn't, remove the OpenAI entries in `Services` and keep only the local providers. Then nothing leaves the
  machine; Internet access only happens for one-time model downloads and the page's Google Fonts.

---

## 6. The output: Markdown for the wiki

`ArticleRenderer.Markdown` and `ArticlePackage` (`src/AvAg.Core/Articles/`):

```
rechnungen-nicht-mehr-speichern.zip
├── article.md
└── images/
    ├── step-01.jpg
    └── step-02.jpg
```

`article.md`:

```markdown
---
title: "Rechnungen nicht mehr speichern"
tags: ["Rechnungen speichern", "Speicherpfad", "Einstellungen"]
language: de
status: draft
resolved: true
source: "Support-Aufzeichnung (llm:avag-article)"
generated: 2026-10-01
---

# Rechnungen nicht mehr speichern

> Automatisch aus einer Support-Aufzeichnung erstellt – bitte vor dem Veröffentlichen prüfen.

## Problem
## Ursache
**Betrifft:** …
## Lösung
### 1. …            (with ### sections when there are alternative solutions, *(nur durch den Support)* where needed)
![Bildschirm bei Schritt 1](images/step-01.jpg)
<!-- video 00:33 -->
## Prüfen, ob es funktioniert
## Hinweise
```

* **Plain CommonMark plus YAML front matter.** It imports into Wiki.js, GitHub/GitLab wikis, Azure DevOps wikis,
  Docusaurus, MkDocs, Obsidian and most others. Wikis without front matter show it as a short block at the top,
  which you can delete.
* **Key combinations** appear as `` `Strg` + `C` ``.
* **`<!-- video mm:ss -->`** comments tell a reviewer where in the recording to look; wikis don't display them.
* **Images use relative links.** Upload the `images` folder with the article. A private article is just
  `article.md`.
* **Language:** the article is written in the spoken language by default, or German/English on request.

---

## 7. Web app and HTTP API

The page (`src/AvAg.Web/wwwroot/index.html`):
1. **Choose the recording** and, if needed, tick **Private video**. *Services and analysis settings* has the
   spoken language (default auto-detect), service URLs (only changed fields are sent; an emptied field switches
   that service off, another URL uses that sidecar there without the configured key) and frame rates. You can also upload an existing transcript instead of using speech recognition.
2. **Analyse video.** The player shows the detected clicks; the transcript and events are listed below.
3. **Create wiki article.** Choose the language, private or not, and whether the language model writes it. A
   timer runs while the model works (CPU: usually under a minute for a short call, a few minutes for long
   ones). You get:
   * a **Preview** and **Markdown** tab;
   * **Download for the wiki (.zip)**, **Markdown only (.md)** and **Copy Markdown**;
   * a status line with the step and screenshot counts, removed personal data, unsolved cases and any fallback.

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/config` | default service URLs, retention, configured article model |
| POST | `/api/jobs` | multipart: `video` or `use_demo=true`; optional `transcript`, `ui_json`, `cursor`, `private`, `lang`, `asr_url`, `ui_url`, `molmo_url`, `sam2_url`, `qwen_url` (absent or the configured URL = as configured, empty = off, other URL = that sidecar there, no key), `coarse_fps`, `fine_fps`, `diarize` → `{id}` |
| GET | `/api/jobs/{id}` | status, log, event graph, transcript, services used |
| GET | `/api/jobs/{id}/graph.json`, `/timeline.{de\|en}.txt`, `/transcript.{txt\|srt}`, `/video` | analysis results |
| POST | `/api/jobs/{id}/article` | form: `lang` (`de`/`en`/empty), `llm` (`true`/`false`), `private` → `{title, steps, screenshots, redactions, resolved, method, log}` |
| GET | `/api/jobs/{id}/article.{md\|zip\|html}` | Markdown, wiki package, HTML preview |
| DELETE | `/api/jobs/{id}` | delete the job and its files now |

---

## 8. Command line

```powershell
$env:OPENAI_API_KEY = "<your key>"     # only in this terminal session
dotnet run --project src/AvAg.Cli -- run --video call.mp4 --out out/call.events.json --lang de `
    --asr-provider openai --asr-align http://127.0.0.1:8011 --asr-fallback-url http://127.0.0.1:8011 `
    --article out/call-article --llm-provider openai --llm-model gpt-5.5 --llm-vision `
    --llm-fallback-provider ollama --llm-fallback-url http://127.0.0.1:11434/v1 --llm-fallback-model avag-article
# → out/call-article/article.md + out/call-article/images/step-NN.jpg
```

Fully local: leave out the `--asr-*` and use `--llm-url http://127.0.0.1:11434/v1 --llm-model avag-article`.

| Option | Meaning |
|---|---|
| `--video`, `--out` | the recording; event graph JSON (timelines `.timeline.de/.en.txt` next to it) |
| `--lang de\|en\|auto` | spoken language (default `de`) |
| `--article <folder>` | write the wiki article into this folder |
| `--article-lang de\|en` | article language (default: spoken language) |
| `--private` | text only, no screenshots, local services only |
| `--llm-url`, `--llm-model`, `--llm-key`, `--llm-provider` | the language model that writes the article (without: rule-based). A broken configuration is reported before the analysis starts. |
| `--llm-vision`, `--llm-effort <level>` | send screen frames to a vision model; reasoning effort |
| `--asr-provider openai`, `--asr-model`, `--asr-align <url>`, `--asr-prompt "<terms>"` | OpenAI speech recognition, word alignment via local WhisperX, vocabulary |
| `--<cap>-fallback-provider/-url/-model` | the service used when the first one fails |
| `OPENAI_API_KEY` (environment) | key for every OpenAI service when no `--<cap>-key` is given |
| `--transcript <whisperx.json>` | use an existing transcript instead of speech recognition |
| `--<cap>-provider/-url/-model/-key` | any AI service, `<cap>` = `asr`, `ui`, `grounder`, `tracker`, `describer`, `llm` |
| `--ui-json`, `--molmo-url`, `--qwen-url`, `--sam2-url`, `--cursor` | optional services / inputs ([13](#13-advanced-more-model-services-evaluation)) |
| `--no-diarize`, `--coarse-fps`, `--fine-fps`, `--coarse-width`, `--keep`, `--describe` | analysis settings |

Other commands: `avag eval --pred <graph.json> --gt <groundtruth.json>` and `avag timeline --graph <graph.json>`.

---

## 9. Configuration and replaceable AI services

`src/AvAg.Web/appsettings.json`, section `AvAg`:

```json
"AvAg": {
  "RetentionMinutes": 60,
  "AutoStartSidecars": true,
  "Services": {
    "Asr": {
      "Provider": "openai", "Url": "https://api.openai.com/v1", "Model": "gpt-4o-transcribe-diarize", "AlignUrl": "http://127.0.0.1:8011",
      "Fallback": { "Provider": "whisperx", "Url": "http://127.0.0.1:8011", "LocalModule": "whisperx_server:app" }
    },
    "UiParser": { "Provider": "openai-vision", "Url": "https://api.openai.com/v1", "Model": "gpt-5.5", "ReasoningEffort": "low" }, "Grounder": {}, "Tracker": { "Provider": "none" }, "ClipDescriber": { "Provider": "none" },
    "TextGenerator": {
      "Provider": "openai", "Url": "https://api.openai.com/v1", "Model": "gpt-5.5", "Vision": true, "ReasoningEffort": "medium", "TimeoutSeconds": 600,
      "Fallback": { "Provider": "ollama", "Url": "http://127.0.0.1:11434/v1", "Model": "avag-article", "TimeoutSeconds": 900 }
    }
  }
}
```

The API keys are **not** in this file. They come from user secrets or environment variables
([1.3](#13-openai-key-recommended)). Fully local instead: `"Asr": { "Provider": "whisperx", "Url": "http://127.0.0.1:8011",
"LocalModule": "whisperx_server:app" }` and `"TextGenerator": { "Provider": "ollama", "Url": "http://127.0.0.1:11434/v1",
"Model": "avag-article" }`.

Every service has the same fields:

| Field | Meaning |
|---|---|
| `Provider` | provider name (table below); `none` (any case) switches the capability off; empty = the default provider when `Url`/`Path` is set |
| `Url` | base URL; for OpenAI-compatible endpoints including the version (`…/v1`) |
| `Model` | model name, where the provider needs one |
| `ApiKey` | for hosted endpoints, sent as `Authorization: Bearer` by every provider. Put real keys in environment variables (`AvAg__Services__TextGenerator__ApiKey`) or user secrets, not in the file |
| `Path` | input file for file-based providers (`whisperx-json`, `ui-json`) |
| `TimeoutSeconds` | per request; empty = provider default (sidecars 30 min, language models 10 min) |
| `LocalModule` | Python module the web app starts from `sidecars/` when the service is local and not running |
| `Fallback` | another service entry, used when this one fails (not reachable, no credit, invalid key, timeout, unusable answer); may have its own fallback |
| `Prompt` | speech recognition: product names and abbreviations to expect |
| `AlignUrl` | speech recognition: local WhisperX sidecar that aligns a cloud transcript to exact word times |
| `Vision` | text generation: the model reads images, so the article writer sends frames of the screen (never for private videos) |
| `ReasoningEffort` | text generation with reasoning models (gpt-5, o3 …): `minimal`, `low`, `medium`, `high` |

Only what you write is used: a section without `LocalModule` starts nothing, and a section with only a `Url` uses
the default provider. A capability you leave out entirely is off. The exception is speech recognition, which
falls back to WhisperX on `127.0.0.1:8011`. Every setting can also come from environment variables, e.g.
`AvAg__Services__Asr__Url=http://gpu-host:8011`.

### Replaceable AI services

The pipeline only knows six small interfaces (`src/AvAg.Core/Abstractions/AiServices.cs`). `AiServiceFactory`
(`src/AvAg.Pipeline/Services/`) picks the implementation by `Provider` name:

| Capability (config key) | Interface | Built-in providers |
|---|---|---|
| Speech recognition (`Asr`) | `IAsrService` | `whisperx` (default), `whisperx-json` (transcript file), `openai` (`gpt-4o-transcribe-diarize` up to 23 min, `whisper-1`; text-only models are refused) |
| UI elements + text (`UiParser`) | `IUiParser` | `openai-vision` (GPT vision around the click, shipped config), `omniparser` (default for a bare URL), `ui-json` (file) |
| Fallback pointing (`Grounder`) | `IVideoGrounder` | `molmo` (default), `qwen-vl` |
| Box tracking (`Tracker`) | `IObjectTracker` | `sam2` |
| Clip narratives (`ClipDescriber`) | `IClipDescriber` | `qwen-vl` (a Qwen-VL describer also serves as fallback pointing unless a grounder is configured) |
| Article writer (`TextGenerator`) | `ITextGenerator` | `openai-compatible` (aliases `ollama`, `openai`, `azure-openai`, `lm-studio`, `vllm`) |

**A supported AI on another server or with another model is configuration only.** For example, a cheaper OpenAI
model, or Azure OpenAI:

```json
"TextGenerator": { "Provider": "openai", "Url": "https://api.openai.com/v1", "Model": "gpt-5.4-mini" }
"TextGenerator": { "Provider": "azure-openai", "Url": "https://<resource>.openai.azure.com/openai/v1", "Model": "<deployment>" }
```

Reasoning models (gpt-5…, o3/o4) automatically get `max_completion_tokens`, no temperature and the
`ReasoningEffort`. If a server rejects a parameter, the request is repeated without it.

**A new AI** takes one class implementing the interface, one registration in `AiServiceFactory.CreateDefault()`,
and the provider name in the configuration. [`docs/ADDING_AN_AI.md`](docs/ADDING_AN_AI.md) has a complete
example. The prompt, the checks, the redaction, the screenshots and the Markdown stay the same.

WhisperX environment variables: `AVAG_WHISPER_MODEL` (`large-v3`, `large-v3-turbo`, `medium` …), `AVAG_WHISPER_DEVICE`
(`cuda`/`cpu`), `AVAG_WHISPER_COMPUTE`, `AVAG_WHISPER_BATCH`, `HF_TOKEN` (speaker labels).
Article model: `sidecars/article-llm.Modelfile` (`FROM qwen2.5:7b`, `num_gpu 0`, `num_ctx 12288`); after changing
it run `ollama create avag-article -f sidecars/article-llm.Modelfile` again.

---

## 10. Processes, ports and hardware

| Process | Port | Started by | Uses |
|---|---|---|---|
| OpenAI API | – (HTTPS) | per request | speech recognition and article (when configured and credited) |
| AvAg.Web | 5080 (localhost) | Visual Studio / `dotnet run` | CPU, RAM for frames during analysis |
| WhisperX (`sidecars/whisperx_server.py`) | 8011 | AvAg.Web, when a job needs it | word alignment for OpenAI transcripts (small), or full speech recognition as fallback (GPU ≈3 GB with `large-v3-turbo`, or CPU) |
| Ollama (`avag-article`) | 11434 | Windows autostart of Ollama | CPU only, ≈5.5 GB RAM while loaded (unloads after 5 idle minutes) |
| FFmpeg | – | short-lived, per step | CPU |

The WhisperX port is **8011**, not 8001, because some headset drivers (e.g. Sennheiser/Mitel `secomsdk.exe`)
occupy 127.0.0.1:8001. The web app starts without waiting for sidecars; a job waits for the services it needs
and restarts them if they stopped.

Tested on a laptop with a 4 GB NVIDIA RTX 500 Ada GPU and 64 GB RAM: a 1-minute call is analysed in seconds,
and the AI-written article takes about 30–60 s. A 2-minute 1080p screen recording takes about 1–2 min to
analyse and 2–3 min for the article.

---

## 11. Project layout and tests

```
src/AvAg.Core                      pure logic, no I/O
  Abstractions/AiServices.cs       the replaceable AI interfaces (IAsrService, ITextGenerator, IArticleWriter, …)
  Transcripts/                     TranscriptSentences (sentences with speakers, clean-up)
  Articles/                        WikiArticle (model), ArticleBuilder (rule-based draft + screenshot planning),
                                   PersonalDataRedactor, ArticleRenderer (Markdown, HTML preview), ArticlePackage (zip/folder)
  Models.cs, AudioRefParser.cs, UiElementRegistry.cs, Fusion.cs, Describer.cs, Metrics.cs
src/AvAg.Pipeline                  everything that touches files, processes or the network
  PipelineRunner.cs                the analysis, step by step
  Media/, Vision/                  FFmpeg, frame differencing, cursor tracking, click detection
  Adapters/<capability>/           one file per AI implementation + HttpServiceClient (shared client, keys, timeouts)
  Services/                        AiServicesOptions (configuration, overrides) + AiServiceFactory (provider registry)
  Articles/                        ArticleService (orchestration, fallback), LlmArticleWriter (prompt + answer parsing),
                                   ArticleScreenshotService (frame choice + extraction)
src/AvAg.Cli                       `avag run | eval | timeline`
src/AvAg.Web                       Program.cs (wiring), Endpoints/ (jobs, article), JobRunner, Jobs, SidecarLauncher,
                                   wwwroot/index.html
tests/AvAg.Tests                   NuGet-free test runner (28 tests)
tools/hooks/pre-commit             refuses commits containing API keys (git config core.hooksPath tools/hooks)
sidecars/                          whisperx_server.py, article-llm.Modelfile, optional molmo/omniparser/sam2 servers
deploy/                            docker-compose.yml for a Linux GPU host
samples/                           synthetic demo video, transcript, UI elements, ground truth
docs/                              ADDING_AN_AI.md, ROADMAP.md
```

Dependencies point one way: `Web`/`Cli` → `Pipeline` → `Core`. Core knows no model, file or service.

`dotnet run --project tests/AvAg.Tests` runs all tests (no network, no GPU):
* **Analysis:** parser, fusion, metrics, vision, and an end-to-end run on a synthetic FFmpeg recording.
* **Article:** a German support call yields the problem, error message, steps, click marker and verification,
  with small talk dropped. Markdown front matter, sections, support-only steps, the zip layout and key
  combinations are checked.
* **Privacy:** a private article never touches the video and contains no names. The redactor removes e-mail,
  phone, IBAN, customer numbers, names and companies, and leaves ordinary text alone.
* **Services:** providers are resolved by name (case-insensitive "none", defaults, aliases), a new AI can be
  registered, and a misconfiguration is reported instead of thrown. One set of override rules covers the web page
  and the CLI, and sidecar JSON is read as snake_case.
* **Model handling:** the language-model writer ties steps to transcript sentences (fake model). Observed steps
  the model drops are restored, and every kind of model failure falls back to the draft.
* **OpenAI, against fake HTTP answers in the documented formats:** the transcription request (diarized MP3,
  bearer key); diarized and word-timestamp answers; gpt-5 parameters; images; the retry when a parameter is
  rejected; "no credit" errors.
* **Fallbacks:** OpenAI → local switching, and no images for a text-only fallback model.
* **No API keys:** every file git would commit is scanned for API keys.

---

## 12. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| "A model service could not be reached (… 127.0.0.1:8011)" | WhisperX is not running and could not be started: check `sidecars/.venv` ([1.2](#12-speech-recognition-service-one-time)) and `sidecars/whisperx_server.log`, or upload a transcript JSON. |
| Wrong language in the transcript | Set the spoken language explicitly (`de`) or to Auto-detect. Swiss German is transcribed as standard German. |
| "GPU out of memory" / laptop freezes | `AVAG_WHISPER_BATCH=1`, `AVAG_WHISPER_MODEL=small` or `AVAG_WHISPER_DEVICE=cpu`. Never run two copies of the service. |
| Log says "openai not usable (… HTTP 429: You have no credits remaining …) – used whisperx" | The OpenAI account has no credit; the local fallback did the work. Add credit at platform.openai.com → Billing. |
| "HTTP 401" / "needs an API key" | Key missing or wrong: `dotnet user-secrets list --project src/AvAg.Web` (web, Development) or `OPENAI_API_KEY` (CLI). |
| "word alignment not available – word times estimated" | The local WhisperX sidecar wasn't running for `/align`. The article still works; click matching is less precise. |
| Article says "rule-based article" / "kept the rule-based article" | The reason is in the status line: no model reachable (OpenAI without credit and Ollama not running – `ollama list` needs `avag-article`), a typo in `Services:TextGenerator`, a timeout, or an unusable answer. |
| Article is too thin / misses a step | The rule-based draft only knows typical phrasings; the model writes better articles. Steps with an observed click are never lost. Long calls with many detours benefit from a larger model. |
| Personal data still in the article | The redactor only finds what has a recognisable shape. Review drafts (`status: draft`), and use private mode when the screen shows customer data. |
| Build error "file is locked by AvAg.Web" | Stop the running web app (Visual Studio: Stop debugging) and build again. |

---

## 13. Advanced: more model services, evaluation

### 13.1 Optional model services (Linux GPU host recommended)

```bash
cd sidecars
uvicorn omniparser_server:app --port 8003 &   # UI elements + OCR: OMNIPARSER_HOME=/opt/OmniParser, AVAG_OCR_LANG=german
uvicorn molmo_server:app      --port 8002 &   # AI pointing fallback: AVAG_MOLMO_MODEL=allenai/Molmo2-VideoPoint-4B
uvicorn sam2_server:app       --port 8004 &   # box tracking: SAM2_CFG / SAM2_CKPT
vllm serve Qwen/Qwen3-VL-8B-Instruct --port 8005 --allowed-local-media-path /data/videos
```

Or all at once with `cd deploy && HF_TOKEN=... docker compose up -d`. With OmniParser the clicked button is named
in the events ("Speichern"), and screenshots get a box around the element.

### 13.2 Cursor template

`--cursor` is a tight PNG crop of the recorded mouse pointer. Without it the tracker uses moving-blob detection.

### 13.3 Event graph format

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

### 13.4 Evaluation and calibration

Annotate videos in the format of `samples/demo_groundtruth.json` and run `avag eval --pred out/x.json --gt gt/x.json`.
It reports AVAGA@250ms (speaker ∧ phrase ∧ action ∧ |Δt| ≤ 250 ms ∧ point ∈ target), phrase recall, action
accuracy, time success, point-hit accuracy, pixel distances and factual event precision. `FusionConfig` and the
detector thresholds are starting values to tune on your own recordings. See `docs/ROADMAP.md`.

---

## 14. Licensing

This repository: Apache-2.0 (`LICENSE`). Check the licence of each model before commercial use:

| Component | Licence |
|---|---|
| WhisperX | BSD-2-Clause; faster-whisper MIT; pyannote models have their own terms |
| Qwen2.5 (article writer), Qwen3-VL, Molmo2, SAM2, PaddleOCR | Apache-2.0 (check datasets/checkpoints) |
| Ollama | MIT |
| OmniParser | repo CC-BY-4.0; current detector weights MIT, older Ultralytics weights AGPL – **check** |
