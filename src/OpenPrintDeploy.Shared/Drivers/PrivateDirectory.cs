using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenPrintDeploy.Shared.Drivers;

[SupportedOSPlatform("windows")]
public static class PrivateDirectory
{
    public static string Create(string path)
    {
        // Reject reparse points on every existing ancestor before ACL or file work.
        for (var parent = new DirectoryInfo(Path.GetFullPath(path)); parent is not null; parent = parent.Parent)
        {
            try
            {
                // File.GetAttributes also examines a dangling link. Directory.Exists
                // alone can hide it and must not be used as the safety check.
                if ((File.GetAttributes(parent.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Driver storage cannot use a reparse point.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) directory.Create(security);
        directory.Refresh();
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Driver storage cannot use a reparse point.");
        // Check after create as well: an unprivileged process may have won the
        // race to create this name under ProgramData. Never adopt its contents.
        var owner = directory.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (!Equals(owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null))
            && !Equals(owner, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)))
            throw new IOException("Driver directory must be owned by SYSTEM or Administrators.");
        directory.SetAccessControl(security);
        return path;
    }
}
