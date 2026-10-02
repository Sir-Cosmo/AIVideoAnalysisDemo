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
        foreach (var o in _settings.Services.All)
            if (IsManaged(o, out var uri)) await StartIfNeededAsync(o, uri, ct);
    }

    /// <summary>
    /// Before a job: (re)starts every managed sidecar the job uses and waits until each configured (primary) service
    /// accepts connections. Fallback sidecars are only started, never waited for: a broken or slowly loading fallback
    /// must not hold up or fail a job whose primary service works – if the fallback is needed, its own error is reported.
    /// </summary>
    public async Task EnsureReadyAsync(AiServicesOptions services, Action<string> progress, CancellationToken ct)
    {
        foreach (var primary in services.Primaries)
        foreach (var o in primary.WithFallbacks())
        {
            if (!IsManaged(o, out var uri) || await IsListeningAsync(uri, ct)) continue;
            bool isFallback = !ReferenceEquals(o, primary);
            progress(isFallback ? $"starting the {o.LocalModule} sidecar (fallback) in the background…" : $"starting the {o.LocalModule} sidecar…");
            await StartIfNeededAsync(o, uri, ct);
            if (isFallback || !_byPort.TryGetValue(uri.Port, out var p)) continue; // could not be started – the job reports the connection error
            var deadline = DateTime.UtcNow + ReadyTimeout;
            while (!await IsListeningAsync(uri, ct))
            {
                if (p.HasExited) throw new InvalidOperationException($"The {o.LocalModule} sidecar exited (code {p.ExitCode}); see {LogName(o)} in the sidecars folder.");
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"The {o.LocalModule} sidecar did not start within {ReadyTimeout.TotalMinutes:0} min.");
                await Task.Delay(1000, ct);
            }
        }
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
