using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace OpenPrintDeploy.Server.Updates;

/// <summary>
/// Compares this server's version against the latest GitHub release. Used by the
/// admin "Check for updates" action. Network failures (a print server with no
/// outbound internet) and API errors are turned into a friendly
/// <see cref="UpdateCheckResult.Error"/> rather than thrown — the check is
/// best-effort and must never take the page down.
/// </summary>
public sealed class UpdateCheckService
{
    private readonly HttpClient _http;
    private readonly UpdateOptions _options;

    public UpdateCheckService(HttpClient http, IOptions<UpdateOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        var current = BuildInfo.Version;

        var repo = _options.GitHubRepo?.Trim();
        if (string.IsNullOrWhiteSpace(repo))
        {
            return UpdateCheckResult.Failed(current, "No update repository is configured (Updates:GitHubRepo).");
        }

        GitHubRelease? release;
        try
        {
            using var response = await _http.GetAsync($"repos/{repo}/releases/latest", ct);
            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed(
                    current, $"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase} for '{repo}'.");
            }

            release = await response.Content.ReadFromJsonAsync<GitHubRelease>(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return UpdateCheckResult.Failed(
                current, "Couldn't reach GitHub — this server may not have outbound internet access.");
        }

        var tag = release?.TagName;
        if (string.IsNullOrWhiteSpace(tag))
        {
            return UpdateCheckResult.Failed(current, "No published release was found for the configured repository.");
        }

        var latest = ParseVersion(tag);
        var running = ParseVersion(current);
        var available = latest is not null && running is not null && latest > running;

        return new UpdateCheckResult
        {
            CurrentVersion = current,
            LatestVersion = tag.TrimStart('v', 'V'),
            UpdateAvailable = available,
            ReleaseUrl = release?.HtmlUrl,
            PublishedAt = release?.PublishedAt,
        };
    }

    /// <summary>
    /// Parses a tag/version like "v0.5.2" or "0.5.2-rc.1+sha" into a comparable
    /// <see cref="Version"/> (the numeric "major.minor[.build[.revision]]" core).
    /// Returns null when it isn't a recognisable version.
    /// </summary>
    private static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var t = s.Trim();
        if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V'))
        {
            t = t[1..];
        }

        var cut = t.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            t = t[..cut];
        }

        return Version.TryParse(t, out var v) ? v : null;
    }

    private sealed record GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; init; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; init; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; init; }
    }
}
