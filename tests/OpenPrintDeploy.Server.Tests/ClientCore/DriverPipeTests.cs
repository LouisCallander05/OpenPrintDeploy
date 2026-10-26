using System.IO.Pipes;
using OpenPrintDeploy.Shared.Drivers;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class DriverPipeTests
{
    [Fact]
    public async Task ClientHoldingConnectionOpenIsCancelledAndPipeCanBeReused()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var name = "opd-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            var connected = server.WaitForConnectionAsync(deadline.Token);
            await client.ConnectAsync(deadline.Token);
            await connected;
            using var responseDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var reply = DriverWire.WriteResponseAsync(server, new(false, "test error"), responseDeadline.Token);
            Assert.Equal("test error", (await DriverWire.ReadAsync<DriverResponse>(client, deadline.Token)).Error);
            Assert.False(reply.IsCompleted);
            responseDeadline.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reply);
            server.Disconnect();
        }
        using var next = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var reconnected = server.WaitForConnectionAsync(deadline.Token);
        await next.ConnectAsync(deadline.Token);
        await reconnected;
        var nextReply = DriverWire.WriteResponseAsync(server, new(true), deadline.Token);
        Assert.True((await DriverWire.ReadAsync<DriverResponse>(next, deadline.Token)).Ready);
        next.Dispose();
        await nextReply;
        server.Disconnect();
    }

    [Fact]
    public async Task ExtraRequestDataIsRejected()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var name = "opd-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);
        await connected;
        var reply = DriverWire.WriteResponseAsync(server, new(true), deadline.Token);
        await DriverWire.ReadAsync<DriverResponse>(client, deadline.Token);
        await client.WriteAsync(new byte[] { 1 }, deadline.Token);
        await Assert.ThrowsAsync<InvalidDataException>(() => reply);
        server.Disconnect();
    }

    [Fact]
    public async Task DelayedReaderReceivesCompleteResponseAcrossReusedConnections()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var name = "opd-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096);
        var expected = new DriverResponse(true, Driver: new("Test driver", "Windows x64", 42, 123));
        for (var i = 0; i < 5; i++)
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            var connected = server.WaitForConnectionAsync(deadline.Token);
            await client.ConnectAsync(deadline.Token);
            await connected;
            var reply = ReplyAndDisconnect();
            // The old implementation disconnects while the response is still buffered.
            await Task.Delay(50, deadline.Token);
            Assert.Equal(expected, await DriverWire.ReadAsync<DriverResponse>(client, deadline.Token));
            client.Dispose();
            await reply;
        }

        async Task ReplyAndDisconnect()
        {
            try { await DriverWire.WriteResponseAsync(server, expected, deadline.Token); }
            finally { server.Disconnect(); }
        }
    }
}
