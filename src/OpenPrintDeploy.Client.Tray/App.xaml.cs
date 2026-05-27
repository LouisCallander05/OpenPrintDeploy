using System.Windows;

namespace OpenPrintDeploy.Client.Tray;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Tray icon, named-pipe listener, and printer apply loop wire up here.
    }
}
