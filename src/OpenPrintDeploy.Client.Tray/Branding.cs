using System.Drawing;
using WpfApplication = System.Windows.Application;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>
/// One place for the client's name, palette, and logo assets. Colours are
/// sampled from <c>Assets/logo.png</c> so the UI matches the icon. The assets
/// are compiled into the exe as WPF resources (see the .csproj), so these
/// loaders work without any loose files on disk.
/// </summary>
internal static class Branding
{
    /// <summary>User-facing product name. Used in the tray tooltip, menus,
    /// balloon titles, and dialog captions.</summary>
    public const string ProductName = "Open Print Deploy Client";

    // Sampled straight from the logo art.
    public static readonly Color Navy = Color.FromArgb(0x0B, 0x40, 0x70); // printer body / primary text
    public static readonly Color Teal = Color.FromArgb(0x18, 0xC5, 0xB8); // arrows / accent
    public static readonly Color Sky  = Color.FromArgb(0x8A, 0xE1, 0xF2); // light arrow / hover

    private static readonly Uri IconUri = new("pack://application:,,,/Assets/appicon.ico");
    private static readonly Uri LogoUri = new("pack://application:,,,/Assets/logo.png");

    /// <summary>The full multi-resolution app icon (all frames).</summary>
    public static Icon LoadIcon() => LoadIcon(size: null);

    /// <summary>The app icon, preferring the frame nearest <paramref name="size"/>.</summary>
    public static Icon LoadIcon(Size? size)
    {
        var info = WpfApplication.GetResourceStream(IconUri)
            ?? throw new InvalidOperationException("appicon.ico resource is missing from the app.");
        using var stream = info.Stream;
        return size is { } s ? new Icon(stream, s) : new Icon(stream);
    }

    /// <summary>The trimmed, transparent logo bitmap (512px square).</summary>
    public static Bitmap LoadLogo()
    {
        var info = WpfApplication.GetResourceStream(LogoUri)
            ?? throw new InvalidOperationException("logo.png resource is missing from the app.");
        using var stream = info.Stream;
        return new Bitmap(stream);
    }
}
