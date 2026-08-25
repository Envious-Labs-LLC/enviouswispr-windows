using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace EnviousWispr.Polish;

public sealed record EgOneEndpoint(string Url, string ApiKey, int Port);

/// Spawns, waits on, and kills the local llama-server hosting EG-1.
/// Contract ported from EGOneServerManager.swift:
///   llama-server <shard1.gguf> --host 127.0.0.1 --port <free> -c <ctx>
///              --api-key <token> -fa on --cache-type-k q8_0 --cache-type-v q8_0
/// (flash attention + q8 KV cache = the Mac's measured 4.1 GB footprint config;
///  multi-shard GGUFs are followed by llama.cpp via sibling files, so only the
///  entrypoint shard is passed).
public sealed class EgOneServer : IAsyncDisposable
{
    private Process? _proc;

    public EgOneEndpoint? Endpoint { get; private set; }
    public bool IsRunning => _proc is { HasExited: false };
    public event Action<string>? Log;

    /// Random free loopback port. Port 8081 is the Qwen control plane —
    /// never touch it; project llama.cpp servers run on 8082+.
    private static int FindFreePort()
    {
        while (true)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            if (port != 8081 && port >= 8082) return port;
        }
    }

    /// Waits until the server answers /health (model load takes 10-60 s).
    public async Task<bool> StartAsync(string serverExe, string modelShardPath, int contextTokens,
        int timeoutSeconds, CancellationToken ct = default)
    {
        if (!File.Exists(serverExe))
        {
            Log?.Invoke($"llama-server not found at {serverExe}");
            return false;
        }
        if (!File.Exists(modelShardPath))
        {
            Log?.Invoke($"EG-1 shard not found at {modelShardPath}");
            return false;
        }

        var port = FindFreePort();
        var token = Guid.NewGuid().ToString() + Guid.NewGuid().ToString();

        var psi = new ProcessStartInfo
        {
            FileName = serverExe,
            WorkingDirectory = Path.GetDirectoryName(modelShardPath)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // b10615 llama.cpp no longer accepts a bare positional model path —
        // the parser rejects it with "invalid argument: <path>" (MEASURED:
        // identical argv worked with --model, failed positional).
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(modelShardPath);
        psi.ArgumentList.Add("--host"); psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(contextTokens.ToString());
        psi.ArgumentList.Add("--api-key"); psi.ArgumentList.Add(token);
        psi.ArgumentList.Add("-fa"); psi.ArgumentList.Add("on");
        psi.ArgumentList.Add("--cache-type-k"); psi.ArgumentList.Add("q8_0");
        psi.ArgumentList.Add("--cache-type-v"); psi.ArgumentList.Add("q8_0");

        // Log the exact argv so launch failures are debuggable from the app log.
        Log?.Invoke($"llama-server argv: {string.Join(" ", psi.ArgumentList)}");

        _proc = new Process { StartInfo = psi };
        _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke($"[llama] {e.Data}"); };
        _proc.OutputDataReceived += (_, e) => { if (e.Data != null && e.Data.Contains("error", StringComparison.OrdinalIgnoreCase)) Log?.Invoke($"[llama] {e.Data}"); };
        _proc.Start();
        _proc.BeginErrorReadLine();
        _proc.BeginOutputReadLine();

        Log?.Invoke($"EG-1 server starting on 127.0.0.1:{port}");
        Endpoint = new EgOneEndpoint($"http://127.0.0.1:{port}/v1/chat/completions", token, port);

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        while (DateTime.UtcNow < deadline && !_proc.HasExited)
        {
            try
            {
                var resp = await http.GetAsync($"http://127.0.0.1:{port}/health", ct);
                if (resp.IsSuccessStatusCode)
                {
                    Log?.Invoke("EG-1 server ready");
                    return true;
                }
            }
            catch (HttpRequestException) { /* not up yet */ }
            catch (TaskCanceledException) { /* per-request timeout while loading */ }
            await Task.Delay(500, ct);
        }

        Log?.Invoke(_proc.HasExited
            ? $"EG-1 server exited early (code {_proc.ExitCode})"
            : "EG-1 server did not become healthy in time");
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_proc is not { HasExited: false }) return;
        try
        {
            _proc.Kill(entireProcessTree: true);
            await _proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch { /* already gone */ }
    }
}
