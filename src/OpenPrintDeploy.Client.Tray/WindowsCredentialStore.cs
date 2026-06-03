using System.ComponentModel;
using System.Runtime.InteropServices;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>
/// Persists the domain credentials a non-domain-joined machine uses to reach the
/// server, in the Windows Credential Manager (the OS credential vault). Stored as
/// a generic credential under a per-server target name; the password is protected
/// by Windows for the current user, so it survives restarts without us writing a
/// secret to our own files.
/// </summary>
internal static class WindowsCredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    public static (string User, string Password)? TryRead(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var handle))
        {
            return null;
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);
            var user = cred.UserName ?? string.Empty;
            var password = cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0
                ? Marshal.PtrToStringUni(cred.CredentialBlob, (int)(cred.CredentialBlobSize / 2)) ?? string.Empty
                : string.Empty;
            return (user, password);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public static void Save(string target, string userName, string password)
    {
        var blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var cred = new CREDENTIAL
            {
                Flags = 0,
                Type = CredTypeGeneric,
                TargetName = target,
                Comment = null,
                LastWritten = default,
                CredentialBlobSize = (uint)(password.Length * 2),
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = null,
                UserName = userName,
            };

            if (!CredWrite(ref cred, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save credentials to Windows Credential Manager.");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public static void Delete(string target)
    {
        // Best-effort: a missing entry just returns false, which we ignore.
        _ = CredDelete(target, CredTypeGeneric, 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredReadW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredWriteW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void CredFree(IntPtr buffer);
}
