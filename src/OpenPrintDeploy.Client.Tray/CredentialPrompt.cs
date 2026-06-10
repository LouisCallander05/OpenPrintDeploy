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
    // Layout constants. The form is laid out below a branded header banner.
    private const int BannerHeight = 64;
    private const int FormWidth    = 430;

    public static NetworkCredential? Show(CredentialPromptContext ctx)
    {
        using var icon = Branding.LoadIcon();
        using var logo = Branding.LoadLogo();

        using var form = new Form
        {
            Text = $"{Branding.ProductName} — Sign in",
            Icon = icon,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(FormWidth, BannerHeight + 218),
            BackColor = Color.White,
            TopMost = true,
        };

        form.Controls.Add(BuildBanner(logo));

        // Everything below the banner is offset by BannerHeight + a little gap.
        var top = BannerHeight + 12;

        var intro = new Label
        {
            Left = 12,
            Top = top,
            Width = FormWidth - 24,
            Height = 44,
            Text = $"This PC isn't joined to your domain. Sign in with your domain account to receive printers from {ctx.Server.Host}.",
        };

        var reason = new Label
        {
            Left = 12,
            Top = top + 46,
            Width = FormWidth - 24,
            Height = 18,
            ForeColor = Color.Firebrick,
            Text = ctx.Reason ?? string.Empty,
            Visible = !string.IsNullOrWhiteSpace(ctx.Reason),
        };

        var userLabel = new Label { Left = 12, Top = top + 74, Width = 84, Height = 22, Text = "Username:" };
        var userBox = new TextBox { Left = 100, Top = top + 71, Width = 318, Text = ctx.SuggestedUser ?? string.Empty };

        var passLabel = new Label { Left = 12, Top = top + 104, Width = 84, Height = 22, Text = "Password:" };
        var passBox = new TextBox { Left = 100, Top = top + 101, Width = 318, UseSystemPasswordChar = true };

        var hint = new Label
        {
            Left = 100,
            Top = top + 126,
            Width = 318,
            Height = 16,
            ForeColor = Color.Gray,
            Text = "e.g. CONTOSO\\jsmith or jsmith@contoso.edu.au",
        };

        var ok = new Button
        {
            Text = "Sign in",
            Left = 238,
            Top = top + 156,
            Width = 88,
            Height = 28,
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            BackColor = Branding.Navy,
            ForeColor = Color.White,
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.FlatAppearance.MouseOverBackColor = Branding.Teal;

        var cancel = new Button
        {
            Text = "Cancel",
            Left = 330,
            Top = top + 156,
            Width = 88,
            Height = 28,
            DialogResult = DialogResult.Cancel,
        };

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

    /// <summary>
    /// The white header strip: the logo on the left, the product name beside it,
    /// and a teal accent line along the bottom edge — matching the icon.
    /// </summary>
    private static Panel BuildBanner(Image logo)
    {
        var banner = new Panel
        {
            Left = 0,
            Top = 0,
            Width = FormWidth,
            Height = BannerHeight,
            BackColor = Color.White,
        };

        var logoBox = new PictureBox
        {
            Left = 14,
            Top = (BannerHeight - 44) / 2,
            Width = 44,
            Height = 44,
            Image = logo,
            SizeMode = PictureBoxSizeMode.Zoom,
        };

        var title = new Label
        {
            Left = 70,
            Top = (BannerHeight - 26) / 2,
            Width = FormWidth - 84,
            Height = 26,
            Text = Branding.ProductName,
            ForeColor = Branding.Navy,
            Font = new Font("Segoe UI", 12F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var accent = new Panel
        {
            Left = 0,
            Top = BannerHeight - 3,
            Width = FormWidth,
            Height = 3,
            BackColor = Branding.Teal,
        };

        banner.Controls.AddRange([logoBox, title, accent]);
        return banner;
    }
}
