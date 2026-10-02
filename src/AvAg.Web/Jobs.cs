using System.Collections.Concurrent;
using AvAg.Pipeline;
using AvAg.Pipeline.Services;

namespace AvAg.Web;

/// <summary>appsettings.json section "AvAg".</summary>
public sealed class WebSettings
{
    /// <summary>Which AI implements each capability and where it runs. See AiServicesOptions / AiServiceFactory.</summary>
    public AiServicesOptions Services { get; set; } = new();
    /// <summary>Uploads, frames and results are deleted this many minutes after the job finished (privacy).</summary>
    public int RetentionMinutes { get; set; } = 60;
    /// <summary>Start local sidecars (services with a LocalModule and a local Url) when they are not running.</summary>
    public bool AutoStartSidecars { get; set; } = true;
    public string UploadRoot { get; set; } = Path.Combine(Path.GetTempPath(), "avag-web");
}

/// <summary>Per-job settings: the configured services with this upload's overrides applied.</summary>
public sealed class RunOptions
{
    public required AiServicesOptions Services { get; init; }
    /// <summary>Resolved provider and endpoint per capability (from AiServiceFactory.Describe) – shown in the status, never API keys.</summary>
    public IReadOnlyDictionary<string, string?> ServiceSummary { get; init; } = new Dictionary<string, string?>();
    public string? Language { get; init; }   // null = detect
    public bool Diarize { get; init; } = true;
    public double CoarseFps { get; init; } = 3;
    public double FineFps { get; init; } = 20;

    /// <summary>For the status API.</summary>
    public object Describe() => new { language = Language, diarize = Diarize, coarse_fps = CoarseFps, fine_fps = FineFps, services = ServiceSummary };
}

public enum JobState { Queued, Running, Done, Failed }

public sealed class Job
{
    public required string Id { get; init; }
    public required string Dir { get; init; }
    public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Finished { get; private set; }
    public string? VideoPath { get; set; }
    public string? VideoName { get; set; }
    public string? CursorPath { get; set; }
    public RunOptions? Options { get; set; }
    public CancellationTokenSource Cancel { get; } = new();
    public List<string> Log { get; } = new();
    public PipelineResult? Result { get; set; }
    public string? GraphJson { get; set; }
    public AvAg.Core.WikiArticle? Article { get; set; }
    /// <summary>Set at upload: every article for this video is text only (no screenshots).</summary>
    public bool Private { get; set; }
    public string? Error { get; set; }

    private JobState _state;
    public JobState State
    {
        get => _state;
        set { _state = value; if (value is JobState.Done or JobState.Failed) Finished = DateTimeOffset.UtcNow; }
    }

    public object ToStatus() => new
    {
        id = Id, state = State, video_name = VideoName, @private = Private, error = Error, log = Log,
        created = Created, finished = Finished,
        graph = Result?.Graph,
        article = Article is null ? null : new { Article.Title, Article.Method, steps = Article.Steps.Count, Article.Language, Article.Private },
        timeline_de = Result?.TimelineDe, timeline_en = Result?.TimelineEn,
        audio_refs = Result?.AudioRefs.Select(a => new { a.Id, a.StartS, a.EndS, a.AnchorS, a.Text, a.Action, a.Deictic, a.ExplicitTarget, a.SpeakerId }),
        transcript = Result?.Transcript.Segments.Select(s => new { s.StartS, s.EndS, s.Text, s.Speaker }),
        inputs = new { cursor = CursorPath is not null, options = Options?.Describe() },
    };
}

public sealed class JobStore
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();
    private readonly WebSettings _settings;
    public JobStore(WebSettings settings) => _settings = settings;

    public Job Create()
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var dir = Path.Combine(_settings.UploadRoot, id);
        Directory.CreateDirectory(dir);
        var job = new Job { Id = id, Dir = dir };
        _jobs[id] = job;
        return job;
    }

    public bool TryGet(string id, out Job job) => _jobs.TryGetValue(id, out job!);

    public void Remove(string id)
    {
        if (_jobs.TryRemove(id, out var job))
        {
            job.Cancel.Cancel();
            try { Directory.Delete(job.Dir, true); } catch { /* best effort */ }
        }
    }

    public IEnumerable<Job> All => _jobs.Values;
}

/// <summary>Deletes finished jobs (video, frames, results) after the configured retention period.</summary>
public sealed class JobJanitor : BackgroundService
{
    private readonly JobStore _store;
    private readonly int _retentionMinutes;

    public JobJanitor(JobStore store, WebSettings settings)
    {
        _store = store;
        _retentionMinutes = settings.RetentionMinutes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-_retentionMinutes);
            foreach (var j in _store.All.Where(j => (j.Finished ?? j.Created) < cutoff).ToList()) _store.Remove(j.Id);
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
