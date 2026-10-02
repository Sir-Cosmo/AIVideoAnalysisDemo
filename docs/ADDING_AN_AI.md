# Using a different AI

AvAg uses AI for six capabilities. The pipeline only knows their interfaces
(`src/AvAg.Core/Abstractions/AiServices.cs`). Which implementation runs is decided by a **provider name** in the
configuration, so there are three ways to change the AI, from least to most work.

| Capability | Config key | Interface | What it must return |
|---|---|---|---|
| Speech recognition | `Asr` | `IAsrService` | `Transcript`: language, segments, **words with start/end times** (+ speaker labels if possible) |
| UI elements + text | `UiParser` | `IUiParser` | boxes of UI elements in a frame, with text and confidences |
| Fallback pointing | `Grounder` | `IVideoGrounder` | points (time, x, y, confidence), always tagged *inferred* |
| Box tracking | `Tracker` | `IObjectTracker` | boxes over time for a seed box/point |
| Clip narratives | `ClipDescriber` | `IClipDescriber` | free text about a clip |
| Article writer | `TextGenerator` | `ITextGenerator` | text for a system + user prompt (optionally JSON) |

> **Data protection:** with the default configuration nothing leaves the machine. A hosted model receives the call
> transcript, which contains customer data. Check that this is allowed before you configure one.

---

## 1. Same API, different model or server: configuration only

The `openai-compatible` text provider works with every server that speaks the OpenAI chat-completions API: Ollama,
LM Studio, vLLM, llama.cpp server, OpenAI, Azure OpenAI (v1 API) and many hosted vendors. In
`src/AvAg.Web/appsettings.json`:

```json
"TextGenerator": { "Provider": "openai-compatible", "Url": "http://127.0.0.1:1234/v1", "Model": "my-model" }
```

On the CLI: `--llm-provider openai-compatible --llm-url http://127.0.0.1:1234/v1 --llm-model my-model`.

API keys go into an environment variable rather than the file: `AvAg__Services__TextGenerator__ApiKey=...`. Every
HTTP provider sends a configured key as `Authorization: Bearer`.

The speech and vision services work the same way: point `Url` at another machine, e.g. a GPU server running the
sidecars from `deploy/docker-compose.yml`. OpenAI speech recognition is the built-in provider `openai` for `Asr`.
Use `gpt-4o-transcribe-diarize` (speaker labels) or `whisper-1` (word times), plus `AlignUrl` for exact word times.

Every service can have a `Fallback` entry that is used when it fails, for example OpenAI first and the local model
when the account has no credit:

```json
"TextGenerator": { "Provider": "openai", "Url": "https://api.openai.com/v1", "Model": "gpt-5.5",
                   "Fallback": { "Provider": "ollama", "Url": "http://127.0.0.1:11434/v1", "Model": "avag-article" } }
```

## 2. Different model behind the same HTTP contract: a new sidecar, no C# changes

Each Python sidecar in `sidecars/` is a small HTTP service. Any service that returns the same JSON (snake_case)
can replace it; only `Url` changes.

| Provider | Request | Response |
|---|---|---|
| `whisperx` | `POST /transcribe {audio_path, language, diarize}` | `{language, segments:[{start, end, text, speaker, words:[{word, start, end, score, speaker}]}]}` |
| `omniparser` | `POST /parse {image_path}` | `{elements:[{bbox:[x1,y1,x2,y2], text, text_confidence, interactive_confidence, class}]}` |
| `molmo` | `POST /point {video_path, prompt, start_s, end_s}` | `{points:[{object_id, time_s, x, y, confidence, label}]}` |
| `sam2` | `POST /track {video_path, seed_time_s, point, box, start_s, end_s}` | `{samples:[{time_s, bbox, confidence}]}` |

Paths are absolute local file paths, so the sidecar must run on the same machine or see the same files.

Example: to use another speech recogniser, write a FastAPI app with `POST /transcribe` that returns the WhisperX
shape above, start it on a free port, and set `"Asr": { "Provider": "whisperx", "Url": "http://127.0.0.1:<port>" }`.
If the web app should start it automatically, also set `"LocalModule": "<file>:app"` and put the file in `sidecars/`.
Without `LocalModule` the web app never starts anything for that service.

## 3. A different API: one C# class + one registration

When the AI has its own API, write an adapter. Example: a text generator for a vendor with a "messages" API.

**Step 1: implement the interface** (`src/AvAg.Pipeline/Adapters/TextGeneration/MyVendorTextGenerator.cs`):

```csharp
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AvAg.Core;

namespace AvAg.Pipeline.Adapters;

/// <summary>Provider "my-vendor": the vendor's messages API.</summary>
public sealed class MyVendorTextGenerator : ITextGenerator
{
    // One HttpClient for the whole process (a new one per request leaks sockets); the timeout is applied per request.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _url, _model, _apiKey;
    private readonly TimeSpan _timeout;

    public MyVendorTextGenerator(string url, string model, string apiKey, TimeSpan timeout)
    {
        _url = url; _model = model; _apiKey = apiKey; _timeout = timeout;
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
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        using var resp = await Http.SendAsync(req, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{Name}: HTTP {(int)resp.StatusCode}: {body}");
        // Return only the generated text; LlmArticleWriter finds and checks the JSON itself.
        return JsonNode.Parse(body)?["content"]?[0]?["text"]?.GetValue<string>() ?? throw new InvalidOperationException($"{Name}: empty answer");
    }
}
```

**Step 2: register a provider name** in `AiServiceFactory.CreateDefault()`
(`src/AvAg.Pipeline/Services/AiServiceFactory.cs`):

```csharp
f.TextGenerator.Register("my-vendor",
    o => new MyVendorTextGenerator(f.TextGenerator.RequireUrl(o), o.Model ?? "", o.ApiKey ?? "", f.TextGenerator.Timeout(o)));
```

You can also register it only in the web app, on the factory instance in `src/AvAg.Web/Program.cs`.
`RequireUrl` gives a clear error when the URL is missing. `Timeout(o)` uses `TimeoutSeconds` or the capability's
default.

**Step 3: select it:**

```json
"TextGenerator": { "Provider": "my-vendor", "Url": "https://api.example.com/v1/messages", "Model": "<model>" }
```

Then set the key with `AvAg__Services__TextGenerator__ApiKey`. On the CLI:
`--llm-provider my-vendor --llm-url … --llm-model … --llm-key …`.

None of the article logic changes. It lives in `LlmArticleWriter` and `ArticleService`, not in the adapter:
* the prompt and the JSON checks;
* the anchoring of steps to transcript sentences and the restoring of observed steps;
* the redaction of personal data, the screenshots and the fallback to the rule-based article.

### Errors

The adapter does not have to handle failures gracefully. `ArticleService` catches anything the writer throws
(HTTP error, timeout, unreadable answer, a bug) and keeps the rule-based article. The reason is shown in the log
and on the page. A wrong configuration (unknown provider, missing URL) is reported the same way.

### Other capabilities

The same three steps apply. For speech recognition, for example:
1. Implement `IAsrService.TranscribeAsync`. The WAV is 16 kHz mono. Return words with start/end times, because the
   click matching depends on them, and speaker labels if your service has them.
2. Register it with `f.Asr.Register("my-asr", o => new MyAsr(...))`.
3. Set `"Asr": { "Provider": "my-asr", ... }`.

If your service already returns WhisperX-style JSON, reuse `WhisperXMapper.ToTranscript`. HTTP services can derive
from `HttpServiceClient`, which gives them the shared client, the API key header, snake_case JSON and timeouts.

### Replacing the article writer entirely

`IArticleWriter` is the level above `ITextGenerator`. Implement it when a different approach should write the
article, e.g. a multimodal model that also looks at the screenshots, and pass it to `ArticleService`. The
contract:
* return steps whose `TimeS`/`EndS` come from real transcript sentences;
* mark support-only steps with `Actor = StepActor.Support`;
* throw when you cannot produce a usable article. `ArticleService` then keeps the rule-based draft.

## Checklist

- [ ] The adapter only translates between the interface and the AI's API, with no article or pipeline logic.
- [ ] One shared `HttpClient`, with the timeout applied per request.
- [ ] Errors throw (`HttpRequestException`, `InvalidOperationException`, `TimeoutException`) with the provider name in the message.
- [ ] No API keys in code or in committed `appsettings.json`.
- [ ] Customer data may only go to services that are allowed to process it.
- [ ] A test with a fake implementation if the adapter does its own parsing (see `FakeTextGenerator` in
      `tests/AvAg.Tests/Program.cs`).
- [ ] `dotnet run --project tests/AvAg.Tests` passes.
