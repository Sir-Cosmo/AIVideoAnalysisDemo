using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using AvAg.Pipeline.Services;

namespace AvAg.Web;

/// <summary>
/// Starts local Python sidecars (e.g. sidecars/whisperx_server.py) on demand, so F5 in Visual Studio is enough for a
/// full run. A service is started when its options name a <see cref="ServiceOptions.LocalModule"/>, its Url points at
/// this machine, and nothing listens there yet – never a second copy, because every instance loads its own model onto
/// the GPU. Uses sidecars/.venv if present (python on PATH otherwise); output goes to sidecars/&lt;module&gt;.log and
/// the processes stop with the web app. Disable with "AvAg:AutoStartSidecars": false.
/// </summary>
public sealed class SidecarLauncher : IHostedService, IDisposable
{
    private readonly WebSettings _settings;
    private readonly ILogger<SidecarLauncher> _log;
    private readonly string _contentRoot;
    private readonly ConcurrentDictionary<int, Process> _byPort = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);

    public SidecarLauncher(WebSettings settings, IWebHostEnvironment env, ILogger<SidecarLauncher> log)
    {
        _settings = settings; _log = log; _contentRoot = env.ContentRootPath;
    }

    /// <summary>At start-up: every configured service with a local module.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var s = _settings.Services;
        foreach (var o in new[] { s.Asr, s.UiParser, s.Grounder, s.Tracker, s.ClipDescriber, s.TextGenerator })
            await EnsureStartedAsync(o, ct);
    }

    /// <summary>Before a job: (re)starts the service if needed and waits until it accepts connections.</summary>
    public async Task EnsureReadyAsync(ServiceOptions o, Action<string> progress, CancellationToken ct)
    {
        if (!IsManaged(o, out var uri) || await IsListeningAsync(uri, ct)) return;
        progress($"starting the {o.LocalModule} sidecar…");
        await EnsureStartedAsync(o, ct);
        if (!_byPort.TryGetValue(uri.Port, out var p)) return;
        var deadline = DateTime.UtcNow.AddMinutes(10); // a first start may download the model
        while (DateTime.UtcNow < deadline && !p.HasExited)
        {
            if (await IsListeningAsync(uri, ct)) return;
            await Task.Delay(1000, ct);
        }
        if (p.HasExited) throw new InvalidOperationException($"The {o.LocalModule} sidecar exited (code {p.ExitCode}); see {LogName(o)} in the sidecars folder.");
    }

    private bool IsManaged(ServiceOptions o, out Uri uri)
    {
        uri = null!;
        if (!_settings.AutoStartSidecars || string.IsNullOrWhiteSpace(o.LocalModule) || o.Provider == ServiceOptions.None) return false;
        if (!Uri.TryCreate(o.Url, UriKind.Absolute, out var u) || u.Host is not ("127.0.0.1" or "localhost")) return false;
        uri = u;
        return true;
    }

    private async Task EnsureStartedAsync(ServiceOptions o, CancellationToken ct)
    {
        if (!IsManaged(o, out var uri)) return;
        await _startLock.WaitAsync(ct);
        try
        {
            if (await IsListeningAsync(uri, ct)) return;
            if (_byPort.TryGetValue(uri.Port, out var running) && !running.HasExited) return; // ours, still starting
            Start(o, uri);
            // The sidecar binds its port first and loads the model in the background.
            if (_byPort.TryGetValue(uri.Port, out var p))
                for (int i = 0; i < 60 && !p.HasExited && !await IsListeningAsync(uri, ct); i++) await Task.Delay(500, ct);
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
