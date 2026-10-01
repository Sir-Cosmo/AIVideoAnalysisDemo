using AvAg.Core;
using AvAg.Pipeline.Adapters;
using AvAg.Pipeline.Manuals;

namespace AvAg.Pipeline.Services;

/// <summary>Named constructors for one capability, e.g. "whisperx" → <see cref="WhisperXSidecar"/>.</summary>
public sealed class ProviderRegistry<T> where T : class
{
    private readonly Dictionary<string, Func<ServiceOptions, T>> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _capability;

    public ProviderRegistry(string capability, string? defaultProvider)
    {
        _capability = capability;
        DefaultProvider = defaultProvider;
    }

    /// <summary>Provider used when the options name none but give a URL or file.</summary>
    public string? DefaultProvider { get; set; }
    public IReadOnlyCollection<string> Names => _providers.Keys;

    /// <summary>Adds or replaces a provider. Several names may point to the same constructor (aliases).</summary>
    public ProviderRegistry<T> Register(string name, Func<ServiceOptions, T> create, params string[] aliases)
    {
        foreach (var n in aliases.Prepend(name)) _providers[n] = create;
        return this;
    }

    /// <summary>The provider name these options select, or null when the capability is switched off.</summary>
    public string? Resolve(ServiceOptions? o)
    {
        if (o is null) return null;
        string? name = string.IsNullOrWhiteSpace(o.Provider)
            ? (string.IsNullOrWhiteSpace(o.Url) && string.IsNullOrWhiteSpace(o.Path) ? null : DefaultProvider)
            : o.Provider.Trim();
        return name is null || name.Equals(ServiceOptions.None, StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    /// <summary>The configured implementation, or null when the capability is switched off.</summary>
    public T? Create(ServiceOptions? o)
    {
        if (Resolve(o) is not { } name) return null;
        if (!_providers.TryGetValue(name, out var create))
            throw new InvalidOperationException($"Unknown {_capability} provider \"{name}\". Known: {string.Join(", ", _providers.Keys.Order())}, none.");
        return create(o!);
    }

    /// <summary>The Url, or a clear error naming the capability and provider.</summary>
    public string RequireUrl(ServiceOptions o) => string.IsNullOrWhiteSpace(o.Url)
        ? throw new InvalidOperationException($"{_capability} provider \"{o.Provider ?? DefaultProvider}\" needs a Url.") : o.Url;

    /// <summary>The Path, or a clear error naming the capability and provider.</summary>
    public string RequirePath(ServiceOptions o) => string.IsNullOrWhiteSpace(o.Path)
        ? throw new InvalidOperationException($"{_capability} provider \"{o.Provider}\" needs a Path.") : o.Path;
}

/// <summary>
/// The one place that turns configuration into AI implementations. To use a different AI for a capability:
/// write a class implementing its interface (AvAg.Core/Abstractions/AiServices.cs), register it here (or on a
/// factory instance at start-up) under a new provider name, and select that name in appsettings.json or on the CLI.
/// </summary>
public sealed class AiServiceFactory
{
    public ProviderRegistry<IAsrService> Asr { get; } = new("Asr", "whisperx");
    public ProviderRegistry<IUiParser> UiParser { get; } = new("UiParser", "omniparser");
    public ProviderRegistry<IVideoGrounder> Grounder { get; } = new("Grounder", "molmo");
    public ProviderRegistry<IObjectTracker> Tracker { get; } = new("Tracker", "sam2");
    public ProviderRegistry<IClipDescriber> ClipDescriber { get; } = new("ClipDescriber", "qwen-vl");
    public ProviderRegistry<ITextGenerator> TextGenerator { get; } = new("TextGenerator", "openai-compatible");

    /// <summary>A factory with all built-in providers.</summary>
    public static AiServiceFactory CreateDefault()
    {
        var f = new AiServiceFactory();
        f.Asr.Register("whisperx", o => new WhisperXSidecar(f.Asr.RequireUrl(o), timeout: o.Timeout))
             .Register("whisperx-json", o => new JsonFileAsr(f.Asr.RequirePath(o)));
        f.UiParser.Register("omniparser", o => new OmniParserSidecar(f.UiParser.RequireUrl(o), timeout: o.Timeout))
                  .Register("ui-json", o => new JsonFileUiParser(f.UiParser.RequirePath(o)));
        f.Grounder.Register("molmo", o => new MolmoPointSidecar(f.Grounder.RequireUrl(o), timeout: o.Timeout))
                  .Register("qwen-vl", o => new Qwen3VlClient(f.Grounder.RequireUrl(o), o.Model, timeout: o.Timeout));
        f.Tracker.Register("sam2", o => new Sam2Sidecar(f.Tracker.RequireUrl(o), timeout: o.Timeout));
        f.ClipDescriber.Register("qwen-vl", o => new Qwen3VlClient(f.ClipDescriber.RequireUrl(o), o.Model, timeout: o.Timeout));
        f.TextGenerator.Register("openai-compatible",
            o => new OpenAiCompatibleTextGenerator(f.TextGenerator.RequireUrl(o), o.Model, o.ApiKey, o.Timeout),
            "ollama", "openai", "azure-openai", "lm-studio", "vllm");
        return f;
    }

    /// <summary>Provider and endpoint per capability for logs and status pages – never API keys.</summary>
    public IReadOnlyDictionary<string, string?> Describe(AiServicesOptions o)
    {
        static string? Where(string? provider, ServiceOptions s) =>
            provider is null ? null : provider + (s.Url is null ? "" : " @ " + s.Url) + (s.Path is null ? "" : " (file)");
        return new Dictionary<string, string?>
        {
            ["asr"] = Where(Asr.Resolve(o.Asr), o.Asr), ["ui_parser"] = Where(UiParser.Resolve(o.UiParser), o.UiParser),
            ["grounder"] = Where(Grounder.Resolve(o.Grounder), o.Grounder), ["tracker"] = Where(Tracker.Resolve(o.Tracker), o.Tracker),
            ["clip_describer"] = Where(ClipDescriber.Resolve(o.ClipDescriber), o.ClipDescriber),
            ["text_generator"] = Where(TextGenerator.Resolve(o.TextGenerator), o.TextGenerator),
        };
    }

    /// <summary>Everything the analysis pipeline needs. Speech recognition is required; the rest is optional.</summary>
    public PipelineServices CreatePipelineServices(AiServicesOptions o) => new()
    {
        Asr = Asr.Create(o.Asr) ?? throw new InvalidOperationException("No speech recognition configured: set Services:Asr or provide a transcript file."),
        UiParser = UiParser.Create(o.UiParser) ?? new NullUiParser(),
        Grounder = Grounder.Create(o.Grounder),
        Tracker = Tracker.Create(o.Tracker),
        ClipDescriber = ClipDescriber.Create(o.ClipDescriber),
    };

    /// <summary>The manual writer for the configured language model, or null to keep the rule-based manual.</summary>
    public IManualWriter? CreateManualWriter(AiServicesOptions o) =>
        TextGenerator.Create(o.TextGenerator) is { } llm ? new LlmManualWriter(llm) : null;
}
