using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace OpenPrintDeploy.Server.Data;

/// <summary>
/// Applies the per-connection SQLite PRAGMAs a multi-client fleet needs, on every
/// connection open:
/// <list type="bullet">
///   <item><c>busy_timeout</c> — wait (rather than fail with SQLITE_BUSY) when a
///   write is briefly blocked by another connection. Hundreds of clients syncing
///   at logon serialise instead of dropping audit rows.</item>
///   <item><c>journal_mode=WAL</c> — readers don't block the writer and vice
///   versa; far better concurrency than the default rollback journal. WAL is a
///   persistent property of the database file, but re-asserting it per open is
///   cheap and harmless (and a no-op for in-memory databases).</item>
///   <item><c>synchronous=NORMAL</c> — the safe, recommended pairing with WAL:
///   durable across application crashes, with far fewer fsyncs than FULL.</item>
/// </list>
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const int BusyTimeoutMs = 5000;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection);

    public override Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Apply(connection);
        return Task.CompletedTask;
    }

    private static void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA busy_timeout = {BusyTimeoutMs};" +
            "PRAGMA journal_mode = WAL;" +
            "PRAGMA synchronous = NORMAL;";
        command.ExecuteNonQuery();
    }
}
