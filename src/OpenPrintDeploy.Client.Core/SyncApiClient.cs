using System.Net;
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
        return result ?? new SyncResponseDto([]);
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

    /// <summary>
    /// Builds an <see cref="HttpClient"/> that authenticates with an explicit
    /// domain account instead of the signed-in user. This is the non-domain-joined
    /// path: SSPI has no Kerberos ticket for the supplied account, so Negotiate
    /// falls back to NTLM with these credentials, and the (domain-joined) server
    /// validates the NTLM response against a domain controller. The credential's
    /// <c>UserName</c> should be <c>DOMAIN\user</c> or <c>user@domain</c>.
    /// </summary>
    public static HttpClient CreateExplicitCredentialsClient(Uri baseAddress, NetworkCredential credential)
    {
        // Scope the credential to this server + Negotiate so it's never offered
        // anywhere else, and so a default-credentials handler isn't used instead.
        var cache = new CredentialCache { { baseAddress, "Negotiate", credential } };
        var handler = new HttpClientHandler { Credentials = cache };
        return new HttpClient(handler) { BaseAddress = baseAddress };
    }
}
