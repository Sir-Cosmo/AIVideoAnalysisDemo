using AvAg.Core;

namespace AvAg.Pipeline.Adapters;

/// <summary>Provider "molmo": sidecars/molmo_server.py — POST /point {video_path, prompt, start_s, end_s} → {points:[{object_id,time_s,x,y,confidence,label}]}.</summary>
public sealed class MolmoPointSidecar : HttpServiceClient, IVideoGrounder
{
    public MolmoPointSidecar(string baseUrl, string? apiKey = null, TimeSpan? timeout = null) : base(baseUrl, apiKey, timeout) { }

    private sealed record Res(List<VideoPoint> Points);

    public async Task<IReadOnlyList<VideoPoint>> PointAsync(string videoPath, string prompt, double startS, double endS, CancellationToken ct = default) =>
        (await PostAsync<Res>("point", new { video_path = Path.GetFullPath(videoPath), prompt, start_s = startS, end_s = endS }, ct)).Points;
}
