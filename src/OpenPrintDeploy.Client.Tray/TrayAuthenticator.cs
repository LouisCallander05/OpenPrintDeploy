using System.Net;
using System.Net.Http;
using OpenPrintDeploy.Client.Core;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>
/// Decides how the tray authenticates to the server and builds the matching
/// <see cref="HttpClient"/>:
/// <list type="bullet">
///   <item>Domain- or Entra-joined → the signed-in user's Windows identity
///   (Integrated Auth), exactly as before.</item>
///   <item>Standalone (workgroup) machine → explicit domain credentials, taken
///   from the Windows Credential Manager or, the first time, a sign-in prompt.</item>
/// </list>
/// The same explicit-credentials path is reused when integrated auth is rejected
/// (a 401), so a misjudged join state still recovers.
/// </summary>
public sealed class TrayAuthenticator
{
    private readonly Uri _server;
    private readonly string _credentialTarget;
    private readonly Func<CredentialPromptContext, NetworkCredential?> _prompt;

    /// <param name="server">The server base address.</param>
    /// <param name="prompt">
    /// Shows the sign-in dialog and returns the entered credentials (or null if
    /// cancelled). The caller is responsible for marshalling to the UI thread.
    /// </param>
    public TrayAuthenticator(Uri server, Func<CredentialPromptContext, NetworkCredential?> prompt)
    {
        _server = server;
        _prompt = prompt;
        _credentialTarget = $"OpenPrintDeploy:{server.Host}";
    }

    /// <summary>
    /// Builds the client for a sync attempt. Returns null only when the machine
    /// needs explicit credentials and the user dismissed the sign-in prompt with
    /// nothing saved — the caller should then report "sign-in required".
    /// </summary>
    public HttpClient? CreateClient()
    {
        if (DomainJoin.IsIntegratedAuthAvailable())
        {
            return SyncApiClient.CreateDefaultCredentialsClient(_server);
        }

        var credential = ReadStored() ?? PromptAndStore(reason: null);
        return credential is null
            ? null
            : SyncApiClient.CreateExplicitCredentialsClient(_server, credential);
    }

    /// <summary>
    /// Prompts for fresh credentials after the server rejected the current ones,
    /// saves them, and returns a new client — or null if the user cancelled.
    /// </summary>
    public HttpClient? ReAuthenticate(string reason)
    {
        var credential = PromptAndStore(reason);
        return credential is null
            ? null
            : SyncApiClient.CreateExplicitCredentialsClient(_server, credential);
    }

    /// <summary>The manual "Sign in…" tray action: always prompts.</summary>
    public HttpClient? SignInInteractive()
        => ReAuthenticate("Enter the domain account this PC should use.");

    private NetworkCredential? ReadStored()
    {
        var stored = WindowsCredentialStore.TryRead(_credentialTarget);
        return stored is null ? null : new NetworkCredential(stored.Value.User, stored.Value.Password);
    }

    private NetworkCredential? PromptAndStore(string? reason)
    {
        var existing = WindowsCredentialStore.TryRead(_credentialTarget);
        var context = new CredentialPromptContext(_server, existing?.User, reason);

        var credential = _prompt(context);
        if (credential is null)
        {
            return null;
        }

        WindowsCredentialStore.Save(_credentialTarget, credential.UserName, credential.Password);
        return credential;
    }
}
