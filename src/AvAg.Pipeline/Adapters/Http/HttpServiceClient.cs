using System.Net.Http.Json;

namespace AvAg.Pipeline.Adapters;

/// <summary>
/// Base for adapters that call a model service over HTTP with JSON (the Python sidecars, vLLM, …).
/// Errors carry the service name, route and response body so a failing service is easy to identify.
/// </summary>
public abstract class HttpServiceClient
{
    protected readonly HttpClient Http;
    protected readonly Uri BaseUri;

    protected HttpServiceClient(string baseUrl, HttpClient? http = null, TimeSpan? timeout = null)
    {
        BaseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        Http = http ?? new HttpClient { Timeout = timeout ?? TimeSpan.FromMinutes(30) };
    }

    protected async Task<TRes> PostAsync<TReq, TRes>(string route, TReq body, CancellationToken ct)
    {
        using var resp = await Http.PostAsJsonAsync(new Uri(BaseUri, route), body, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{GetType().Name} {route} → {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
        return await resp.Content.ReadFromJsonAsync<TRes>(cancellationToken: ct) ?? throw new InvalidOperationException($"{GetType().Name}: empty response");
    }
}
