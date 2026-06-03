using System.Drawing;
using System.Net;
using System.Windows.Forms;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>Inputs for the sign-in dialog: which server, and why we're asking.</summary>
public sealed record CredentialPromptContext(Uri Server, string? SuggestedUser, string? Reason);

/// <summary>
/// A small modal dialog that collects a domain username and password for the
/// non-domain-joined sign-in path. Returns <c>null</c> if the user cancels or
/// leaves a field blank. Must be shown on the UI (STA) thread.
/// </summary>
internal static class CredentialPrompt
{
    public static NetworkCredential? Show(CredentialPromptContext ctx)
    {
        using var form = new Form
        {
            Text = "OpenPrintDeploy — Sign in",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(430, 210),
            TopMost = true,
        };

        var intro = new Label
        {
            Left = 12,
            Top = 12,
            Width = 406,
            Height = 44,
            Text = $"This PC isn't joined to your domain. Sign in with your domain account to receive printers from {ctx.Server.Host}.",
        };

        var reason = new Label
        {
            Left = 12,
            Top = 58,
            Width = 406,
            Height = 18,
            ForeColor = Color.Firebrick,
            Text = ctx.Reason ?? string.Empty,
            Visible = !string.IsNullOrWhiteSpace(ctx.Reason),
        };

        var userLabel = new Label { Left = 12, Top = 86, Width = 84, Height = 22, Text = "Username:" };
        var userBox = new TextBox { Left = 100, Top = 83, Width = 318, Text = ctx.SuggestedUser ?? string.Empty };

        var passLabel = new Label { Left = 12, Top = 116, Width = 84, Height = 22, Text = "Password:" };
        var passBox = new TextBox { Left = 100, Top = 113, Width = 318, UseSystemPasswordChar = true };

        var hint = new Label
        {
            Left = 100,
            Top = 138,
            Width = 318,
            Height = 16,
            ForeColor = Color.Gray,
            Text = "e.g. CONTOSO\\jsmith or jsmith@contoso.edu.au",
        };

        var ok = new Button { Text = "Sign in", Left = 238, Top = 168, Width = 88, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 330, Top = 168, Width = 88, DialogResult = DialogResult.Cancel };

        form.Controls.AddRange([intro, reason, userLabel, userBox, passLabel, passBox, hint, ok, cancel]);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        if (form.ShowDialog() != DialogResult.OK)
        {
            return null;
        }

        var user = userBox.Text.Trim();
        var password = passBox.Text;
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        return new NetworkCredential(user, password);
    }
}
