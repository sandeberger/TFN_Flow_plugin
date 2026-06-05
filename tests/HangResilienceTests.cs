using System.Diagnostics;
using FileNinja.FlowLauncher.Ipc;
using Flow.Launcher.Plugin;

namespace FileNinja.FlowLauncher.Tests;

/// <summary>
/// "Did we accidentally make Flow.Launcher freeze" tests. Each one establishes
/// a wall-clock budget — if these regress, the user sees a stalled launcher.
/// </summary>
public class HangResilienceTests
{
    private const int FastBudgetMs = 100;

    /// <summary>InitAsync must be effectively instant — Flow calls it on plugin discovery.</summary>
    [Fact]
    public async Task InitAsync_ReturnsImmediately()
    {
        var main = new Main();
        var sw = Stopwatch.StartNew();
        await main.InitAsync(new PluginInitContext());
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(FastBudgetMs);
    }

    /// <summary>
    /// QueryAsync against an unreachable FileNinja must complete in bounded time.
    /// The plugin uses connectTimeoutMs=1500 internally; we allow some slack but
    /// must not see anything near "hangs forever".
    /// </summary>
    [Fact(Timeout = 5000)]
    public async Task QueryAsync_WhenNoFileNinja_BoundedByConnectTimeout()
    {
        var main = new Main(new IpcClient("FileNinja.PluginTest.Unreachable." + Guid.NewGuid().ToString("N")));
        await main.InitAsync(new PluginInitContext());

        var sw = Stopwatch.StartNew();
        var results = await main.QueryAsync("*.cs", CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(3000,
            "QueryAsync must surface a 'FileNinja not running' hint quickly, not hang Flow.");
        results.Should().NotBeEmpty("plugin should always return at least a hint result so Flow has something to render.");
    }

    /// <summary>
    /// Cancellation from Flow (user kept typing) must take effect promptly. Without
    /// the token being honored, fast typing piles up 1500-ms blocking queries and
    /// Flow appears frozen.
    /// </summary>
    [Fact(Timeout = 5000)]
    public async Task QueryAsync_HonoursCancellationToken()
    {
        await using var hangingServer = new FakePipeServer(FakePipeServer.Mode.Hang);
        var main = new Main(new IpcClient(hangingServer.PipeName));
        await main.InitAsync(new PluginInitContext());

        using var cts = new CancellationTokenSource(150);
        var sw = Stopwatch.StartNew();

        try
        {
            await main.QueryAsync("lookup", cts.Token);
        }
        catch (OperationCanceledException) { /* fine — also a valid cancel signal */ }
        catch (TimeoutException) { /* also fine — connect timed out before cancellation propagated */ }

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(2500,
            "the query must observe cancellation (or its own timeout) instead of holding Flow's UI thread.");
    }

    /// <summary>
    /// Flow fires QueryAsync on every keystroke; we shouldn't deadlock when many
    /// queries are in flight simultaneously. This catches accidental lock contention
    /// or shared mutable state.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task QueryAsync_ManyParallel_AllComplete()
    {
        await using var server = new FakePipeServer(req => new IpcResponse
        {
            Id = req.Id,
            Ok = true,
            Result = new IpcResult { Items = new() }
        });
        var main = new Main(new IpcClient(server.PipeName));
        await main.InitAsync(new PluginInitContext());

        const int N = 10;
        var tasks = Enumerable.Range(0, N)
            .Select(i => main.QueryAsync("q" + i, CancellationToken.None))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        results.Should().HaveCount(N);
        results.Should().OnlyContain(r => r != null);
    }

    /// <summary>
    /// An empty query is the very first thing Flow asks for — the plugin must
    /// answer without ever touching the network.
    /// </summary>
    [Fact(Timeout = 1000)]
    public async Task QueryAsync_EmptyString_NeverTouchesIpc()
    {
        // Use a pipe that, if hit, would hang forever. The empty-query path
        // must short-circuit before any IPC happens.
        var main = new Main(new IpcClient("FileNinja.PluginTest.MustNotBeHit." + Guid.NewGuid().ToString("N")));
        await main.InitAsync(new PluginInitContext());

        var sw = Stopwatch.StartNew();
        var results = await main.QueryAsync("", CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(50);
        results.Should().NotBeEmpty();
    }
}
