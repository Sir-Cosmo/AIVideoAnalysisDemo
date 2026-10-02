namespace AvAg.Pipeline.Services;

/// <summary>
/// Configuration of one AI capability: which provider implements it and how to reach it.
/// Bound from appsettings.json (<c>AvAg:Services:&lt;Capability&gt;</c>) or built from CLI flags.
/// Deliberately has no defaults in its properties: the configuration binder merges into existing values, so defaults
/// here would silently survive in sections the user wrote without them.
/// </summary>
public sealed class ServiceOptions
{
    public const string None = "none";

    /// <summary>Provider name registered in <see cref="AiServiceFactory"/> (e.g. "whisperx", "openai-compatible").
    /// Empty: the capability's default provider if <see cref="Url"/> or <see cref="Path"/> is set, otherwise off.
    /// "none" (any case) switches the capability off.</summary>
    public string? Provider { get; set; }
    /// <summary>Base URL of an HTTP service. For OpenAI-compatible endpoints include the version, e.g. http://127.0.0.1:11434/v1.</summary>
    public string? Url { get; set; }
    public string? Model { get; set; }
    /// <summary>Sent as "Authorization: Bearer" by every HTTP provider. Prefer environment variables / user secrets
    /// (e.g. AvAg__Services__TextGenerator__ApiKey) over appsettings.json for real keys.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Input file for file-based providers ("whisperx-json", "ui-json").</summary>
    public string? Path { get; set; }
    /// <summary>Request timeout; empty = the provider's default (sidecars 30 min, language models 10 min).</summary>
    public int? TimeoutSeconds { get; set; }
    /// <summary>Python module of a local sidecar ("whisperx_server:app"); the web app starts it when <see cref="Url"/> is local and not running.</summary>
    public string? LocalModule { get; set; }

    /// <summary>Used instead when this service fails (not reachable, out of credit, timeout, bad answer) – e.g. OpenAI
    /// first, the local WhisperX / Ollama as fallback. May itself have a fallback.</summary>
    public ServiceOptions? Fallback { get; set; }
    /// <summary>Speech recognition: words, product names and abbreviations to expect ("Messerli, BAUAD, Mandant …").
    /// Passed to providers that accept a prompt.</summary>
    public string? Prompt { get; set; }
    /// <summary>Speech recognition: a local WhisperX sidecar that aligns a cloud transcript to exact word times
    /// (POST /align). Without it, word times are spread evenly over each segment.</summary>
    public string? AlignUrl { get; set; }
    /// <summary>Text generation: the model can read images; the article writer then also sends frames of the screen
    /// (never for private videos).</summary>
    public bool Vision { get; set; }
    /// <summary>Text generation with reasoning models (gpt-5, o3 …): "minimal", "low", "medium", "high". Empty = model default.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>Explicitly switched off with "none".</summary>
    public bool IsOff => string.Equals(Provider?.Trim(), None, StringComparison.OrdinalIgnoreCase);
    /// <summary>Nothing configured at all.</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Provider) && string.IsNullOrWhiteSpace(Url) && string.IsNullOrWhiteSpace(Path);

    /// <summary>Runs on this machine or in the local network: a file, or a URL on a loopback / private address.
    /// Only such services may see a private video.</summary>
    public bool IsLocal => !string.IsNullOrWhiteSpace(Path) && string.IsNullOrWhiteSpace(Url) || IsLocalUrl(Url);

    public static bool IsLocalUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.IsLoopback || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;
        if (!System.Net.IPAddress.TryParse(u.Host, out var ip)) return false;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
    }

    /// <summary>This chain without the services that are not <see cref="IsLocal"/> (an alignment URL elsewhere is dropped
    /// too); <see cref="Off"/> if none is left.</summary>
    public ServiceOptions LocalOnly()
    {
        if (IsOff) return Clone();
        ServiceOptions? head = null, tail = null;
        foreach (var o in WithFallbacks().Where(x => !x.IsEmpty && x.IsLocal))
        {
            var c = (ServiceOptions)o.MemberwiseClone();
            c.Fallback = null;
            if (!IsLocalUrl(c.AlignUrl)) c.AlignUrl = null;
            if (head is null) head = c; else tail!.Fallback = c;
            tail = c;
        }
        return head ?? (IsEmpty ? Clone() : Off());
    }

    public TimeSpan TimeoutOr(TimeSpan fallback) => TimeoutSeconds is > 0 and var s ? TimeSpan.FromSeconds(s) : fallback;

    public ServiceOptions Clone()
    {
        var o = (ServiceOptions)MemberwiseClone();
        o.Fallback = Fallback?.Clone();
        return o;
    }

    /// <summary>These options and all their fallbacks, in order.</summary>
    public IEnumerable<ServiceOptions> WithFallbacks()
    {
        for (var o = this; o is not null; o = o.Fallback) yield return o;
    }

    /// <summary>
    /// Applies an endpoint override: null or the configured URL keeps these options, "" switches the capability off, and
    /// another URL selects <paramref name="sidecarProvider"/> there (the sidecar the page field names). Provider, model,
    /// timeout and auto-start survive only if they already belong to that sidecar; API key, alignment URL and fallbacks
    /// never follow a URL to another host.
    /// </summary>
    public ServiceOptions WithUrlOverride(string? url, string sidecarProvider)
    {
        if (url is null) return Clone();
        url = url.Trim();
        if (url.Length == 0) return Off();
        if (!IsOff && string.Equals(url.TrimEnd('/'), Url?.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return Clone();
        bool sameSidecar = !IsOff && (string.IsNullOrWhiteSpace(Provider) || Provider.Trim().Equals(sidecarProvider, StringComparison.OrdinalIgnoreCase));
        return sameSidecar
            ? new ServiceOptions { Provider = Provider, Url = url, Model = Model, TimeoutSeconds = TimeoutSeconds, LocalModule = LocalModule, Prompt = Prompt }
            : new ServiceOptions { Provider = sidecarProvider, Url = url };
    }

    public static ServiceOptions FromFile(string provider, string path) => new() { Provider = provider, Path = path };
    public static ServiceOptions Off() => new() { Provider = None };
}

/// <summary>
/// Endpoint overrides from the web page or the CLI, applied with <see cref="AiServicesOptions.WithOverrides"/>.
/// For URLs: null (or the configured URL) = keep the configuration, "" = switch the capability off, otherwise that
/// capability's sidecar at that URL (see <see cref="ServiceOptions.WithUrlOverride"/>).
/// A file replaces the corresponding service for this run.
/// </summary>
public sealed record ServiceOverrides(
    string? AsrUrl = null, string? UiParserUrl = null, string? GrounderUrl = null, string? TrackerUrl = null, string? ClipDescriberUrl = null,
    string? TranscriptFile = null, string? UiElementsFile = null);

/// <summary>All replaceable AI capabilities of the pipeline (see AvAg.Core/Abstractions/AiServices.cs).
/// Every capability is off unless configured – except speech recognition, which falls back to the local WhisperX sidecar.</summary>
public sealed class AiServicesOptions
{
    public const string LocalAsrUrl = "http://127.0.0.1:8011";
    public const string LocalAsrModule = "whisperx_server:app";

    /// <summary>Speech recognition – <see cref="AvAg.Core.IAsrService"/>. Empty: WhisperX on <see cref="LocalAsrUrl"/>.</summary>
    public ServiceOptions Asr { get; set; } = new();
    /// <summary>UI elements + text per frame – <see cref="AvAg.Core.IUiParser"/>.</summary>
    public ServiceOptions UiParser { get; set; } = new();
    /// <summary>Fallback pointing when no cursor is visible – <see cref="AvAg.Core.IVideoGrounder"/>.</summary>
    public ServiceOptions Grounder { get; set; } = new();
    /// <summary>Box tracking – <see cref="AvAg.Core.IObjectTracker"/>.</summary>
    public ServiceOptions Tracker { get; set; } = new();
    /// <summary>Clip narratives – <see cref="AvAg.Core.IClipDescriber"/>.</summary>
    public ServiceOptions ClipDescriber { get; set; } = new();
    /// <summary>Language model that writes the wiki article – <see cref="AvAg.Core.ITextGenerator"/>.</summary>
    public ServiceOptions TextGenerator { get; set; } = new();

    /// <summary>Speech recognition as used: the configured one, or the local WhisperX sidecar when nothing is configured.</summary>
    public ServiceOptions EffectiveAsr => Asr.IsEmpty ? new ServiceOptions { Provider = "whisperx", Url = LocalAsrUrl, LocalModule = LocalAsrModule } : Asr;

    /// <summary>The configured service of every capability, each with its fallbacks.</summary>
    public IEnumerable<ServiceOptions> Primaries => [EffectiveAsr, UiParser, Grounder, Tracker, ClipDescriber, TextGenerator];

    /// <summary>Every configured service including fallbacks – for starting local sidecars.</summary>
    public IEnumerable<ServiceOptions> All => Primaries.SelectMany(o => o.WithFallbacks());

    /// <summary>For private videos: every capability restricted to services on this machine or in the local network.
    /// Cloud services (OpenAI, …) are dropped from each chain; a capability with no local service is off.</summary>
    public AiServicesOptions LocalOnly() => new()
    {
        Asr = EffectiveAsr.LocalOnly(), UiParser = UiParser.LocalOnly(), Grounder = Grounder.LocalOnly(),
        Tracker = Tracker.LocalOnly(), ClipDescriber = ClipDescriber.LocalOnly(), TextGenerator = TextGenerator.LocalOnly(),
    };

    /// <summary>
    /// A copy with this run's overrides – the single place where the web page and the CLI change the configuration.
    /// If no grounder is configured but a Qwen-VL clip describer is, the describer's endpoint (with its model, key and
    /// timeout) also serves as fallback grounder; an explicit "none" is respected.
    /// </summary>
    public AiServicesOptions WithOverrides(ServiceOverrides o)
    {
        var r = new AiServicesOptions
        {
            Asr = o.TranscriptFile is not null ? ServiceOptions.FromFile("whisperx-json", o.TranscriptFile) : EffectiveAsr.WithUrlOverride(o.AsrUrl, "whisperx"),
            UiParser = o.UiElementsFile is not null ? ServiceOptions.FromFile("ui-json", o.UiElementsFile) : UiParser.WithUrlOverride(o.UiParserUrl, "omniparser"),
            Grounder = Grounder.WithUrlOverride(o.GrounderUrl, "molmo"),
            Tracker = Tracker.WithUrlOverride(o.TrackerUrl, "sam2"),
            ClipDescriber = ClipDescriber.WithUrlOverride(o.ClipDescriberUrl, "qwen-vl"),
            TextGenerator = TextGenerator.Clone(),
        };
        bool describerIsQwen = !r.ClipDescriber.IsOff && !r.ClipDescriber.IsEmpty
                               && (string.IsNullOrWhiteSpace(r.ClipDescriber.Provider) || r.ClipDescriber.Provider.Trim().Equals("qwen-vl", StringComparison.OrdinalIgnoreCase));
        if (r.Grounder.IsEmpty && describerIsQwen)
        {
            r.Grounder = r.ClipDescriber.Clone();
            r.Grounder.Provider = "qwen-vl";
            r.Grounder.LocalModule = null;
        }
        return r;
    }
}
