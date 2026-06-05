using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FileNinja.FlowLauncher.Ipc;

/// <summary>
/// One-shot async named-pipe client for the FileNinja IPC server.
/// Each call opens a fresh connection, sends a single request line, reads
/// a single response line, and closes. This keeps the design dead simple
/// and avoids long-lived state across Flow.Launcher query invocations.
/// </summary>
internal sealed class IpcClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _pipeName;

    public IpcClient() : this(null) { }

    /// <summary>
    /// Test seam: pass a custom pipe name. Production code uses the parameterless
    /// constructor which resolves the per-user pipe name.
    /// </summary>
    public IpcClient(string? pipeName)
    {
        _pipeName = string.IsNullOrEmpty(pipeName)
            ? "FileNinja.Plugin." + Environment.UserName
            : pipeName!;
    }

    public string PipeName => _pipeName;

    public async Task<IpcResponse> SendAsync(string method, IpcParams? p, int connectTimeoutMs, CancellationToken ct)
    {
        var req = new IpcRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            Method = method,
            Params = p
        };

        await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(connectTimeoutMs, ct).ConfigureAwait(false);

        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

        var line = JsonSerializer.Serialize(req, JsonOpts);
        await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);

        var responseLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (responseLine == null)
            throw new IOException("FileNinja IPC: connection closed before response.");

        var resp = JsonSerializer.Deserialize<IpcResponse>(responseLine, JsonOpts)
                   ?? throw new IOException("FileNinja IPC: empty response.");
        return resp;
    }

    /// <summary>
    /// True when an alive FileNinja instance is reachable (sub-second probe).
    /// </summary>
    public async Task<bool> IsAliveAsync(CancellationToken ct)
    {
        try
        {
            var r = await SendAsync("ping", null, connectTimeoutMs: 300, ct).ConfigureAwait(false);
            return r.Ok;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort launch of FileNinja.exe when no instance is responding.
    /// Resolves the exe from the configured path; otherwise relies on PATH.
    /// </summary>
    public bool TryStartFileNinja(string? exePathOverride)
    {
        var exe = !string.IsNullOrWhiteSpace(exePathOverride) && File.Exists(exePathOverride)
            ? exePathOverride
            : "FileNinja.exe";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
