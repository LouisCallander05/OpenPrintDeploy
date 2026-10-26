using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenPrintDeploy.Client.Core;
using Xunit;

namespace OpenPrintDeploy.Server.Tests.ClientCore;

public sealed class SyncTransportSecurityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CredentialClients_DoNotFollowRedirects(bool explicitCredentials)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptTcpClientAsync(stop.Token);
                    await using var stream = connection.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                    var count = Interlocked.Increment(ref received);
                    var reply = count == 1
                        ? $"HTTP/1.1 307 Temporary Redirect\r\nLocation: {origin}other\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        : "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            using var client = explicitCredentials
                ? SyncApiClient.CreateExplicitCredentialsClient(origin, new NetworkCredential("test", "test"))
                : SyncApiClient.CreateDefaultCredentialsClient(origin);
            using var response = await client.GetAsync("sync", stop.Token);
            Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
            Assert.Equal(1, received);
        }
        finally { stop.Cancel(); await server; }
    }
}
