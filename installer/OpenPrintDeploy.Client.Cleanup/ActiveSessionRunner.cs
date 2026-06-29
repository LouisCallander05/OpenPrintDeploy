using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OpenPrintDeploy.Client.Cleanup;

/// <summary>
/// Runs <c>--remove-user</c> in the interactive user's session right now, so the
/// person sitting at the machine when it's uninstalled doesn't have to sign out
/// and back in for their printers to go. The logon task still covers everyone
/// else (and this same user on later logons — the manifest prune makes that a
/// no-op).
///
/// <para>Identity, same branch as the installer's UserSessionLauncher:</para>
/// <list type="bullet">
///   <item>SYSTEM (MSI deferred action / Intune): grab the console session's user
///   token and <c>CreateProcessAsUser</c> into their session, with their
///   environment block so <c>%LOCALAPPDATA%</c> resolves to their profile.</item>
///   <item>Elevated admin (EXE installer double-click): we're already in that
///   user's session, so a plain <see cref="Process.Start(ProcessStartInfo)"/>
///   runs as them.</item>
/// </list>
/// Best-effort throughout — never throws; a failure just defers to the logon task.
/// </summary>
internal static class ActiveSessionRunner
{
    private const string RemoveUserArg = "--remove-user";

    public static void TryRemoveForActiveUser(string exePath)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.IsSystem)
            {
                TryRunInActiveSession(exePath);
                return;
            }

            // Elevated-admin uninstall: this IS the interactive user's session.
            using var p = Process.Start(new ProcessStartInfo(exePath, RemoveUserArg)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exePath)!,
            });
            CleanupLog.Info(p is not null
                ? "Launched immediate per-user removal in the current session."
                : "Could not launch immediate per-user removal; logon task will handle it.");
        }
        catch (Exception ex)
        {
            CleanupLog.Warn($"Immediate per-user removal skipped ({ex.Message}); logon task will handle it.");
        }
    }

    private static void TryRunInActiveSession(string exePath)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            CleanupLog.Info("No interactive session; logon task will remove printers at next sign-in.");
            return;
        }

        if (!WTSQueryUserToken(sessionId, out var userToken))
        {
            CleanupLog.Info("No signed-in console user; logon task will handle removal at next sign-in.");
            return;
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

            var ok = CreateProcessAsUser(
                userToken,
                lpApplicationName: null,
                lpCommandLine: $"\"{exePath}\" {RemoveUserArg}",
                lpProcessAttributes: IntPtr.Zero,
                lpThreadAttributes: IntPtr.Zero,
                bInheritHandles: false,
                dwCreationFlags: flags,
                lpEnvironment: envBlock,
                lpCurrentDirectory: Path.GetDirectoryName(exePath),
                ref startupInfo,
                out var processInfo);

            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed");
            }

            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
            CleanupLog.Info($"Launched immediate per-user removal in console session {sessionId}.");
        }
        catch (Exception ex)
        {
            CleanupLog.Warn($"Could not launch removal in the active session ({ex.Message}); logon task will handle it.");
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

    // ----- P/Invoke (all in System32; pin search path against DLL hijacking) -----

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
