using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvAg.Core;

namespace AvAg.Pipeline.Adapters;

/// <summary>
/// Provider "openai-compatible" (aliases "openai", "ollama", "azure-openai", "lm-studio", "vllm"): any endpoint that
/// speaks the OpenAI chat-completions API. <paramref name="baseUrl"/> ends with the API version, e.g.
/// <c>https://api.openai.com/v1</c> or <c>http://127.0.0.1:11434/v1</c>.
/// <list type="bullet">
/// <item>Reasoning models (gpt-5…, o1/o3/o4…) get <c>max_completion_tokens</c>, no temperature and the configured
/// <c>reasoning_effort</c>; other models <c>max_tokens</c> and <c>temperature</c>. If a server still rejects one of these
/// parameters, the request is repeated without it.</item>
/// <item>With <c>vision</c>, images are sent as data URLs after the text.</item>
/// <item>Every failure (HTTP error, timeout, unreadable answer) surfaces as an exception the article service falls back on.</item>
/// </list>
/// </summary>
public sealed class OpenAiCompatibleTextGenerator : ITextGenerator
{
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string? _apiKey, _reasoningEffort;
    private readonly bool _vision;
    private readonly TimeSpan _timeout;
    private readonly HttpClient _http;

    public OpenAiCompatibleTextGenerator(string baseUrl, string? model, string? apiKey = null, TimeSpan? timeout = null,
                                         bool vision = false, string? reasoningEffort = null, HttpClient? http = null)
    {
        _endpoint = new Uri(baseUrl.TrimEnd('/') + "/chat/completions");
        _model = model ?? "";
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        _vision = vision;
        _reasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort;
        _http = http ?? HttpServiceClient.Shared;
    }

    public string Name => string.IsNullOrEmpty(_model) ? "openai-compatible" : _model;
    public bool SupportsImages => _vision;

    /// <summary>OpenAI reasoning models take different sampling parameters.</summary>
    private bool Reasoning => _model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) && !_model.Contains("chat", StringComparison.OrdinalIgnoreCase)
                              || _model.Length > 1 && _model[0] is 'o' or 'O' && char.IsDigit(_model[1]);

    private sealed record Shape(bool CompletionTokens, bool Temperature, bool Effort, bool JsonMode);

    public async Task<string> GenerateAsync(TextGenerationRequest r, CancellationToken ct = default)
    {
        var shape = new Shape(CompletionTokens: Reasoning, Temperature: !Reasoning, Effort: Reasoning && _reasoningEffort is not null, JsonMode: r.Json);
        for (int attempt = 0; ; attempt++)
        {
            var (status, text) = await SendAsync(Body(r, shape), ct);
            if (status < 300) return Content(text);
            // A parameter this server does not accept: adapt once per parameter and try again.
            string error = HttpServiceClient.ErrorMessage(text);
            Shape? adapted = status == 400 && attempt < 4 ? Adapt(shape, error) : null;
            if (adapted is null || adapted == shape)
                throw new HttpRequestException($"{Name}: HTTP {status}: {error}", null, (System.Net.HttpStatusCode)status);
            shape = adapted;
        }
    }

    private static Shape? Adapt(Shape s, string error)
    {
        string e = error.ToLowerInvariant();
        if (e.Contains("max_tokens") && !s.CompletionTokens) return s with { CompletionTokens = true };
        if (e.Contains("max_completion_tokens") && s.CompletionTokens) return s with { CompletionTokens = false };
        if (e.Contains("temperature") && s.Temperature) return s with { Temperature = false };
        if (e.Contains("reasoning_effort") && s.Effort) return s with { Effort = false };
        if (e.Contains("response_format") && s.JsonMode) return s with { JsonMode = false };
        return null;
    }

    private JsonObject Body(TextGenerationRequest r, Shape s)
    {
        JsonNode user = r.User;
        if (_vision && r.Images.Count > 0)
        {
            var parts = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = r.User });
            foreach (var img in r.Images)
            {
                parts.Add(new JsonObject { ["type"] = "text", ["text"] = img.Label });
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(img.Jpeg), ["detail"] = "high" },
                });
            }
            user = parts;
        }
        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = r.System },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        // Reasoning models spend tokens on thinking before they answer: give them room.
        if (s.CompletionTokens) body["max_completion_tokens"] = Reasoning ? Math.Max(r.MaxTokens * 4, 16000) : r.MaxTokens;
        else body["max_tokens"] = r.MaxTokens;
        if (s.Temperature) body["temperature"] = r.Temperature;
        if (s.Effort) body["reasoning_effort"] = _reasoningEffort;
        if (s.JsonMode) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        return body;
    }

    private async Task<(int Status, string Text)> SendAsync(JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = JsonContent.Create(body) };
        if (_apiKey is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Headers.Add("api-key", _apiKey); // Azure OpenAI
        }
        return await HttpServiceClient.SendAsync(_http, req, _timeout, Name, ct);
    }

    private string Content(string text)
    {
        JsonNode? choice;
        string? content;
        try
        {
            choice = JsonNode.Parse(text)?["choices"]?[0];
            content = choice?["message"]?["content"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)   // not JSON / content is not a string
        {
            throw new InvalidOperationException($"{Name}: answer is not an OpenAI-style response: {text[..Math.Min(text.Length, 300)]}", ex);
        }
        if (!string.IsNullOrWhiteSpace(content)) return content;
        string reason = choice?["finish_reason"]?.GetValue<string>() ?? "unknown";
        throw new InvalidOperationException($"{Name}: empty answer (finish_reason: {reason})");
    }
}
