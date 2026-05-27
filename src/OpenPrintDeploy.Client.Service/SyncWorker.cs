namespace OpenPrintDeploy.Client.Service;

public sealed class SyncWorker : BackgroundService
{
    private readonly ILogger<SyncWorker> _logger;

    public SyncWorker(ILogger<SyncWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("SyncWorker tick at {Now}", DateTimeOffset.Now);
            await Task.Delay(TimeSpan.FromMinutes(60), stoppingToken);
        }
    }
}
