using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using OpenPrintDeploy.Shared.Drivers;

namespace OpenPrintDeploy.Client.Installer;

internal static class DriverServiceRegistration
{
    public static void Stop()
    {
        using var service = new ServiceController(DriverProtocol.ServiceName);
        try
        {
            if (service.Status == ServiceControllerStatus.Stopped) return;
            if (service.Status != ServiceControllerStatus.StopPending) service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 1060 }) { }
    }

    public static void Install(string directory)
    {
        var executable = Path.Combine(directory, "OpenPrintDeploy.Client.Service.exe");
        if (!File.Exists(executable)) throw new IOException("Driver service is missing from the installer payload.");
        using var service = new ServiceController(DriverProtocol.ServiceName);
        var exists = true;
        try { _ = service.Status; }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 1060 }) { exists = false; }
        Run(exists ? "config" : "create", DriverProtocol.ServiceName, "binPath=", $"\"{executable}\"",
            "start=", "auto", "obj=", "LocalSystem", "depend=", "Spooler", "DisplayName=", "Open Print Deploy Drivers");
        Run("failure", DriverProtocol.ServiceName, "reset=", "86400", "actions=", "restart/60000/restart/60000/none/0");
        service.Refresh();
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    public static void Remove()
    {
        Stop();
        Run("delete", DriverProtocol.ServiceName);
    }

    private static void Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not configure the driver service.");
        if (!process.WaitForExit(30000)) { process.Kill(); throw new IOException("Service configuration timed out."); }
        if (process.ExitCode != 0 && !(arguments[0] == "delete" && process.ExitCode == 1060))
            throw new IOException($"Service configuration failed ({process.ExitCode}).");
    }
}
