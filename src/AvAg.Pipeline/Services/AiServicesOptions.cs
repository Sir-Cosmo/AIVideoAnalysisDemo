namespace AvAg.Pipeline.Services;

/// <summary>
/// Configuration of one AI capability: which provider implements it and how to reach it.
/// Bound from appsettings.json (<c>AvAg:Services:&lt;Capability&gt;</c>) or built from CLI flags.
/// </summary>
public sealed class ServiceOptions
{
    /// <summary>Provider name registered in <see cref="AiServiceFactory"/> (e.g. "whisperx", "openai-compatible").
    /// Empty: the capability's default provider if <see cref="Url"/> or <see cref="Path"/> is set, otherwise "none".
    /// "none" switches the capability off.</summary>
    public string? Provider { get; set; }
    /// <summary>Base URL of an HTTP service. For OpenAI-compatible endpoints include the version, e.g. http://127.0.0.1:11434/v1.</summary>
    public string? Url { get; set; }
    public string? Model { get; set; }
    /// <summary>Only for hosted endpoints. Prefer environment variables / user secrets over appsettings.json for real keys.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Input file for file-based providers ("whisperx-json", "ui-json").</summary>
    public string? Path { get; set; }
    public int TimeoutSeconds { get; set; } = 1800;
    /// <summary>Python module of a local sidecar ("whisperx_server:app"); the web app starts it when <see cref="Url"/> is local and not running.</summary>
    public string? LocalModule { get; set; }

    public const string None = "none";

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Max(1, TimeoutSeconds));

    /// <summary>A copy with another URL – used when a request overrides the configured endpoint. A capability that was
    /// switched off is switched on with its default provider.</summary>
    public ServiceOptions WithUrl(string? url) => string.IsNullOrWhiteSpace(url) ? this : Copy(o =>
    {
        o.Url = url;
        if (string.Equals(o.Provider, None, StringComparison.OrdinalIgnoreCase)) o.Provider = null;
    });

    /// <summary>Options that read from a local file with a file-based provider.</summary>
    public static ServiceOptions FromFile(string provider, string path) => new() { Provider = provider, Path = path };

    public static ServiceOptions Off() => new() { Provider = None };

    private ServiceOptions Copy(Action<ServiceOptions> change)
    {
        var o = (ServiceOptions)MemberwiseClone();
        change(o);
        return o;
    }
}

/// <summary>All replaceable AI capabilities of the pipeline (see AvAg.Core/Abstractions/AiServices.cs).</summary>
public sealed class AiServicesOptions
{
    public const string LocalAsrUrl = "http://127.0.0.1:8011";

    /// <summary>Speech recognition – <see cref="AvAg.Core.IAsrService"/>.</summary>
    public ServiceOptions Asr { get; set; } = new() { Provider = "whisperx", Url = LocalAsrUrl, LocalModule = "whisperx_server:app" };
    /// <summary>UI elements + text per frame – <see cref="AvAg.Core.IUiParser"/>.</summary>
    public ServiceOptions UiParser { get; set; } = ServiceOptions.Off();
    /// <summary>Fallback pointing when no cursor is visible – <see cref="AvAg.Core.IVideoGrounder"/>.</summary>
    public ServiceOptions Grounder { get; set; } = ServiceOptions.Off();
    /// <summary>Box tracking – <see cref="AvAg.Core.IObjectTracker"/>.</summary>
    public ServiceOptions Tracker { get; set; } = ServiceOptions.Off();
    /// <summary>Clip narratives – <see cref="AvAg.Core.IClipDescriber"/>.</summary>
    public ServiceOptions ClipDescriber { get; set; } = ServiceOptions.Off();
    /// <summary>Language model that writes the manual – <see cref="AvAg.Core.ITextGenerator"/>.</summary>
    public ServiceOptions TextGenerator { get; set; } = ServiceOptions.Off();
}
