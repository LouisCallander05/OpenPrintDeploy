using System.Net;
using System.Net.Http;
using System.Security.Principal;
using OpenPrintDeploy.Client.Core;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>
/// How the tray is authenticating right now, for the menu to display.
/// </summary>
/// <param name="Integrated">True when using the signed-in Windows identity
/// (Kerberos / Integrated Auth) — i.e. a domain- or Entra-joined machine.</param>
/// <param name="User">The account label to show: the Windows user when
/// integrated, otherwise the stored domain account (null if none saved).</param>
public readonly record struct AuthStatus(bool Integrated, string? User)
{
    /// <summary>Only standalone machines surface a "Sign in" action.</summary>
    public bool CanSignIn => !Integrated;
}

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
    private readonly string? _pinnedThumbprint;
    private readonly string _credentialTarget;
    private readonly Func<CredentialPromptContext, NetworkCredential?> _prompt;

    /// <param name="server">The server base address.</param>
    /// <param name="prompt">
    /// Shows the sign-in dialog and returns the entered credentials (or null if
    /// cancelled). The caller is responsible for marshalling to the UI thread.
    /// </param>
    /// <param name="pinnedThumbprint">
    /// Server TLS certificate thumbprint to pin, or null for normal validation.
    /// </param>
    public TrayAuthenticator(
        Uri server,
        Func<CredentialPromptContext, NetworkCredential?> prompt,
        string? pinnedThumbprint = null)
    {
        _server = server;
        _prompt = prompt;
        _pinnedThumbprint = pinnedThumbprint;
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
            return SyncApiClient.CreateDefaultCredentialsClient(_server, _pinnedThumbprint);
        }

        var credential = ReadStored() ?? PromptAndStore(reason: null);
        return credential is null
            ? null
            : SyncApiClient.CreateExplicitCredentialsClient(_server, credential, _pinnedThumbprint);
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
            : SyncApiClient.CreateExplicitCredentialsClient(_server, credential, _pinnedThumbprint);
    }

    /// <summary>The manual "Sign in…" tray action: always prompts.</summary>
    public HttpClient? SignInInteractive()
        => ReAuthenticate("Enter the domain account this PC should use.");

    /// <summary>Removes the stored credentials so the next sync will prompt again.</summary>
    public void SignOut() => WindowsCredentialStore.Delete(_credentialTarget);

    /// <summary>
    /// Describes the current sign-in state for the tray menu: whether we use the
    /// signed-in Windows identity (Kerberos) and which account that is, or — on a
    /// standalone machine — the stored domain account, if any. The domain-join
    /// probe shells out to <c>dsregcmd</c>, so it's run off the UI thread.
    /// </summary>
    public async Task<AuthStatus> DescribeStatusAsync()
    {
        var integrated = await Task.Run(DomainJoin.IsIntegratedAuthAvailable).ConfigureAwait(false);
        if (integrated)
        {
            string? user = null;
            try
            {
                user = WindowsIdentity.GetCurrent().Name;
            }
            catch
            {
                // Best-effort label only; leave it null if the identity is unreadable.
            }

            return new AuthStatus(Integrated: true, User: user);
        }

        var stored = WindowsCredentialStore.TryRead(_credentialTarget);
        return new AuthStatus(Integrated: false, User: stored?.User);
    }

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
