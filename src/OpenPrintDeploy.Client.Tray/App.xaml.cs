using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace OpenPrintDeploy.Client.Tray;

public partial class App : Application
{
    private Forms.NotifyIcon? _notifyIcon;
    private Drawing.Icon? _trayIcon;
    private Drawing.Icon? _balloonIcon;
    private SyncCoordinator? _coordinator;
    private TrayAuthenticator? _authenticator;
    private Forms.ToolStripMenuItem? _identityItem;
    private Forms.ToolStripMenuItem? _signInItem;
    private DispatcherTimer? _timer;
    private bool _isSignedIn;

    /// <summary>
    /// CLI flag the MSI's post-install action passes (as SYSTEM) to bring the
    /// tray up in the signed-in user's session right after install.
    /// </summary>
    private const string LaunchActiveSessionArg = "--launch-active-session";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Launch-now path: spawn the tray in the interactive session and exit
        // immediately — never build the UI in this (possibly session-0) process.
        if (e.Args.Any(a => string.Equals(a, LaunchActiveSessionArg, StringComparison.OrdinalIgnoreCase)))
        {
            TraySessionLauncher.LaunchInteractive();
            Shutdown(0);
            return;
        }

        TraySettings settings;
        try
        {
            settings = TraySettings.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"{Branding.ProductName} could not start: {ex.Message}",
                Branding.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // The prompt must run on the UI (STA) thread; the authenticator may call
        // it from a background continuation during a sync, so marshal explicitly.
        _authenticator = new TrayAuthenticator(
            settings.ServerBaseAddress,
            ctx => Dispatcher.Invoke(() => CredentialPrompt.Show(ctx)),
            settings.ServerCertificateThumbprint);
        _coordinator = new SyncCoordinator(_authenticator);

        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };

        // A non-clickable branded header at the top of the menu.
        var header = new Forms.ToolStripMenuItem(Branding.ProductName)
        {
            Enabled = false,
            Font = new Drawing.Font(menu.Font, Drawing.FontStyle.Bold),
        };
        header.ForeColor = Branding.Navy;
        menu.Items.Add(header);
        menu.Items.Add(new Forms.ToolStripSeparator());

        // Sign-in state. On a domain/Entra-joined PC this shows "Signed in as
        // <user>" (Kerberos) and no action; on a standalone PC the "Sign in…"
        // action appears. Resolved asynchronously by RefreshAuthMenuAsync below.
        _identityItem = new Forms.ToolStripMenuItem("Checking sign-in…") { Enabled = false };
        menu.Items.Add(_identityItem);
        _signInItem = new Forms.ToolStripMenuItem("Sign in…", null, async (_, _) =>
        {
            if (_isSignedIn)
            {
                _coordinator?.SignOut();
                _ = RefreshAuthMenuAsync();
            }
            else
            {
                _coordinator?.SignIn();
                _ = RefreshAuthMenuAsync();
                await SyncAsync(manual: true);
            }
        })
        {
            Visible = false,
        };
        menu.Items.Add(_signInItem);
        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add("Sync now", null, async (_, _) => await SyncAsync(manual: true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add($"Server: {settings.ServerBaseAddress}") .Enabled = false;
        menu.Items.Add($"Version: {GetVersion()}").Enabled = false;
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        _trayIcon = Branding.LoadIcon(Forms.SystemInformation.SmallIconSize);
        // A larger frame for the notification (NIIF_LARGE_ICON), which renders
        // bigger than the tray glyph.
        _balloonIcon = Branding.LoadIcon(new Drawing.Size(32, 32));
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            Text = Branding.ProductName,
            ContextMenuStrip = menu,
        };

        _ = RefreshAuthMenuAsync();

        // Sync at logon (startup), then on the configured interval.
        _timer = new DispatcherTimer { Interval = settings.SyncInterval };
        _timer.Tick += async (_, _) => await SyncAsync();
        _timer.Start();

        await SyncAsync();
    }

    /// <summary>
    /// Updates the identity line and the visibility of the "Sign in…" item to
    /// match the current auth mode. Safe to call repeatedly (e.g. after a manual
    /// sign-in changes the stored account).
    /// </summary>
    private async Task RefreshAuthMenuAsync()
    {
        if (_authenticator is null || _identityItem is null || _signInItem is null)
        {
            return;
        }

        // Capture non-null locals so the closure below doesn't trip nullable flow.
        Forms.ToolStripMenuItem identityItem = _identityItem;
        Forms.ToolStripMenuItem signInItem = _signInItem;

        var status = await _authenticator.DescribeStatusAsync();
        Dispatcher.Invoke(() =>
        {
            identityItem.Text = status.User is { Length: > 0 } user
                ? $"Signed in as {user}"
                : status.Integrated ? "Signed in" : "Not signed in";
            _isSignedIn = status.CanSignIn && status.User is { Length: > 0 };
            signInItem.Text = _isSignedIn ? "Sign out" : "Sign in…";
            signInItem.Visible = status.CanSignIn;
        });
    }

    /// <param name="manual">
    /// True only for an explicit "Sync now" / "Sign in" action. Background syncs
    /// (logon + the timer) pass false and stay completely silent — they never
    /// raise a notification, so the tray doesn't interrupt the user (and a fixed
    /// set of printers doesn't toast on every cycle). Only an explicit "Sync now"
    /// shows feedback, and only on success — failures stay quiet so an offline
    /// device doesn't nag.
    /// </param>
    private async Task SyncAsync(bool manual = false)
    {
        if (_coordinator is null || _notifyIcon is null || _balloonIcon is null)
        {
            return;
        }

        var outcome = await _coordinator.RunOnceAsync();

        // Background syncs are silent. Everything below is for "Sync now" only.
        if (!manual)
        {
            return;
        }

        if (outcome.Ok)
        {
            var text = outcome.AddedNames.Count switch
            {
                0 => $"{outcome.PrinterCount} printer(s) in sync.",
                1 => $"Printer added: {outcome.AddedNames[0]}",
                _ => $"{outcome.AddedNames.Count} printers added.",
            };

            // Some printers installed, others didn't (e.g. a conflict with an
            // orphaned printer) — say so rather than claim a clean sync.
            if (outcome.FailedNames.Count > 0)
            {
                _notifyIcon.ShowBalloonTip(
                    6000, Branding.ProductName,
                    $"{text} {outcome.FailedNames.Count} couldn’t be installed: "
                        + string.Join(", ", outcome.FailedNames),
                    Forms.ToolTipIcon.Warning);
            }
            else
            {
                TrayBalloon.Show(_notifyIcon, Branding.ProductName, text, _balloonIcon, 3000);
            }
        }
        else
        {
            _notifyIcon.ShowBalloonTip(
                6000, Branding.ProductName,
                outcome.Error ?? "Sync failed.", Forms.ToolTipIcon.Error);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        _trayIcon?.Dispose();
        _balloonIcon?.Dispose();

        _coordinator?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Stamped at build time via -p:Version=&lt;tag&gt; in CI.</summary>
    private static string GetVersion()
    {
        var raw = typeof(App).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
            ?? typeof(App).Assembly.GetName().Version?.ToString()
            ?? "(unknown)";

        // Drop any build metadata the SDK appends (e.g. "0.5.0+<git-sha>").
        var plus = raw.IndexOf('+');
        return plus >= 0 ? raw[..plus] : raw;
    }
}
