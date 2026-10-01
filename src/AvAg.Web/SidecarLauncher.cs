using System.Diagnostics;
using System.Net.Sockets;

namespace AvAg.Web;

/// <summary>
/// Starts the WhisperX sidecar (sidecars/whisperx_server.py) together with the web app when the configured ASR URL
/// points at this machine and nothing is listening there yet, so F5 in Visual Studio is enough for a full run.
/// Uses sidecars/.venv if present (python on PATH otherwise); the process is stopped when the web app stops.
/// Disable with "AvAg:AutoStartSidecars": false.
/// </summary>
public sealed class SidecarLauncher : IHostedService, IDisposable
{
    private readonly WebSettings _cfg;
    private readonly ILogger<SidecarLauncher> _log;
    private readonly string _contentRoot;
    private Process? _asr;
    private Uri? _asrUri;

    public SidecarLauncher(WebSettings cfg, IWebHostEnvironment env, ILogger<SidecarLauncher> log)
    {
        _cfg = cfg; _log = log; _contentRoot = env.ContentRootPath;
    }

    public Task StartAsync(CancellationToken ct) => EnsureRunningAsync(_cfg.AsrUrl, ct);

    private readonly SemaphoreSlim _startLock = new(1, 1);

    /// <summary>Starts the sidecar for <paramref name="url"/> unless something already listens there (never a second copy:
    /// each instance puts its own model on the GPU).</summary>
    private async Task EnsureRunningAsync(string? url, CancellationToken ct)
    {
        if (!_cfg.AutoStartSidecars || !TryLocal(url, out var uri)) return;
        await _startLock.WaitAsync(ct);
        try { await StartLockedAsync(uri!, ct); } finally { _startLock.Release(); }
    }

    private async Task StartLockedAsync(Uri uri, CancellationToken ct)
    {
        if (await IsListeningAsync(uri, ct)) { _asrUri ??= uri; return; }
        if (_asr is { HasExited: false } && _asrUri?.Port == uri.Port) return; // ours, still starting up
        _asrUri = uri;

        var dir = FindSidecarDir();
        if (dir is null) { _log.LogWarning("sidecars/whisperx_server.py not found; start the WhisperX sidecar manually on {Url}", _asrUri); return; }
        var venvPy = Path.Combine(dir, ".venv", "Scripts", "python.exe");
        var python = File.Exists(venvPy) ? venvPy : "python";
        var logPath = Path.Combine(dir, "whisperx_server.log");

        var psi = new ProcessStartInfo(python)
        {
            WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-m", "uvicorn", "whisperx_server:app", "--host", _asrUri!.Host, "--port", _asrUri.Port.ToString() })
            psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1"; // errors reach whisperx_server.log immediately

        try
        {
            _asr = Process.Start(psi)!;
            var logFile = new StreamWriter(logPath, append: false) { AutoFlush = true };
            _asr.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (logFile) logFile.WriteLine(e.Data); };
            _asr.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (logFile) logFile.WriteLine(e.Data); };
            _asr.Exited += (_, _) => { lock (logFile) logFile.Dispose(); };
            _asr.EnableRaisingEvents = true;
            _asr.BeginOutputReadLine(); _asr.BeginErrorReadLine();
            _log.LogInformation("Started WhisperX sidecar (pid {Pid}) on {Url}; model loading takes ~30 s. Log: {Log}", _asr.Id, _asrUri, logPath);
            for (int i = 0; i < 60 && !_asr.HasExited && !await IsListeningAsync(_asrUri, ct); i++) await Task.Delay(500, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not start WhisperX sidecar with {Python}", python); _asr = null; }
    }

    /// <summary>Before a job: (re)starts the local ASR sidecar if it is not running and waits until it accepts connections.</summary>
    public async Task WaitUntilReadyAsync(string? url, Action<string> progress, CancellationToken ct)
    {
        if (!TryLocal(url, out var u)) return;
        if (await IsListeningAsync(u!, ct)) return;
        progress("starting the WhisperX sidecar…");
        await EnsureRunningAsync(url, ct);
        if (_asr is null || _asrUri is null || u!.Port != _asrUri.Port) return;
        if (await IsListeningAsync(_asrUri, ct)) return;
        var deadline = DateTime.UtcNow.AddMinutes(10); // first start may download the model
        while (DateTime.UtcNow < deadline && !_asr.HasExited)
        {
            await Task.Delay(1000, ct);
            if (await IsListeningAsync(_asrUri, ct)) return;
        }
        if (_asr.HasExited)
            throw new InvalidOperationException($"The WhisperX sidecar exited (code {_asr.ExitCode}); see sidecars/whisperx_server.log.");
    }

    public Task StopAsync(CancellationToken ct)
    {
        try { if (_asr is { HasExited: false }) _asr.Kill(entireProcessTree: true); } catch { /* already gone */ }
        return Task.CompletedTask;
    }

    public void Dispose() => _asr?.Dispose();

    private static bool TryLocal(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Host is not ("127.0.0.1" or "localhost")) return false;
        uri = u; return true;
    }

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

    private string? FindSidecarDir()
    {
        foreach (var start in new[] { _contentRoot, AppContext.BaseDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                var cand = Path.Combine(d.FullName, "sidecars");
                if (File.Exists(Path.Combine(cand, "whisperx_server.py"))) return cand;
            }
        return null;
    }
}
