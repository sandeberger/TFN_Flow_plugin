using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FileNinja.FlowLauncher.Ipc;

namespace FileNinja.FlowLauncher.Tests;

/// <summary>
/// In-process named-pipe stand-in for the FileNinja IPC server. The client tests
/// hand it a function that maps request → response (or asks it to "go silent",
/// "hang up", etc.) so each test can pin down the exact server behavior it cares
/// about without spinning up the real WPF app.
/// </summary>
internal sealed class FakePipeServer : IAsyncDisposable
{
    public enum Mode
    {
        /// <summary>Behave correctly: read one line, write one valid response line.</summary>
        Normal,
        /// <summary>Accept the connection, never reply, hold the line open.</summary>
        Hang,
        /// <summary>Accept the connection, write a non-JSON byte sequence, close.</summary>
        Malformed,
        /// <summary>Accept the connection, close immediately without sending anything.</summary>
        CloseImmediately
    }

    private readonly string _pipeName;
    private readonly Func<IpcRequest, IpcResponse>? _handler;
    private readonly Mode _mode;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public string PipeName => _pipeName;
    public int ConnectionsServed { get; private set; }

    public FakePipeServer(Func<IpcRequest, IpcResponse> handler, Mode mode = Mode.Normal)
    {
        _pipeName = "FileNinja.PluginTest." + Guid.NewGuid().ToString("N");
        _handler = handler;
        _mode = mode;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public FakePipeServer(Mode mode) : this(_ => throw new InvalidOperationException("handler not used"), mode)
    {
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName,
                    PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                ConnectionsServed++;

                switch (_mode)
                {
                    case Mode.CloseImmediately:
                        pipe.Dispose();
                        break;

                    case Mode.Hang:
                        // Hold the connection open until the test disposes us.
                        await Task.Delay(Timeout.Infinite, _cts.Token).ConfigureAwait(false);
                        break;

                    case Mode.Malformed:
                        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true))
                        await using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
                        {
                            // Drain the request first so the client's WriteLineAsync completes
                            // even when running in synchronous flow over a small pipe buffer.
                            await reader.ReadLineAsync().ConfigureAwait(false);
                            await writer.WriteLineAsync("not-json-at-all").ConfigureAwait(false);
                        }
                        break;

                    case Mode.Normal:
                        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true))
                        await using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true })
                        {
                            var line = await reader.ReadLineAsync().ConfigureAwait(false);
                            if (line == null) break;

                            var req = JsonSerializer.Deserialize<IpcRequest>(line) ?? new IpcRequest();
                            var resp = _handler!(req);
                            var json = JsonSerializer.Serialize(resp);
                            await writer.WriteLineAsync(json).ConfigureAwait(false);
                        }
                        break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* swallow — tests assert on client-side behavior */ }
            finally { pipe?.Dispose(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }
}
