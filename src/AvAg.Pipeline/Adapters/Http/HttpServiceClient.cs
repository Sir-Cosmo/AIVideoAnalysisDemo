using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvAg.Core;

namespace AvAg.Pipeline.Adapters;

/// <summary>
/// Base for adapters that call a model service over HTTP with JSON (the Python sidecars, vLLM, …).
/// <list type="bullet">
/// <item>One shared <see cref="HttpClient"/> for all adapters (no socket leaks); the timeout is applied per request.</item>
/// <item>JSON in both directions uses <see cref="Json.Options"/> (snake_case), matching the sidecars' wire format.</item>
/// <item>An API key, if configured, is sent as "Authorization: Bearer".</item>
/// <item>Errors name the adapter and route; a timeout becomes a <see cref="TimeoutException"/>.</item>
/// </list>
/// </summary>
public abstract class HttpServiceClient
{
    /// <summary>Shared by all adapters. Its own timeout is infinite – each request carries its own.</summary>
    internal static readonly HttpClient Shared = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    protected readonly Uri BaseUri;
    private readonly string? _apiKey;
    private readonly TimeSpan _timeout;

    protected HttpServiceClient(string baseUrl, string? apiKey = null, TimeSpan? timeout = null)
    {
        BaseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _timeout = timeout ?? TimeSpan.FromMinutes(30);
    }

    /// <summary>POST JSON to <paramref name="route"/> (relative to the base URL) and read a JSON answer.</summary>
    protected async Task<TRes> PostAsync<TRes>(string route, object body, CancellationToken ct) =>
        JsonSerializer.Deserialize<TRes>(await PostForStringAsync(route, body, ct), Json.Options)
        ?? throw new InvalidOperationException($"{GetType().Name} {route}: empty response");

    /// <summary>POST JSON and return the raw answer body.</summary>
    protected async Task<string> PostForStringAsync(string route, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, route))
        {
            Content = JsonContent.Create(body, body.GetType(), options: Json.Options),
        };
        if (_apiKey is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var (status, text) = await SendAsync(Shared, req, _timeout, $"{GetType().Name} {route}", ct);
        if (status >= 300) throw new HttpRequestException($"{GetType().Name} {route} → {status}: {text[..Math.Min(text.Length, 500)]}", null, (System.Net.HttpStatusCode)status);
        return text;
    }

    /// <summary>Sends <paramref name="req"/> with its own timeout and returns status and body; a timeout becomes a
    /// <see cref="TimeoutException"/> naming <paramref name="what"/>. Shared by all HTTP adapters.</summary>
    internal static async Task<(int Status, string Text)> SendAsync(HttpClient http, HttpRequestMessage req, TimeSpan timeout, string what, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var resp = await http.SendAsync(req, cts.Token);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(cts.Token));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{what} did not answer within {timeout.TotalMinutes:0.#} min.");
        }
    }

    /// <summary>The message of an OpenAI-style error answer (<c>{"error":{"message":…}}</c>), else the start of the body.</summary>
    internal static string ErrorMessage(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? body[..Math.Min(body.Length, 300)]; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return body[..Math.Min(body.Length, 300)]; }
    }
}
