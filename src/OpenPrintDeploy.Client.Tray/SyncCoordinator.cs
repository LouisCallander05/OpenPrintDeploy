using System.Net;
using System.Net.Http;
using OpenPrintDeploy.Client.Core;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>Outcome of one sync attempt, for surfacing a tray notification.</summary>
public readonly record struct SyncOutcome(
    bool Ok,
    int PrinterCount,
    IReadOnlyList<string> AddedNames,
    IReadOnlyList<string> FailedNames,
    string? Error)
{
    public static SyncOutcome Success(int count, IReadOnlyList<string> addedNames, IReadOnlyList<string> failedNames)
        => new(true, count, addedNames, failedNames, null);

    public static SyncOutcome Failure(string error) => new(false, 0, [], [], error);
}

/// <summary>
/// Owns the HTTP client and runs one sync cycle on demand. The client's
/// authentication (signed-in user vs explicit domain credentials) is decided by
/// <see cref="TrayAuthenticator"/>. Failures are caught and returned, never
/// thrown, so the caller can notify the user and keep installed printers in
/// place when the server or a domain controller is unreachable. A 401 triggers a
/// one-shot re-auth with explicit domain credentials.
/// </summary>
public sealed class SyncCoordinator : IDisposable
{
    private readonly TrayAuthenticator _auth;
    private readonly ManagedStateStore _state;
    private readonly string _machineName;

    private HttpClient? _http;
    private SyncOrchestrator? _orchestrator;

    public SyncCoordinator(TrayAuthenticator auth)
    {
        _auth = auth;
        _state = new ManagedStateStore();
        _machineName = Environment.MachineName;
    }

    public async Task<SyncOutcome> RunOnceAsync(CancellationToken ct = default)
    {
        if (!EnsureClient())
        {
            return SyncOutcome.Failure("Sign-in required — open the tray menu and choose “Sign in…”.");
        }

        try
        {
            return await SyncWithCurrentClientAsync(ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Integrated auth (or the stored credentials) was rejected. Offer a
            // sign-in and retry once with explicit domain credentials.
            var refreshed = _auth.ReAuthenticate(
                "The server rejected the current credentials. Enter a domain account.");
            if (refreshed is null)
            {
                return SyncOutcome.Failure("Sign-in required — the server rejected the current credentials.");
            }

            SwapClient(refreshed);
            try
            {
                return await SyncWithCurrentClientAsync(ct);
            }
            catch (Exception retryEx) when (retryEx is not OperationCanceledException)
            {
                return SyncOutcome.Failure(DescribeError(retryEx));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SyncOutcome.Failure(DescribeError(ex));
        }
    }

    /// <summary>
    /// Turns an exception into a user-facing message. A TLS trust failure (the
    /// common first-run snag with a self-signed server cert that the client
    /// hasn't been told to trust) gets actionable guidance instead of the
    /// framework's opaque "The SSL connection could not be established".
    /// </summary>
    private static string DescribeError(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Security.Authentication.AuthenticationException)
            {
                return "Couldn’t establish a trusted secure (TLS) connection — the server’s certificate "
                     + "isn’t trusted on this PC. If the server uses its self-signed certificate, pin its "
                     + "thumbprint (CERTTHUMBPRINT when installing the client, or set the registry value "
                     + "HKLM\\SOFTWARE\\OpenPrintDeploy\\Client\\ServerCertificateThumbprint and restart), "
                     + "or add the certificate to this PC’s Trusted Root store.";
            }
        }

        return ex.Message;
    }

    /// <summary>Manual "Sign in…" tray action: prompt and switch to those credentials.</summary>
    public void SignIn()
    {
        var client = _auth.SignInInteractive();
        if (client is not null)
        {
            SwapClient(client);
        }
    }

    /// <summary>Clears stored credentials and drops the current HTTP client.</summary>
    public void SignOut()
    {
        _auth.SignOut();
        _http?.Dispose();
        _http = null;
        _orchestrator = null;
    }

    private async Task<SyncOutcome> SyncWithCurrentClientAsync(CancellationToken ct)
    {
        var managed = _state.Load();
        var result = await _orchestrator!.SyncOnceAsync(_machineName, managed, ct);
        _state.Save(result.ManagedPrinters);
        return SyncOutcome.Success(result.ManagedPrinters.Count, result.AddedNames, result.FailedNames);
    }

    private bool EnsureClient()
    {
        if (_orchestrator is not null)
        {
            return true;
        }

        var client = _auth.CreateClient();
        if (client is null)
        {
            return false;
        }

        _http = client;
        _orchestrator = new SyncOrchestrator(new SyncApiClient(_http), new WindowsPrinterApplier());
        return true;
    }

    private void SwapClient(HttpClient client)
    {
        _http?.Dispose();
        _http = client;
        _orchestrator = new SyncOrchestrator(new SyncApiClient(_http), new WindowsPrinterApplier());
    }

    public void Dispose() => _http?.Dispose();
}
