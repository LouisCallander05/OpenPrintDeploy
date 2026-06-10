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
    private SyncCoordinator? _coordinator;
    private DispatcherTimer? _timer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        var authenticator = new TrayAuthenticator(
            settings.ServerBaseAddress,
            ctx => Dispatcher.Invoke(() => CredentialPrompt.Show(ctx)));
        _coordinator = new SyncCoordinator(authenticator);

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

        menu.Items.Add("Sync now", null, async (_, _) => await SyncAsync());
        menu.Items.Add("Sign in…", null, async (_, _) =>
        {
            _coordinator?.SignIn();
            await SyncAsync();
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add($"Server: {settings.ServerBaseAddress}") .Enabled = false;
        menu.Items.Add($"Version: {GetVersion()}").Enabled = false;
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        _trayIcon = Branding.LoadIcon(Forms.SystemInformation.SmallIconSize);
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Visible = true,
            Text = Branding.ProductName,
            ContextMenuStrip = menu,
        };

        // Sync at logon (startup), then on the configured interval.
        _timer = new DispatcherTimer { Interval = settings.SyncInterval };
        _timer.Tick += async (_, _) => await SyncAsync();
        _timer.Start();

        await SyncAsync();
    }

    private async Task SyncAsync()
    {
        if (_coordinator is null || _notifyIcon is null)
        {
            return;
        }

        var outcome = await _coordinator.RunOnceAsync();
        // ToolTipIcon.None suppresses the generic system info/warning glyph so
        // Windows shows our own tray icon (the logo) in the notification instead.
        // Success/failure is conveyed in the wording rather than a system badge.
        if (outcome.Ok)
        {
            _notifyIcon.ShowBalloonTip(
                3000, Branding.ProductName, $"{outcome.PrinterCount} printer(s) in sync.", Forms.ToolTipIcon.None);
        }
        else
        {
            _notifyIcon.ShowBalloonTip(
                5000, Branding.ProductName, $"Sync failed — {outcome.Error}", Forms.ToolTipIcon.None);
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

        _coordinator?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Stamped at build time via -p:Version=&lt;tag&gt; in CI.</summary>
    private static string GetVersion()
        => typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? typeof(App).Assembly.GetName().Version?.ToString()
        ?? "(unknown)";
}
