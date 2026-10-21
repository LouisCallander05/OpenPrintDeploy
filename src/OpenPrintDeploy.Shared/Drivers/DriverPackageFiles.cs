using System.IO.Compression;

namespace OpenPrintDeploy.Shared.Drivers;

public static class DriverPackageFiles
{
    public static string Resolve(string root, string relative)
    {
        // Windows path rules even when validation tests run on other platforms.
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length == 0 || parts.Any(p => p.Length == 0 || p is "." or ".."
            || p.EndsWith('.') || p.EndsWith(' ') || p.Any(c => c < 32 || ":*?\"<>|".Contains(c))
            || IsDeviceName(p)))
            throw new InvalidDataException("Invalid package path.");
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package path escapes its directory.");
        return result;
    }

    public static void Extract(string zipPath, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count > 10000) throw new InvalidDataException("Too many package files.");
        long total = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var path = Resolve(destination, entry.FullName);
            if (!seen.Add(path)) throw new InvalidDataException("Duplicate package path.");
            total = checked(total + entry.Length);
            if (total > DriverProtocol.MaxPackageBytes) throw new InvalidDataException("Expanded package is too large.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var source = entry.Open();
            using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = source.Read(buffer)) > 0)
            {
                copied += read;
                if (copied > entry.Length) throw new InvalidDataException("Incorrect package file length.");
                target.Write(buffer, 0, read);
            }
        }
    }

    private static bool IsDeviceName(string name)
    {
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT"))
                && (char.IsDigit(stem[3]) || "¹²³".Contains(stem[3])));
    }
}
