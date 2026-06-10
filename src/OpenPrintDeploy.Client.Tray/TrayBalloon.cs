using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>
/// Shows a tray notification that displays our own logo instead of the generic
/// Windows info glyph. WinForms' <see cref="NotifyIcon.ShowBalloonTip(int)"/>
/// can't set a custom balloon icon, so we issue the <c>Shell_NotifyIcon</c>
/// NIM_MODIFY ourselves with the NIIF_USER flag, reusing the window + id the
/// NotifyIcon already created (read by reflection). If those internals ever
/// differ, we fall back to the standard balloon — never a crash.
/// </summary>
internal static class TrayBalloon
{
    public static void Show(NotifyIcon notifyIcon, string title, string text, Icon balloonIcon, int fallbackTimeoutMs)
    {
        if (!TryShowWithLogo(notifyIcon, title, text, balloonIcon))
        {
            notifyIcon.ShowBalloonTip(fallbackTimeoutMs, title, text, ToolTipIcon.None);
        }
    }

    private static bool TryShowWithLogo(NotifyIcon notifyIcon, string title, string text, Icon balloonIcon)
    {
        try
        {
            var type = typeof(NotifyIcon);
            var window = type.GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)?
                .GetValue(notifyIcon) as NativeWindow;
            var idValue = type.GetField("_id", BindingFlags.NonPublic | BindingFlags.Instance)?
                .GetValue(notifyIcon);
            if (window is null || idValue is null)
            {
                return false;
            }

            var hwnd = window.Handle;
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var data = new NOTIFYICONDATAW
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = hwnd,
                uID = Convert.ToUInt32(idValue),
                uFlags = NIF_INFO,
                uCallbackMessage = 0,
                hIcon = IntPtr.Zero,
                szTip = string.Empty,
                dwState = 0,
                dwStateMask = 0,
                szInfo = Truncate(text, 255),
                uVersionOrTimeout = 0,
                szInfoTitle = Truncate(title, 63),
                dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON,
                guidItem = Guid.Empty,
                hBalloonIcon = balloonIcon.Handle,
            };

            return Shell_NotifyIcon(NIM_MODIFY, ref data);
        }
        catch
        {
            // Reflection shape changed or the shell call threw — let the caller
            // fall back to the plain balloon.
            return false;
        }
    }

    private static string Truncate(string? s, int max)
    {
        s ??= string.Empty;
        return s.Length <= max ? s : s[..max];
    }

    private const uint NIM_MODIFY      = 0x00000001;
    private const uint NIF_INFO        = 0x00000010;
    private const uint NIIF_USER       = 0x00000004;
    private const uint NIIF_LARGE_ICON = 0x00000020;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATAW lpData);
}
