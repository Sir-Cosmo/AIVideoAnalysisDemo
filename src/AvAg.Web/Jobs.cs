using System.Collections.Concurrent;
using AvAg.Pipeline;

namespace AvAg.Web;

public sealed class WebSettings
{
    public string? AsrUrl { get; set; } = "http://127.0.0.1:8011";
    public string? UiUrl { get; set; } = "http://127.0.0.1:8003";
    public string? MolmoUrl { get; set; }
    public string? Sam2Url { get; set; }
    public string? QwenUrl { get; set; }
    /// <summary>Uploads, frames and results are deleted this many minutes after the job finished (privacy).</summary>
    public int RetentionMinutes { get; set; } = 60;
    /// <summary>Start sidecars/whisperx_server.py with the web app when AsrUrl is local and not already running.</summary>
    public bool AutoStartSidecars { get; set; } = true;
    /// <summary>Optional OpenAI-compatible language model that summarises the manual; empty Url = rule-based manual.</summary>
    public ManualLlmSettings ManualLlm { get; set; } = new();
    public string UploadRoot { get; set; } = Path.Combine(Path.GetTempPath(), "avag-web");
}

public sealed class RunOptions
{
    public string? AsrUrl { get; init; }
    public string? UiUrl { get; init; }
    public string? MolmoUrl { get; init; }
    public string? Sam2Url { get; init; }
    public string? QwenUrl { get; init; }
    public string? Language { get; init; }   // null = detect
    public bool Diarize { get; init; } = true;
    public double CoarseFps { get; init; } = 3;
    public double FineFps { get; init; } = 20;
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
    public string? TranscriptPath { get; set; }
    public string? UiJsonPath { get; set; }
    public string? CursorPath { get; set; }
    public RunOptions? Options { get; set; }
    public CancellationTokenSource Cancel { get; } = new();
    public List<string> Log { get; } = new();
    public PipelineResult? Result { get; set; }
    public string? GraphJson { get; set; }
    public AvAg.Core.Manual? Manual { get; set; }
    /// <summary>Set at upload: every manual for this video is text only (no screenshots).</summary>
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
        manual = Manual is null ? null : new { Manual.Title, Manual.Method, steps = Manual.Steps.Count, Manual.Language },
        timeline_de = Result?.TimelineDe, timeline_en = Result?.TimelineEn,
        audio_refs = Result?.AudioRefs.Select(a => new { a.Id, a.StartS, a.EndS, a.AnchorS, a.Text, a.Action, a.Deictic, a.ExplicitTarget, a.SpeakerId }),
        transcript = Result?.Transcript.Segments.Select(s => new { s.StartS, s.EndS, s.Text, s.Speaker }),
        inputs = new { transcript = TranscriptPath is not null, ui_json = UiJsonPath is not null, cursor = CursorPath is not null, options = Options },
    };
}

public sealed class JobStore
{
    private readonly ConcurrentDictionary<string, Job> _jobs = new();

    public Job Create(WebSettings cfg)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var dir = Path.Combine(cfg.UploadRoot, id);
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
    public JobJanitor(JobStore store, IConfiguration config)
    {
        _store = store;
        _retentionMinutes = config.GetSection("AvAg").Get<WebSettings>()?.RetentionMinutes ?? 60;
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
