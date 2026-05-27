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
                $"OpenPrintDeploy could not start: {ex.Message}",
                "OpenPrintDeploy", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _coordinator = new SyncCoordinator(settings);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Sync now", null, async (_, _) => await SyncAsync());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Visible = true,
            Text = "OpenPrintDeploy",
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
        if (outcome.Ok)
        {
            _notifyIcon.ShowBalloonTip(
                3000, "OpenPrintDeploy", $"{outcome.PrinterCount} printer(s) in sync.", Forms.ToolTipIcon.Info);
        }
        else
        {
            _notifyIcon.ShowBalloonTip(
                5000, "OpenPrintDeploy", $"Sync failed: {outcome.Error}", Forms.ToolTipIcon.Warning);
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

        _coordinator?.Dispose();
        base.OnExit(e);
    }
}
