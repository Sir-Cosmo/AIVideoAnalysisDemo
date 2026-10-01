using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AvAg.Core;

namespace AvAg.Pipeline.Adapters;

/// <summary>
/// Provider "openai-compatible": any endpoint that speaks the OpenAI chat-completions API – Ollama, LM Studio, vLLM,
/// llama.cpp server, OpenAI, Azure OpenAI (v1 API) and many hosted vendors.
/// <paramref name="baseUrl"/> ends with the API version, e.g. <c>http://127.0.0.1:11434/v1</c>.
/// </summary>
public sealed class OpenAiCompatibleTextGenerator : ITextGenerator
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string? _apiKey;

    public OpenAiCompatibleTextGenerator(string baseUrl, string? model, string? apiKey = null, TimeSpan? timeout = null, HttpClient? http = null)
    {
        _endpoint = baseUrl.TrimEnd('/') + "/chat/completions";
        _model = model ?? "";
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _http = http ?? new HttpClient { Timeout = timeout ?? TimeSpan.FromMinutes(15) };
    }

    public string Name => string.IsNullOrEmpty(_model) ? "openai-compatible" : _model;

    public async Task<string> GenerateAsync(TextGenerationRequest r, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        if (_apiKey is not null)
        {
            req.Headers.Add("Authorization", "Bearer " + _apiKey);
            req.Headers.Add("api-key", _apiKey); // Azure OpenAI
        }
        var body = new JsonObject
        {
            ["model"] = _model,
            ["temperature"] = r.Temperature,
            ["max_tokens"] = r.MaxTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = r.System },
                new JsonObject { ["role"] = "user", ["content"] = r.User }),
        };
        if (r.Json) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        req.Content = JsonContent.Create(body);

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{Name}: HTTP {(int)resp.StatusCode}: {text[..Math.Min(text.Length, 300)]}");
        return JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
               ?? throw new InvalidOperationException($"{Name}: empty answer");
    }
}
