using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>
/// Launches a fresh tray instance in the interactive user's session. The MSI's
/// post-install custom action runs the tray exe with <c>--launch-active-session</c>
/// (as SYSTEM); App.OnStartup routes that here and exits, so the tray itself
/// becomes the "session launcher" — no separate binary needed.
///
/// Branches like the EXE installer did: as SYSTEM (Intune / the MSI's deferred
/// action) it grabs the active console session's user token and
/// <c>CreateProcessAsUser</c> into their session; if there's no user logged in,
/// it just skips (the Run key starts the tray at next logon). As a non-SYSTEM
/// elevated process it's already in the user's session, so a plain start works.
/// Best-effort throughout — failure here never matters; the Run key is the
/// fallback.
/// </summary>
internal static class TraySessionLauncher
{
    public static bool LaunchInteractive(string? extraArgs = null)
    {
        var trayExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(trayExe))
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.IsSystem)
            {
                return TryLaunchInActiveSession(trayExe, extraArgs);
            }

            using var process = Process.Start(new ProcessStartInfo(trayExe)
            {
                Arguments = extraArgs ?? string.Empty,
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(trayExe)!,
            });
            return process is not null;
        }
        catch
        {
            // The Run key launches it at next logon regardless.
            return false;
        }
    }

    private static bool TryLaunchInActiveSession(string trayExe, string? extraArgs)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            return false; // no one attached to the console (e.g. an ESP install)
        }

        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            return false; // no signed-in user on the console session
        }

        var envBlock = IntPtr.Zero;
        try
        {
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

            var commandLine = string.IsNullOrWhiteSpace(extraArgs)
                ? $"\"{trayExe}\""
                : $"\"{trayExe}\" {extraArgs}";
            var ok = CreateProcessAsUser(
                userToken,
                lpApplicationName: null,
                lpCommandLine: commandLine,
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
                return false;
            }

            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
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

    // ----- P/Invoke (all targets live in System32; pin to block DLL hijacking) -----

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
