using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenPrintDeploy.Server.Data;

namespace OpenPrintDeploy.Server.Sync;

public sealed class ClientActivityRetentionService : BackgroundService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IOptions<ClientActivityOptions> _options;
    private readonly ILogger<ClientActivityRetentionService> _logger;

    public ClientActivityRetentionService(
        IDbContextFactory<AppDbContext> dbFactory,
        IOptions<ClientActivityOptions> options,
        ILogger<ClientActivityRetentionService> logger)
    {
        _dbFactory = dbFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var days = Math.Clamp(_options.Value.RetentionDays, 1, 3650);
                var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
                await using var db = await _dbFactory.CreateDbContextAsync(stoppingToken);
                // SQLite stores DateTimeOffset as text and cannot translate all
                // ordering/comparison operations. Activity is already bounded,
                // so select timestamps and perform the cutoff comparison here.
                var candidates = await db.ClientActivities.ToListAsync(stoppingToken);
                var expired = candidates.Where(a => a.OccurredAt < cutoff).ToList();
                db.ClientActivities.RemoveRange(expired);
                await db.SaveChangesAsync(stoppingToken);
                var removed = expired.Count;
                if (removed > 0)
                {
                    _logger.LogInformation("Removed {Count} expired client activity entries.", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Client activity retention cleanup failed; it will retry tomorrow.");
            }

            await Task.Delay(TimeSpan.FromDays(1), stoppingToken);
        }
    }
}
