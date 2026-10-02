using AvAg.Core;
using AvAg.Pipeline.Adapters;
using AvAg.Pipeline.Articles;

namespace AvAg.Pipeline.Services;

/// <summary>Named constructors for one capability, e.g. "whisperx" → <see cref="WhisperXSidecar"/>.</summary>
public sealed class ProviderRegistry<T> where T : class
{
    private readonly Dictionary<string, Func<ServiceOptions, T>> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _capability;

    public ProviderRegistry(string capability, string? defaultProvider, TimeSpan defaultTimeout)
    {
        _capability = capability;
        DefaultProvider = defaultProvider;
        DefaultTimeout = defaultTimeout;
    }

    /// <summary>Provider used when the options name none but give a URL or file.</summary>
    public string? DefaultProvider { get; set; }
    /// <summary>Request timeout when the options set none.</summary>
    public TimeSpan DefaultTimeout { get; set; }

    /// <summary>Adds or replaces a provider. Several names may point to the same constructor (aliases).</summary>
    public ProviderRegistry<T> Register(string name, Func<ServiceOptions, T> create, params string[] aliases)
    {
        foreach (var n in aliases.Prepend(name)) _providers[n] = create;
        return this;
    }

    /// <summary>The provider name these options select, or null when the capability is off.</summary>
    public string? Resolve(ServiceOptions? o)
    {
        if (o is null || o.IsOff) return null;
        if (!string.IsNullOrWhiteSpace(o.Provider)) return o.Provider.Trim();
        return string.IsNullOrWhiteSpace(o.Url) && string.IsNullOrWhiteSpace(o.Path) ? null : DefaultProvider;
    }

    /// <summary>The configured implementation, or null when the capability is off.
    /// Throws <see cref="InvalidOperationException"/> for an unknown provider or missing Url/Path.</summary>
    public T? Create(ServiceOptions? o)
    {
        if (Resolve(o) is not { } name) return null;
        if (!_providers.TryGetValue(name, out var create))
            throw new InvalidOperationException($"Unknown {_capability} provider \"{name}\". Known: {string.Join(", ", _providers.Keys.Order())}, none.");
        return create(o!);
    }

    /// <summary>The Url, or a clear error naming the capability and provider.</summary>
    public string RequireUrl(ServiceOptions o) => string.IsNullOrWhiteSpace(o.Url)
        ? throw new InvalidOperationException($"{_capability} provider \"{Resolve(o)}\" needs a Url.") : o.Url;

    /// <summary>The Path, or a clear error naming the capability and provider.</summary>
    public string RequirePath(ServiceOptions o) => string.IsNullOrWhiteSpace(o.Path)
        ? throw new InvalidOperationException($"{_capability} provider \"{Resolve(o)}\" needs a Path.") : o.Path;

    /// <summary>The configured timeout, or this capability's default.</summary>
    public TimeSpan Timeout(ServiceOptions o) => o.TimeoutOr(DefaultTimeout);
}

/// <summary>
/// The one place that turns configuration into AI implementations. To use a different AI for a capability:
/// write a class implementing its interface (AvAg.Core/Abstractions/AiServices.cs), register it here (or on a
/// factory instance at start-up) under a new provider name, and select that name in appsettings.json or on the CLI.
/// </summary>
public sealed class AiServiceFactory
{
    private static readonly TimeSpan SidecarTimeout = TimeSpan.FromMinutes(30), LlmTimeout = TimeSpan.FromMinutes(10);

    public ProviderRegistry<IAsrService> Asr { get; } = new("Asr", "whisperx", SidecarTimeout);
    public ProviderRegistry<IUiParser> UiParser { get; } = new("UiParser", "omniparser", SidecarTimeout);
    public ProviderRegistry<IVideoGrounder> Grounder { get; } = new("Grounder", "molmo", SidecarTimeout);
    public ProviderRegistry<IObjectTracker> Tracker { get; } = new("Tracker", "sam2", SidecarTimeout);
    public ProviderRegistry<IClipDescriber> ClipDescriber { get; } = new("ClipDescriber", "qwen-vl", SidecarTimeout);
    public ProviderRegistry<ITextGenerator> TextGenerator { get; } = new("TextGenerator", "openai-compatible", LlmTimeout);

    /// <summary>A factory with all built-in providers.</summary>
    public static AiServiceFactory CreateDefault()
    {
        var f = new AiServiceFactory();
        f.Asr.Register("whisperx", o => new WhisperXSidecar(f.Asr.RequireUrl(o), o.ApiKey, f.Asr.Timeout(o)))
             .Register("whisperx-json", o => new JsonFileAsr(f.Asr.RequirePath(o)))
             .Register("openai", o => new OpenAiTranscriber(f.Asr.RequireUrl(o), o.Model, o.ApiKey, f.Asr.Timeout(o), o.Prompt, o.AlignUrl));
        f.UiParser.Register("omniparser", o => new OmniParserSidecar(f.UiParser.RequireUrl(o), o.ApiKey, f.UiParser.Timeout(o)))
                  .Register("ui-json", o => new JsonFileUiParser(f.UiParser.RequirePath(o)));
        f.Grounder.Register("molmo", o => new MolmoPointSidecar(f.Grounder.RequireUrl(o), o.ApiKey, f.Grounder.Timeout(o)))
                  .Register("qwen-vl", o => new Qwen3VlClient(f.Grounder.RequireUrl(o), o.Model, o.ApiKey, f.Grounder.Timeout(o)));
        f.Tracker.Register("sam2", o => new Sam2Sidecar(f.Tracker.RequireUrl(o), o.ApiKey, f.Tracker.Timeout(o)));
        f.ClipDescriber.Register("qwen-vl", o => new Qwen3VlClient(f.ClipDescriber.RequireUrl(o), o.Model, o.ApiKey, f.ClipDescriber.Timeout(o)));
        f.TextGenerator.Register("openai-compatible",
            o => new OpenAiCompatibleTextGenerator(f.TextGenerator.RequireUrl(o), o.Model, o.ApiKey, f.TextGenerator.Timeout(o), o.Vision, o.ReasoningEffort),
            "ollama", "openai", "azure-openai", "lm-studio", "vllm");
        return f;
    }

    /// <summary>Everything the analysis pipeline needs. Speech recognition is required; the rest is optional.</summary>
    public PipelineServices CreatePipelineServices(AiServicesOptions o) => new()
    {
        Asr = CreateAsr(o.EffectiveAsr) ?? throw new InvalidOperationException("Speech recognition is switched off: configure Services:Asr or provide a transcript file."),
        UiParser = Chain(UiParser, o.UiParser, (p, pn, f, fn) => new FallbackUiParser(p, pn, f, fn)) ?? new NullUiParser(),
        Grounder = Chain(Grounder, o.Grounder, (p, pn, f, fn) => new FallbackGrounder(p, pn, f, fn)),
        Tracker = Chain(Tracker, o.Tracker, (p, pn, f, fn) => new FallbackTracker(p, pn, f, fn)),
        ClipDescriber = Chain(ClipDescriber, o.ClipDescriber, (p, pn, f, fn) => new FallbackClipDescriber(p, pn, f, fn)),
    };

    /// <summary>The article writer for the configured language model, or null to keep the rule-based article.
    /// Throws <see cref="InvalidOperationException"/> when the configuration is invalid.</summary>
    public IArticleWriter? CreateArticleWriter(AiServicesOptions o) =>
        CreateTextGenerator(o.TextGenerator) is { } llm ? new LlmArticleWriter(llm) : null;

    /// <summary>Speech recognition with its fallback chain (e.g. OpenAI → local WhisperX).</summary>
    public IAsrService? CreateAsr(ServiceOptions o) => Chain(Asr, o, (p, pn, f, fn) => new FallbackAsrService(p, pn, f, fn));

    /// <summary>Language model with its fallback chain (e.g. OpenAI → local Ollama).</summary>
    public ITextGenerator? CreateTextGenerator(ServiceOptions o) => Chain(TextGenerator, o, (p, _, f, _) => new FallbackTextGenerator(p, f));

    /// <summary>The service these options configure, wrapped with its <see cref="ServiceOptions.Fallback"/> chain.</summary>
    private static T? Chain<T>(ProviderRegistry<T> registry, ServiceOptions o, Func<T, string, T, string, T> withFallback) where T : class
    {
        var primary = registry.Create(o);
        var fallback = o.Fallback is { } fb ? Chain(registry, fb, withFallback) : null;
        return primary is null ? fallback
             : fallback is null ? primary
             : withFallback(primary, registry.Resolve(o)!, fallback, registry.Resolve(o.Fallback)!);
    }

    /// <summary>Like <see cref="CreateArticleWriter"/>, but a broken configuration returns null and the reason,
    /// so the caller can still produce the rule-based article.</summary>
    public IArticleWriter? TryCreateArticleWriter(AiServicesOptions o, out string? problem)
    {
        problem = null;
        try { return CreateArticleWriter(o); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException) { problem = ex.Message; return null; }
    }

    /// <summary>Provider and endpoint per capability for logs and status pages – never API keys.</summary>
    public IReadOnlyDictionary<string, string?> Describe(AiServicesOptions o)
    {
        string? Where<T>(ProviderRegistry<T> registry, ServiceOptions s) where T : class
        {
            var parts = s.WithFallbacks().Select(x => registry.Resolve(x) is { } p
                ? p + (x.Model is null ? "" : " " + x.Model) + (x.Url is null ? "" : " @ " + x.Url) + (x.Path is null ? "" : " (file)") : null)
                .Where(x => x is not null).ToList();
            return parts.Count == 0 ? null : string.Join(" → fallback ", parts);
        }
        return new Dictionary<string, string?>
        {
            ["asr"] = Where(Asr, o.EffectiveAsr), ["ui_parser"] = Where(UiParser, o.UiParser),
            ["grounder"] = Where(Grounder, o.Grounder), ["tracker"] = Where(Tracker, o.Tracker),
            ["clip_describer"] = Where(ClipDescriber, o.ClipDescriber), ["text_generator"] = Where(TextGenerator, o.TextGenerator),
        };
    }
}
