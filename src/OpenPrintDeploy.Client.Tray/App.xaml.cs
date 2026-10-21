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
    private Forms.ToolStripMenuItem? _syncItem;
    private Forms.ToolStripMenuItem? _syncStatusItem;
    private bool _syncing;
    private bool _manualFeedbackRequested;
    private readonly CancellationTokenSource _shutdown = new();

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

        ClientLog.Info($"Starting {Branding.ProductName} v{GetVersion()}");

        TraySettings settings;
        try
        {
            settings = TraySettings.Load();
        }
        catch (Exception ex)
        {
            ClientLog.Error("Failed to load settings", ex);
            MessageBox.Show(
                $"{Branding.ProductName} could not start: {ex.Message}",
                Branding.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        ClientLog.Info($"Server: {settings.ServerBaseAddress}");

        // The prompt is only opened by the explicit tray-menu action and must run
        // on the UI (STA) thread, so marshal explicitly.
        _authenticator = new TrayAuthenticator(
            settings.ServerBaseAddress,
            ctx => Dispatcher.Invoke(() => CredentialPrompt.Show(ctx)),
            settings.ServerCertificateThumbprint);
        _coordinator = new SyncCoordinator(_authenticator, settings.AllowedPrintServers);

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
            if (_syncing) return;
            if (_isSignedIn)
            {
                ClientLog.Info("User signed out");
                _coordinator?.SignOut();
                _ = RefreshAuthMenuAsync();
            }
            else
            {
                ClientLog.Info("User signing in…");
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

        _syncItem = new Forms.ToolStripMenuItem("Sync now", null, async (_, _) => await SyncAsync(manual: true));
        menu.Items.Add(_syncItem);
        _syncStatusItem = new Forms.ToolStripMenuItem("Waiting for first sync") { Enabled = false };
        menu.Items.Add(_syncStatusItem);
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
    /// shows feedback, including errors. Clicking during background sync requests
    /// feedback from the current cycle without starting another one.
    /// </param>
    private async Task SyncAsync(bool manual = false)
    {
        if (_coordinator is null || _notifyIcon is null || _balloonIcon is null)
        {
            return;
        }

        // All entry points are on the dispatcher. A click during background work
        // subscribes to its result instead of starting a competing reconciliation.
        _manualFeedbackRequested |= manual;
        if (_syncing) return;
        _syncing = true;
        if (_syncItem is not null) _syncItem.Text = "Syncing… (show result)";
        if (_syncStatusItem is not null) _syncStatusItem.Text = "Sync in progress";
        if (_signInItem is not null) _signInItem.Enabled = false;
        SyncOutcome outcome;
        try
        {
            // Winspool, domain discovery and state I/O are synchronous even when
            // their wrappers return Task. Keep the entire cycle off the UI thread.
            outcome = await Task.Run(() => _coordinator.RunOnceAsync(_shutdown.Token));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            ClientLog.Error("Unexpected sync failure", ex);
            outcome = SyncOutcome.Failure(ex.Message);
        }
        finally
        {
            _syncing = false;
            if (!_shutdown.IsCancellationRequested)
            {
                if (_syncItem is not null) _syncItem.Text = "Sync now";
                if (_signInItem is not null) _signInItem.Enabled = true;
            }
        }
        if (_shutdown.IsCancellationRequested) return;
        manual = _manualFeedbackRequested;
        _manualFeedbackRequested = false;
        if (_syncStatusItem is not null)
            _syncStatusItem.Text = outcome.Ok && outcome.FailedNames.Count == 0
                ? $"Last sync: {DateTime.Now:t}"
                : $"Last sync needs attention ({DateTime.Now:t})";

        if (outcome.Ok)
        {
            var logMsg = $"Sync OK — {outcome.PrinterCount} printer(s)";
            if (outcome.AddedNames.Count > 0)
                logMsg += $", {outcome.AddedNames.Count} added ({string.Join(", ", outcome.AddedNames)})";
            if (outcome.FailedNames.Count > 0)
                logMsg += $", {outcome.FailedNames.Count} failed ({string.Join(", ", outcome.FailedNames)})";
            ClientLog.Info(logMsg);
        }
        else
        {
            ClientLog.Error($"Sync failed: {outcome.Error}");
        }

        // Background syncs are silent. Everything below is for "Sync now" only.
        if (!manual)
        {
            return;
        }

        if (outcome.Ok)
        {
            // Report the total assigned printer count, not how many were
            // (re)applied this cycle — "12 printers assigned" reads as the steady
            // state, whereas "12 added" wrongly implies they were new each time.
            var count = outcome.PrinterCount;
            var text = $"{count} printer{(count == 1 ? "" : "s")} assigned.";

            // Some printers couldn't be installed (e.g. a conflict with an
            // orphaned printer) — say so rather than claim a clean sync.
            if (outcome.FailedNames.Count > 0)
            {
                _notifyIcon.ShowBalloonTip(
                    6000, Branding.ProductName,
                    $"{text} {outcome.FailedNames.Count} need attention: "
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
        ClientLog.Info("Shutting down");
        _timer?.Stop();
        _shutdown.Cancel();
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        _trayIcon?.Dispose();
        _balloonIcon?.Dispose();

        // Process exit releases resources. Never dispose an HTTP client while
        // its worker is still inside a non-cancellable Windows spooler call.
        if (!_syncing) _coordinator?.Dispose();
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
