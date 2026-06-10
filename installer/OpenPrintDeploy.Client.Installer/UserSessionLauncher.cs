using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OpenPrintDeploy.Client.Installer;

/// <summary>
/// Starts the tray in the interactive user's session right after install, so it
/// appears immediately instead of waiting for the next logon (the Run key still
/// covers that). The hard part is identity: this installer is always elevated,
/// and the tray must run as the *signed-in user* — its Kerberos zone resolution
/// depends on that. So we branch on how we're elevated:
///
///  - As SYSTEM (Intune): the installer's own token is the wrong identity and
///    sits in session 0. We grab the active console session's user token and
///    <c>CreateProcessAsUser</c> into their session. If nobody is logged in
///    (device-setup / ESP), we skip — there's no session to launch into.
///  - As an elevated admin (UAC double-click): the interactive user *is* us, in
///    this session, so a plain <see cref="Process.Start(string)"/> is correct.
///    The tray inherits our (filtered, medium-integrity) admin token.
///
/// Best-effort throughout: a failure here never fails the install — worst case
/// the user waits until next logon.
/// </summary>
internal static class UserSessionLauncher
{
    /// <summary>
    /// Launches <paramref name="trayExe"/> for the current interactive user.
    /// Returns true if a process was started; false if we deliberately skipped
    /// (e.g. SYSTEM with no logged-in user). Never throws.
    /// </summary>
    public static bool TryLaunch(string trayExe)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.IsSystem)
            {
                return TryLaunchInActiveSession(trayExe);
            }

            // Elevated admin double-click: we're already in the user's session.
            using var process = Process.Start(new ProcessStartInfo(trayExe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(trayExe)!,
            });
            return process is not null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Note: could not auto-start the tray now ({ex.Message}); " +
                              "it will start at next logon.");
            return false;
        }
    }

    private static bool TryLaunchInActiveSession(string trayExe)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        // 0xFFFFFFFF means "no session currently attached to the console" — e.g.
        // an Intune install running before anyone has signed in.
        if (sessionId == 0xFFFFFFFF)
        {
            Console.WriteLine("  No interactive session yet; the tray will start at next logon.");
            return false;
        }

        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            // No user token for the console session (locked-out / transitional
            // state). SeTcbPrivilege is required and SYSTEM has it, so a failure
            // here means there's genuinely no user to launch as.
            Console.WriteLine("  No signed-in user on the console session; the tray will start at next logon.");
            return false;
        }

        var envBlock = IntPtr.Zero;
        try
        {
            // Without the user's environment block the tray's %LOCALAPPDATA%
            // (where per-user state lives) would resolve to SYSTEM's profile.
            if (!CreateEnvironmentBlock(out envBlock, userToken, inherit: false))
            {
                envBlock = IntPtr.Zero;
            }

            var startupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = @"winsta0\default",
            };

            const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
            const uint CREATE_NO_WINDOW = 0x08000000;
            var flags = CREATE_NO_WINDOW | (envBlock != IntPtr.Zero ? CREATE_UNICODE_ENVIRONMENT : 0);

            var quotedExe = $"\"{trayExe}\"";
            var ok = CreateProcessAsUser(
                userToken,
                lpApplicationName: null,
                lpCommandLine: quotedExe,
                lpProcessAttributes: IntPtr.Zero,
                lpThreadAttributes: IntPtr.Zero,
                bInheritHandles: false,
                dwCreationFlags: flags,
                lpEnvironment: envBlock,
                lpCurrentDirectory: Path.GetDirectoryName(trayExe),
                ref startupInfo,
                out var processInfo);

            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed");
            }

            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
            Console.WriteLine($"  Started the tray in session {sessionId}.");
            return true;
        }
        finally
        {
            if (envBlock != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(envBlock);
            }

            CloseHandle(userToken);
        }
    }

    // ----- P/Invoke -----
    // All targets live in System32; pin the search path to block DLL hijacking.

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("userenv.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}
