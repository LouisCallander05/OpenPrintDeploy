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

    /// <summary>SYSTEM-context uninstall flag: relaunch as the console user to clean their printers.</summary>
    private const string CleanupActiveSessionArg = "--cleanup-active-session";

    /// <summary>User-context flag: remove the printers OPD created for this user, then exit.</summary>
    private const string CleanupArg = "--cleanup";

    private static bool HasArg(StartupEventArgs e, string arg)
        => e.Args.Any(a => string.Equals(a, arg, StringComparison.OrdinalIgnoreCase));

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Launch-now path: spawn the tray in the interactive session and exit
        // immediately — never build the UI in this (possibly session-0) process.
        if (HasArg(e, LaunchActiveSessionArg))
        {
            TraySessionLauncher.LaunchInteractive();
            Shutdown(0);
            return;
        }

        // Uninstall cleanup. The MSI runs this as SYSTEM; relaunch as the console
        // user so we can remove THEIR per-user connections. If we're already a
        // user (manual run), clean inline.
        if (HasArg(e, CleanupActiveSessionArg))
        {
            if (OperatingSystem.IsWindows() && System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem)
            {
                TraySessionLauncher.LaunchInteractive(CleanupArg);
            }
            else
            {
                await CleanupManagedPrintersAsync();
            }

            Shutdown(0);
            return;
        }

        if (HasArg(e, CleanupArg))
        {
            await CleanupManagedPrintersAsync();
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

        // The prompt must run on the UI (STA) thread; the authenticator may call
        // it from a background continuation during a sync, so marshal explicitly.
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

    /// <summary>
    /// Removes the printer connections OPD created for the current user (used at
    /// uninstall so deployed printers don't orphan). Adopted printers — ones that
    /// existed before OPD claimed them — are deliberately left in place. Reuses
    /// the applier's best-effort removal path; failures are logged, never thrown.
    /// </summary>
    private static async Task CleanupManagedPrintersAsync()
    {
        try
        {
            var store = new ManagedStateStore();
            var created = store.Load()
                .Where(m => m.Origin == OpenPrintDeploy.Client.Core.PrinterOrigin.Created)
                .Select(m => m.Unc)
                .ToList();

            if (created.Count > 0)
            {
                await new WindowsPrinterApplier().ApplyAsync(
                    new OpenPrintDeploy.Client.Core.ReconcileResult([], created, []));
            }

            store.Save([]);
            ClientLog.Info($"Uninstall cleanup removed {created.Count} OPD-managed printer(s).");
        }
        catch (Exception ex)
        {
            ClientLog.Error($"Uninstall cleanup failed: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ClientLog.Info("Shutting down");
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
