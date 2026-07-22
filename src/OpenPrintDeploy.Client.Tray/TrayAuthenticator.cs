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
///   <item>Standalone (workgroup) machine → explicit domain credentials from
///   Windows Credential Manager, populated by the manual "Sign in…" action.</item>
/// </list>
/// Interactive credential collection only happens from the explicit tray-menu
/// action. Background sync must never interrupt the user with a modal dialog.
/// </summary>
public sealed class TrayAuthenticator
{
    private readonly Uri _server;
    private readonly string? _pinnedThumbprint;
    private readonly string _credentialTarget;
    private readonly Func<CredentialPromptContext, NetworkCredential?> _prompt;

    /// <summary>Whether the most recently created client uses Windows integrated authentication.</summary>
    public bool CurrentClientUsesIntegratedAuth { get; private set; }

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
    /// Builds the client for a sync attempt. Returns null when a standalone
    /// machine has no saved credentials; the user can then choose "Sign in…"
    /// from the tray menu. This method never opens UI.
    /// </summary>
    public HttpClient? CreateClient()
    {
        if (DomainJoin.IsIntegratedAuthAvailable())
        {
            return CreateIntegratedClient();
        }

        CurrentClientUsesIntegratedAuth = false;
        var credential = ReadStored();
        return credential is null
            ? null
            : SyncApiClient.CreateExplicitCredentialsClient(_server, credential, _pinnedThumbprint);
    }

    /// <summary>
    /// Rebuilds an integrated-auth HTTP session. A fresh handler lets Windows
    /// renegotiate after a transient Entra/Kerberos/NTLM token failure.
    /// </summary>
    public HttpClient CreateIntegratedClient()
    {
        CurrentClientUsesIntegratedAuth = true;
        return SyncApiClient.CreateDefaultCredentialsClient(_server, _pinnedThumbprint);
    }

    /// <summary>The manual "Sign in…" tray action: always prompts.</summary>
    public HttpClient? SignInInteractive()
    {
        var credential = PromptAndStore("Enter the domain account this PC should use.");
        if (credential is null)
        {
            return null;
        }

        CurrentClientUsesIntegratedAuth = false;
        return SyncApiClient.CreateExplicitCredentialsClient(_server, credential, _pinnedThumbprint);
    }

    /// <summary>Removes stored credentials; a later manual "Sign in…" can replace them.</summary>
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
