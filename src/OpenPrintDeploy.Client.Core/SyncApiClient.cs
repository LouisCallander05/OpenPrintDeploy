using System.Net.Http.Json;
using OpenPrintDeploy.Shared.Sync;

namespace OpenPrintDeploy.Client.Core;

/// <summary>
/// Talks to the server's <c>/sync</c> endpoint. The supplied <see cref="HttpClient"/>
/// should authenticate as the signed-in user (Negotiate) — see
/// <see cref="CreateDefaultCredentialsClient"/>. Transport/HTTP failures surface
/// as exceptions so the caller can keep the user's current printers and retry.
/// </summary>
public sealed class SyncApiClient
{
    private readonly HttpClient _http;

    public SyncApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<SyncResponseDto> FetchAsync(string? machineName, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("sync", new SyncRequestDto(machineName), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SyncResponseDto>(ct);
        return result ?? new SyncResponseDto([], null);
    }

    /// <summary>
    /// Builds an <see cref="HttpClient"/> that sends the calling user's Windows
    /// credentials (Kerberos/NTLM) — the basis for "sync as the signed-in user".
    /// </summary>
    public static HttpClient CreateDefaultCredentialsClient(Uri baseAddress)
    {
        var handler = new HttpClientHandler { UseDefaultCredentials = true };
        return new HttpClient(handler) { BaseAddress = baseAddress };
    }
}
