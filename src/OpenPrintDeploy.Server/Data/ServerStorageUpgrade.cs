using System.Runtime.Versioning;
using System.Security.Principal;
using OpenPrintDeploy.Shared.Drivers;

namespace OpenPrintDeploy.Server.Data;

[SupportedOSPlatform("windows")]
public static class ServerStorageUpgrade
{
    // No caller-supplied paths or configuration: this command is an elevated
    // MSI action for the historical default server data directory only.
    public static void Run()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.IsSystem)
            throw new UnauthorizedAccessException("Server storage migration requires the Local SYSTEM installer context.");

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenPrintDeploy");
        PrivateDirectory.CreateForServerUpgrade(path);
        // Users could have created files under the old directory ACL. Securing
        // the parent does NOT make those files trustworthy. Check their owners,
        // ACLs and link status before the server is allowed to use them.
        foreach (var name in new[] { "app.db", "app.db-wal", "app.db-shm", "app.db-journal", "admin-access.json" })
        {
            try { PrivateDirectory.ValidateExistingFile(Path.Combine(path, name)); }
            catch (IOException ex) { throw new IOException($"Server storage migration refused {name}: {ex.Message}", ex); }
        }
        // Deliberately retain tightened permissions if a later MSI step rolls
        // back. Older servers run as SYSTEM and remain able to use this data;
        // rollback must not restore the broad Users write grant.
        Console.WriteLine("Server data permissions secured; existing data files preserved.");
    }
}
