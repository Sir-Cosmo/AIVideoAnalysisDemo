using System.Text.Json;
using AvAg.Core;

namespace AvAg.Pipeline.Adapters;

/// <summary>Wire format shared by the OmniParser sidecar and UI-elements JSON files.</summary>
internal sealed record UiElementsJson(List<UiElementsJson.El> Elements)
{
    internal sealed record El(int[] Bbox, string? Text, double TextConfidence, double InteractiveConfidence, string? Class);

    public IReadOnlyList<UiElementRegistry.Detection> ToDetections() => Elements.Select(e => new UiElementRegistry.Detection(
        new BBox(e.Bbox[0], e.Bbox[1], e.Bbox[2], e.Bbox[3]), e.Text, e.TextConfidence, e.InteractiveConfidence, e.Class ?? "unknown")).ToList();
}

/// <summary>Provider "omniparser": sidecars/omniparser_server.py — POST /parse {image_path} → {elements:[{bbox,text,text_confidence,interactive_confidence,class}]}.</summary>
public sealed class OmniParserSidecar : HttpServiceClient, IUiParser
{
    public OmniParserSidecar(string baseUrl, string? apiKey = null, TimeSpan? timeout = null) : base(baseUrl, apiKey, timeout) { }

    public async Task<IReadOnlyList<UiElementRegistry.Detection>> ParseAsync(string framePngPath, CancellationToken ct = default) =>
        (await PostAsync<UiElementsJson>("parse", new { image_path = Path.GetFullPath(framePngPath) }, ct)).ToDetections();
}

/// <summary>Provider "ui-json": the same UI elements for every frame, read from a JSON file (tests, golden runs).</summary>
public sealed class JsonFileUiParser : IUiParser
{
    private readonly string _path;
    public JsonFileUiParser(string path) => _path = path;

    public async Task<IReadOnlyList<UiElementRegistry.Detection>> ParseAsync(string framePngPath, CancellationToken ct = default) =>
        (JsonSerializer.Deserialize<UiElementsJson>(await File.ReadAllTextAsync(_path, ct), Json.Options) ?? new UiElementsJson(new())).ToDetections();
}

/// <summary>Provider "none": no UI parsing – events keep their coordinates but get no element name.</summary>
public sealed class NullUiParser : IUiParser
{
    public Task<IReadOnlyList<UiElementRegistry.Detection>> ParseAsync(string framePngPath, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<UiElementRegistry.Detection>>(Array.Empty<UiElementRegistry.Detection>());
}
