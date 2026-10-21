using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using OpenPrintDeploy.Shared.Drivers;

namespace OpenPrintDeploy.Client.Service;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64 || !WindowsIdentity.GetCurrent().IsSystem)
        {
            Console.Error.WriteLine("The driver service requires Windows x64 and the Local SYSTEM account.");
            return 1;
        }
        if (args.Length == 2 && args[0] == "--install-package")
        {
            try
            {
                var root = PrivateDirectory.Create(DriverInstaller.StorageRoot);
                var job = Path.GetFullPath(args[1]);
                if (!job.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(job) != "install.json") throw new InvalidDataException("Invalid installation job.");
                var manifest = JsonSerializer.Deserialize<DriverManifest>(File.ReadAllText(job))!;
                var inf = DriverPackageFiles.Resolve(Path.Combine(Path.GetDirectoryName(job)!, "files"), manifest.InfPath);
                WindowsDriverStore.Install(inf, manifest.DriverName);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return ex.HResult == 0 ? 1 : ex.HResult; }
        }
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Services.AddWindowsService(o => o.ServiceName = DriverProtocol.ServiceName);
        builder.Logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.Information);
        builder.Services.AddSingleton<DriverInstaller>();
        builder.Services.AddHostedService<DriverWorker>();
        await builder.Build().RunAsync();
        return 0;
    }
}
