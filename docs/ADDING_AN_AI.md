# Using a different AI

AvAg uses AI for six capabilities. The pipeline only knows their interfaces
(`src/AvAg.Core/Abstractions/AiServices.cs`); which implementation runs is decided by a **provider name** in the
configuration. That gives you three ways to change the AI, from least to most work.

| Capability | Config key | Interface | What it must return |
|---|---|---|---|
| Speech recognition | `Asr` | `IAsrService` | `Transcript`: language, segments, **words with start/end times** |
| UI elements + text | `UiParser` | `IUiParser` | boxes of UI elements in a frame, with text and confidences |
| Fallback pointing | `Grounder` | `IVideoGrounder` | points (time, x, y, confidence) – always tagged *inferred* |
| Box tracking | `Tracker` | `IObjectTracker` | boxes over time for a seed box/point |
| Clip narratives | `ClipDescriber` | `IClipDescriber` | free text about a clip |
| Manual writer | `TextGenerator` | `ITextGenerator` | text for a system + user prompt (optionally JSON) |

---

## 1. Same API, different model or server: configuration only

The `openai-compatible` text provider works with every server that speaks the OpenAI chat-completions API: Ollama,
LM Studio, vLLM, llama.cpp server, OpenAI, Azure OpenAI (v1 API) and many hosted vendors. Change
`src/AvAg.Web/appsettings.json`:

```json
"TextGenerator": { "Provider": "openai-compatible", "Url": "http://127.0.0.1:1234/v1", "Model": "my-model" }
```

On the CLI: `--llm-provider openai-compatible --llm-url http://127.0.0.1:1234/v1 --llm-model my-model`.
Put API keys in an environment variable instead of the file: `AvAg__Services__TextGenerator__ApiKey=...`.

The speech and vision services work the same way: point `Url` at another machine (e.g. a GPU server running the
sidecars from `deploy/docker-compose.yml`).

## 2. Different model behind the same HTTP contract: a new sidecar, no C# changes

Each Python sidecar in `sidecars/` is a small HTTP service. Any service that answers with the same JSON can replace
it; only `Url` changes.

| Provider | Request | Response |
|---|---|---|
| `whisperx` | `POST /transcribe {audio_path, language, diarize}` | `{language, segments:[{start, end, text, speaker, words:[{word, start, end, score, speaker}]}]}` |
| `omniparser` | `POST /parse {image_path}` | `{elements:[{bbox:[x1,y1,x2,y2], text, text_confidence, interactive_confidence, class}]}` |
| `molmo` | `POST /point {video_path, prompt, start_s, end_s}` | `{points:[{object_id, time_s, x, y, confidence, label}]}` |
| `sam2` | `POST /track {video_path, seed_time_s, point, box, start_s, end_s}` | `{samples:[{time_s, bbox, confidence}]}` |

Paths are absolute local file paths: the sidecar must run on the same machine, or see the same files.

Example: to use another speech recogniser, write a FastAPI app with `POST /transcribe` that returns the WhisperX
shape above, start it on a free port, and set `"Asr": { "Provider": "whisperx", "Url": "http://127.0.0.1:<port>" }`.
If the web app should start it automatically, also set `"LocalModule": "<file>:app"` and put the file in `sidecars/`.

## 3. A different API: one C# class + one registration

When the AI has its own API, write an adapter. Example: a text generator for a vendor with a "messages" API.

**Step 1 – implement the interface** (`src/AvAg.Pipeline/Adapters/TextGeneration/MyVendorTextGenerator.cs`):

```csharp
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AvAg.Core;

namespace AvAg.Pipeline.Adapters;

/// <summary>Provider "my-vendor": the vendor's messages API.</summary>
public sealed class MyVendorTextGenerator : ITextGenerator
{
    private readonly HttpClient _http;
    private readonly string _url, _model, _apiKey;

    public MyVendorTextGenerator(string url, string model, string apiKey, TimeSpan timeout)
    {
        _url = url; _model = model; _apiKey = apiKey;
        _http = new HttpClient { Timeout = timeout };
    }

    public string Name => _model;

    public async Task<string> GenerateAsync(TextGenerationRequest r, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _url);
        req.Headers.Add("x-api-key", _apiKey);
        req.Content = JsonContent.Create(new
        {
            model = _model,
            max_tokens = r.MaxTokens,
            temperature = r.Temperature,
            system = r.System,
            messages = new[] { new { role = "user", content = r.User } },
        });
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{Name}: HTTP {(int)resp.StatusCode}: {body}");
        // Return only the generated text; LlmManualWriter finds and checks the JSON itself.
        return JsonNode.Parse(body)?["content"]?[0]?["text"]?.GetValue<string>() ?? throw new InvalidOperationException($"{Name}: empty answer");
    }
}
```

**Step 2 – register a provider name** in `AiServiceFactory.CreateDefault()`
(`src/AvAg.Pipeline/Services/AiServiceFactory.cs`):

```csharp
f.TextGenerator.Register("my-vendor",
    o => new MyVendorTextGenerator(f.TextGenerator.RequireUrl(o), o.Model ?? "", o.ApiKey ?? "", o.Timeout));
```

(Or register it only in the web app, in `src/AvAg.Web/Program.cs`, on the factory instance.)

**Step 3 – select it:**

```json
"TextGenerator": { "Provider": "my-vendor", "Url": "https://api.example.com/v1/messages", "Model": "<model>" }
```

…and set the key with `AvAg__Services__TextGenerator__ApiKey`. On the CLI:
`--llm-provider my-vendor --llm-url … --llm-model … --llm-key …`.

The manual's prompt, the JSON checks, the anchoring of steps to transcript sentences, the fallback to the rule-based
manual and the screenshots stay exactly the same – they live in `LlmManualWriter` and `ManualService`, not in the
adapter.

### Other capabilities

The same three steps apply. For speech recognition, for example: implement `IAsrService.TranscribeAsync` (the WAV
file is 16 kHz mono; return words with start/end times, because the click matching depends on them), register it
with `f.Asr.Register("my-asr", o => new MyAsr(...))` and set `"Asr": { "Provider": "my-asr", ... }`. If your service
already returns WhisperX-style JSON, reuse `WhisperXMapper.ToTranscript`.

### Replacing the manual writer entirely

`IManualWriter` is the level above `ITextGenerator`. Implement it when a different approach should write the
manual – e.g. a multimodal model that also looks at the screenshots – and pass it to `ManualService`. Contract:
return steps whose `TimeS`/`EndS` come from real transcript sentences, and throw `InvalidOperationException` when
you cannot produce a usable manual; `ManualService` then keeps the rule-based draft.

## Checklist

- [ ] The adapter only translates between the interface and the AI's API – no pipeline logic in it.
- [ ] Errors throw `InvalidOperationException` or `HttpRequestException` with the provider name in the message.
- [ ] No API keys in code or in committed `appsettings.json`.
- [ ] A test with a fake implementation if the adapter has its own parsing (see `FakeTextGenerator` in
      `tests/AvAg.Tests/Program.cs`).
- [ ] `dotnet run --project tests/AvAg.Tests` passes.
