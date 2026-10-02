using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using AvAg.Pipeline.Services;

namespace AvAg.Web;

/// <summary>
/// Starts local Python sidecars (e.g. sidecars/whisperx_server.py) on demand, so F5 in Visual Studio is enough for a
/// full run. A service is managed when its options name a <see cref="ServiceOptions.LocalModule"/>, it is not switched
/// off, and its Url points at this machine. It is started when nothing listens there yet – never a second copy, because
/// every instance loads its own model onto the GPU. Uses sidecars/.venv if present (python on PATH otherwise); output
/// goes to sidecars/&lt;module&gt;.log and the processes stop with the web app. Disable with "AvAg:AutoStartSidecars": false.
/// </summary>
public sealed class SidecarLauncher : IHostedService, IDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(10); // a first start may download the model

    private readonly WebSettings _settings;
    private readonly ILogger<SidecarLauncher> _log;
    private readonly string _contentRoot;
    private readonly ConcurrentDictionary<int, Process> _byPort = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public SidecarLauncher(WebSettings settings, IWebHostEnvironment env, ILogger<SidecarLauncher> log)
    {
        _settings = settings; _log = log; _contentRoot = env.ContentRootPath;
    }

    /// <summary>At start-up: launch every managed sidecar that is not running. Does not wait for them – the web app is
    /// usable immediately, and a job waits for the services it needs (<see cref="EnsureReadyAsync"/>).</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        foreach (var (o, _) in Sidecars(_settings.Services))
            if (IsManaged(o, out var uri)) await StartIfNeededAsync(o, uri, ct);
    }

    /// <summary>
    /// Before a job: (re)starts every managed sidecar the job uses – primaries, fallbacks and the local WhisperX that aligns
    /// a cloud transcript – and waits until each accepts connections. Only a primary that cannot start fails the job: a
    /// fallback or aligner that exits or does not come up is reported and the job goes on without it.
    /// </summary>
    public async Task EnsureReadyAsync(AiServicesOptions services, Action<string> progress, CancellationToken ct)
    {
        foreach (var (o, required) in Sidecars(services))
        {
            if (!IsManaged(o, out var uri) || await IsListeningAsync(uri, ct)) continue;
            progress($"starting the {o.LocalModule} sidecar…");
            await StartIfNeededAsync(o, uri, ct);
            if (!_byPort.TryGetValue(uri.Port, out var p)) continue; // could not be started – the job reports the connection error
            var deadline = DateTime.UtcNow + ReadyTimeout;
            while (!await IsListeningAsync(uri, ct))
            {
                string? problem = p.HasExited ? $"The {o.LocalModule} sidecar exited (code {p.ExitCode}); see {LogName(o)} in the sidecars folder."
                                : DateTime.UtcNow > deadline ? $"The {o.LocalModule} sidecar did not start within {ReadyTimeout.TotalMinutes:0} min." : null;
                if (problem is not null)
                {
                    if (required) throw new InvalidOperationException(problem);
                    progress(problem + " Continuing without it.");
                    break;
                }
                await Task.Delay(1000, ct);
            }
        }
    }

    /// <summary>Every service of every capability (primary = required) plus a local alignment URL of speech recognition,
    /// which is the WhisperX sidecar too. Each port once, a required entry first.</summary>
    private static IEnumerable<(ServiceOptions Options, bool Required)> Sidecars(AiServicesOptions services)
    {
        var all = new List<(ServiceOptions, bool)>();
        foreach (var primary in services.Primaries)
            foreach (var o in primary.WithFallbacks())
            {
                all.Add((o, ReferenceEquals(o, primary)));
                if (!string.IsNullOrWhiteSpace(o.AlignUrl))
                    all.Add((new ServiceOptions { Provider = "whisperx", Url = o.AlignUrl, LocalModule = AiServicesOptions.LocalAsrModule }, false));
            }
        return all.OrderByDescending(x => x.Item2)
                  .DistinctBy(x => Uri.TryCreate(x.Item1.Url, UriKind.Absolute, out var u) && x.Item1.LocalModule is not null ? $"{u.Host}:{u.Port}" : Guid.NewGuid().ToString());
    }

    private bool IsManaged(ServiceOptions o, out Uri uri)
    {
        uri = null!;
        if (!_settings.AutoStartSidecars || string.IsNullOrWhiteSpace(o.LocalModule) || o.IsOff) return false;
        if (!Uri.TryCreate(o.Url, UriKind.Absolute, out var u) || u.Host is not ("127.0.0.1" or "localhost")) return false;
        uri = u;
        return true;
    }

    /// <summary>Starts the process unless something listens on the port or our own copy is still starting. The lock only
    /// covers this check-and-start, never the wait for readiness.</summary>
    private async Task StartIfNeededAsync(ServiceOptions o, Uri uri, CancellationToken ct)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            if (_byPort.TryGetValue(uri.Port, out var running) && !running.HasExited) return;
            if (await IsListeningAsync(uri, ct)) return;
            Start(o, uri);
        }
        finally { _startLock.Release(); }
    }

    private void Start(ServiceOptions o, Uri uri)
    {
        var dir = FindSidecarDir(o.LocalModule!);
        if (dir is null) { _log.LogWarning("Sidecar module {Module} not found; start it manually on {Url}", o.LocalModule, uri); return; }
        var venvPy = Path.Combine(dir, ".venv", "Scripts", "python.exe");
        var python = File.Exists(venvPy) ? venvPy : "python";
        var logPath = Path.Combine(dir, LogName(o));

        var psi = new ProcessStartInfo(python)
        {
            WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-m", "uvicorn", o.LocalModule!, "--host", uri.Host, "--port", uri.Port.ToString() }) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1"; // errors reach the log immediately

        try
        {
            var p = Process.Start(psi)!;
            var logFile = new StreamWriter(logPath, append: false) { AutoFlush = true };
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (logFile) logFile.WriteLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (logFile) logFile.WriteLine(e.Data); };
            p.Exited += (_, _) => { lock (logFile) logFile.Dispose(); };
            p.EnableRaisingEvents = true;
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            _byPort[uri.Port] = p;
            _log.LogInformation("Started sidecar {Module} (pid {Pid}) on {Url}. Log: {Log}", o.LocalModule, p.Id, uri, logPath);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not start sidecar {Module} with {Python}", o.LocalModule, python); }
    }

    public Task StopAsync(CancellationToken ct)
    {
        foreach (var p in _byPort.Values)
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var p in _byPort.Values) p.Dispose();
        _startLock.Dispose();
    }

    private static string LogName(ServiceOptions o) => o.LocalModule!.Split(':')[0] + ".log";

    private static async Task<bool> IsListeningAsync(Uri u, CancellationToken ct)
    {
        try
        {
            using var c = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(500);
            await c.ConnectAsync(u.Host == "localhost" ? "127.0.0.1" : u.Host, u.Port, cts.Token);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }

    /// <summary>The sidecars folder that contains &lt;module&gt;.py, searched upwards from the app.</summary>
    private string? FindSidecarDir(string module)
    {
        string file = module.Split(':')[0] + ".py";
        foreach (var start in new[] { _contentRoot, AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                var cand = Path.Combine(d.FullName, "sidecars");
                if (File.Exists(Path.Combine(cand, file))) return cand;
            }
        return null;
    }
}
