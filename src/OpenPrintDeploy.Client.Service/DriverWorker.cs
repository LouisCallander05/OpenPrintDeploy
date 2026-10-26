using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenPrintDeploy.Shared.Drivers;

namespace OpenPrintDeploy.Client.Service;

internal sealed class DriverWorker(DriverInstaller installer, ILogger<DriverWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // One installation at a time across all sessions. Bounded reads prevent
        // an idle client from monopolizing the pipe indefinitely.
        var security = new PipeSecurity();
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // Keep the first instance reserved across requests; don't leave a gap
        // where another local process can claim the service name.
        using var pipe = NamedPipeServerStreamAcl.Create(DriverProtocol.PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
        while (!stoppingToken.IsCancellationRequested)
        {
            await pipe.WaitForConnectionAsync(stoppingToken);
            try
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var request = await DriverWire.ReadAsync<DriverRequest>(pipe, readTimeout.Token);
                DriverResponse response;
                try
                {
                    using var jobTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    jobTimeout.CancelAfter(TimeSpan.FromMinutes(4));
                    response = await installer.PrepareAsync(request, jobTimeout.Token);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Driver preparation failed for {Queue}", request.UncPath);
                    response = new(false, ex is OperationCanceledException ? "Driver preparation timed out; retry sync." : ex.Message);
                }
                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                writeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await DriverWire.WriteAsync(pipe, response, writeTimeout.Token);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Driver client disconnected or sent an invalid request");
            }
            finally { pipe.Disconnect(); }
        }
    }
}
