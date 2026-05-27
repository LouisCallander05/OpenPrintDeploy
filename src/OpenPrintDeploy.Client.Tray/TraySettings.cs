using System.IO;
using System.Text.Json;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>
/// Tray configuration: where the server lives and how often to sync. Read from
/// <c>appsettings.json</c> next to the executable, overridable by the
/// <c>OPD_SERVER_URL</c> environment variable (handy for Intune deployment).
/// </summary>
public sealed class TraySettings
{
    public required Uri ServerBaseAddress { get; init; }

    public required TimeSpan SyncInterval { get; init; }

    public static TraySettings Load()
    {
        var url = Environment.GetEnvironmentVariable("OPD_SERVER_URL");
        var intervalMinutes = 60;

        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("Server", out var server))
            {
                if (string.IsNullOrWhiteSpace(url)
                    && server.TryGetProperty("BaseAddress", out var baseAddress))
                {
                    url = baseAddress.GetString();
                }

                if (server.TryGetProperty("SyncIntervalMinutes", out var interval)
                    && interval.TryGetInt32(out var minutes) && minutes > 0)
                {
                    intervalMinutes = minutes;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                "Server base address is not configured (Server:BaseAddress in appsettings.json or OPD_SERVER_URL).");
        }

        return new TraySettings
        {
            ServerBaseAddress = new Uri(url, UriKind.Absolute),
            SyncInterval = TimeSpan.FromMinutes(intervalMinutes),
        };
    }
}
