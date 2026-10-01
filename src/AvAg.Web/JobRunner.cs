using AvAg.Core;
using AvAg.Pipeline;
using AvAg.Pipeline.Services;

namespace AvAg.Web;

/// <summary>Runs the analysis pipeline for one uploaded video in the background.</summary>
public sealed class JobRunner
{
    private readonly AiServiceFactory _factory;
    private readonly SidecarLauncher _sidecars;
    private readonly ILogger<JobRunner> _log;

    public JobRunner(AiServiceFactory factory, SidecarLauncher sidecars, ILogger<JobRunner> log)
    {
        _factory = factory; _sidecars = sidecars; _log = log;
    }

    public void Start(Job job) => _ = Task.Run(() => RunAsync(job));

    private async Task RunAsync(Job job)
    {
        try
        {
            job.State = JobState.Running;
            var o = job.Options!;
            await _sidecars.EnsureReadyAsync(o.Services.Asr, job.Log.Add, job.Cancel.Token);

            var runner = new PipelineRunner(new PipelineConfig
            {
                WorkDir = Path.Combine(job.Dir, "work"), LanguageHint = o.Language, Diarize = o.Diarize,
                CoarseFps = o.CoarseFps, FineFps = o.FineFps, CursorTemplatePng = job.CursorPath,
            }, _factory.CreatePipelineServices(o.Services));

            var result = await runner.RunAsync(job.VideoPath!, job.Cancel.Token);
            job.Result = result;
            job.GraphJson = Json.Serialize(result.Graph);
            job.Log.AddRange(result.Log);
            job.State = JobState.Done;
        }
        catch (OperationCanceledException) { job.State = JobState.Failed; job.Error = "Cancelled."; }
        catch (HttpRequestException ex)
        {
            job.State = JobState.Failed;
            job.Error = $"A model service could not be reached ({ex.Message}). Start the Python sidecars, or upload a transcript JSON and UI-elements JSON to run without them.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "job {Id} failed", job.Id);
            job.State = JobState.Failed;
            job.Error = ex.Message;
        }
    }
}
