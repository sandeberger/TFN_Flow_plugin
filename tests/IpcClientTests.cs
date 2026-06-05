using FileNinja.FlowLauncher.Ipc;

namespace FileNinja.FlowLauncher.Tests;

public class IpcClientTests
{
    [Fact]
    public void DefaultPipeName_IncludesUsername()
    {
        var client = new IpcClient();
        client.PipeName.Should().StartWith("FileNinja.Plugin.");
        client.PipeName.Should().EndWith(Environment.UserName);
    }

    [Fact]
    public async Task SendAsync_HappyPath_ParsesResponse()
    {
        await using var server = new FakePipeServer(req =>
        {
            req.Method.Should().Be("ping");
            return new IpcResponse
            {
                Id = req.Id,
                Ok = true,
                Result = new IpcResult { Version = 1, Pid = 1234, Ready = true }
            };
        });
        var client = new IpcClient(server.PipeName);

        var resp = await client.SendAsync("ping", null, 2000, CancellationToken.None);

        resp.Ok.Should().BeTrue();
        resp.Result!.Version.Should().Be(1);
        resp.Result.Pid.Should().Be(1234);
        resp.Result.Ready.Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_ServerError_SurfacesAsResponseError()
    {
        await using var server = new FakePipeServer(req => new IpcResponse
        {
            Id = req.Id,
            Ok = false,
            Error = "intentional failure"
        });
        var client = new IpcClient(server.PipeName);

        var resp = await client.SendAsync("list", new IpcParams { Path = "qry://*.boom" }, 2000, CancellationToken.None);

        resp.Ok.Should().BeFalse();
        resp.Error.Should().Be("intentional failure");
    }

    [Fact]
    public async Task SendAsync_ForwardsAllParams()
    {
        IpcRequest? captured = null;
        await using var server = new FakePipeServer(req =>
        {
            captured = req;
            return new IpcResponse { Id = req.Id, Ok = true, Result = new IpcResult() };
        });
        var client = new IpcClient(server.PipeName);

        await client.SendAsync("navigate", new IpcParams
        {
            Path = @"C:\src",
            Pane = "right",
            NewTab = true,
        }, 2000, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Method.Should().Be("navigate");
        captured.Params!.Path.Should().Be(@"C:\src");
        captured.Params.Pane.Should().Be("right");
        captured.Params.NewTab.Should().BeTrue();
    }

    [Fact]
    public async Task SendAsync_WhenNoServer_ThrowsTimeout()
    {
        // No FakePipeServer created → no listener on the named pipe.
        var client = new IpcClient("FileNinja.PluginTest.Missing." + Guid.NewGuid().ToString("N"));

        var act = async () => await client.SendAsync("ping", null, connectTimeoutMs: 200, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task SendAsync_WhenServerClosesImmediately_ThrowsIO()
    {
        await using var server = new FakePipeServer(FakePipeServer.Mode.CloseImmediately);
        var client = new IpcClient(server.PipeName);

        var act = async () => await client.SendAsync("ping", null, 2000, CancellationToken.None);

        // Either "connection closed before response" or a low-level IO error.
        // Both signal "talk to FileNinja again" to the plugin caller.
        var ex = await act.Should().ThrowAsync<Exception>();
        ex.Which.Should().Match(e => e is IOException || e is System.Text.Json.JsonException);
    }

    [Fact]
    public async Task SendAsync_WhenServerSendsMalformedJson_Throws()
    {
        await using var server = new FakePipeServer(FakePipeServer.Mode.Malformed);
        var client = new IpcClient(server.PipeName);

        var act = async () => await client.SendAsync("ping", null, 2000, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task IsAliveAsync_ReturnsTrue_WhenServerReplies()
    {
        await using var server = new FakePipeServer(req => new IpcResponse
        {
            Id = req.Id,
            Ok = true,
            Result = new IpcResult { Version = 1, Pid = 1, Ready = true }
        });
        var client = new IpcClient(server.PipeName);

        (await client.IsAliveAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsAliveAsync_ReturnsFalse_WhenNoServer()
    {
        var client = new IpcClient("FileNinja.PluginTest.Missing." + Guid.NewGuid().ToString("N"));
        (await client.IsAliveAsync(CancellationToken.None)).Should().BeFalse();
    }
}
