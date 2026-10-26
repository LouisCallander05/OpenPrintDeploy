using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenPrintDeploy.Shared.Drivers;

[SupportedOSPlatform("windows")]
public static class PrivateDirectory
{
    public static string Create(string path, bool allowUsersRead = false)
        => CreateCore(path, allowUsersRead, migrateLegacyServerDirectory: false);

    // Installer-only migration entry point. Callers must validate every sensitive
    // existing file after securing the directory, before reading any of its data.
    public static string CreateForServerUpgrade(string path)
        => CreateCore(path, allowUsersRead: false, migrateLegacyServerDirectory: true);

    private static string CreateCore(string path, bool allowUsersRead, bool migrateLegacyServerDirectory)
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
        if (allowUsersRead)
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
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
        if (migrateLegacyServerDirectory) ValidateLegacyServerDirectoryAccess(directory.GetAccessControl());
        else ValidateAccess(directory.GetAccessControl());
        directory.SetAccessControl(security);
        return directory.FullName;
    }

    public static void ValidateExistingFile(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Protected storage cannot use a link or directory as a file.");
            ValidateAccess(new FileInfo(path).GetAccessControl());
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    public static void ValidateAccess(FileSystemSecurity security)
        => ValidateAccessCore(security, allowLegacyDirectoryWrite: false);

    public static void ValidateLegacyServerDirectoryAccess(DirectorySecurity security)
        => ValidateAccessCore(security, allowLegacyDirectoryWrite: true);

    private static void ValidateAccessCore(FileSystemSecurity security, bool allowLegacyDirectoryWrite)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var owner = security.GetOwner(typeof(SecurityIdentifier));
        if (!Equals(owner, system) && !Equals(owner, admins))
            throw new IOException("Protected storage has an untrusted owner.");
        const FileSystemRights mutations = FileSystemRights.Write | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & mutations) == 0) continue;
            if (Equals(rule.IdentityReference, system) || Equals(rule.IdentityReference, admins)) continue;
            // Older servers inherited ProgramData's container-only Users Write
            // rule. It permits file creation, but not deletion/replacement of
            // the administrator-owned directory. Only the installer may adopt
            // this precise legacy ACL, immediately replacing it with a private
            // ACL. Windows can materialize inherited rules as explicit copies;
            // accept that equivalent rule too. Modify/FullControl, object-
            // inheritable write grants and other writers still fail.
            if (allowLegacyDirectoryWrite
                && rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null))
                && rule.InheritanceFlags == InheritanceFlags.ContainerInherit
                && rule.PropagationFlags == PropagationFlags.None
                && (rule.FileSystemRights & ~(FileSystemRights.Synchronize | FileSystemRights.ReadPermissions)) == FileSystemRights.Write) continue;
            // Creator Owner applies only to new descendants; each sensitive file
            // is checked separately before opening it.
            if (rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null))
                && rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            throw new IOException($"Protected storage permits writes by an untrusted identity: {rule.IdentityReference} ({rule.FileSystemRights}).");
        }
    }
}
