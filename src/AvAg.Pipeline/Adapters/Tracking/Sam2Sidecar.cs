using AvAg.Core;

namespace AvAg.Pipeline.Adapters;

/// <summary>Provider "sam2": sidecars/sam2_server.py — POST /track {video_path, seed_time_s, point, box, start_s, end_s} → {samples:[{time_s,bbox,confidence}]}.</summary>
public sealed class Sam2Sidecar : HttpServiceClient, IObjectTracker
{
    public Sam2Sidecar(string baseUrl, string? apiKey = null, TimeSpan? timeout = null) : base(baseUrl, apiKey, timeout) { }

    private sealed record S(double TimeS, int[] Bbox, double Confidence);
    private sealed record Res(List<S> Samples);

    public async Task<IReadOnlyList<TrackSample>> TrackAsync(string videoPath, double seedTimeS, Point2D? seedPoint, BBox? seedBox, double startS, double endS, CancellationToken ct = default)
    {
        var res = await PostAsync<Res>("track", new
        {
            video_path = Path.GetFullPath(videoPath), seed_time_s = seedTimeS,
            point = seedPoint is null ? null : new[] { seedPoint.X, seedPoint.Y },
            box = seedBox?.ToArray(), start_s = startS, end_s = endS,
        }, ct);
        return res.Samples.Select(s => new TrackSample(s.TimeS, new BBox(s.Bbox[0], s.Bbox[1], s.Bbox[2], s.Bbox[3]), s.Confidence)).ToList();
    }
}
