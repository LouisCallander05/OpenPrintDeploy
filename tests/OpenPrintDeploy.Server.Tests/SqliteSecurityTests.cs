using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace OpenPrintDeploy.Server.Tests;

public sealed class SqliteSecurityTests(ITestOutputHelper output)
{
    [Fact]
    public void LoadedSqliteIncludesCve20256965Fix()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        var version = (string)command.ExecuteScalar()!;
        output.WriteLine("Loaded native SQLite: " + version);
        Assert.True(Version.Parse(version) >= new Version(3, 50, 2), version);
    }
}
