using System.Globalization;
using System.IO;

namespace OpenPrintDeploy.Client.Tray;

/// <summary>
/// Minimal rolling file logger for field diagnosis. Writes timestamped lines to
/// <c>%LOCALAPPDATA%\OpenPrintDeploy\opd-client.log</c>, rotating to <c>.1</c>
/// when the file exceeds ~1 MB. No framework dependencies — just a static lock
/// and <see cref="File.AppendAllText"/>.
/// </summary>
internal static class ClientLog
{
    private const long MaxBytes = 1_048_576; // 1 MB
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenPrintDeploy");
    private static readonly string LogPath = Path.Combine(LogDir, "opd-client.log");
    private static readonly string BackupPath = LogPath + ".1";
    private static readonly object Lock = new();

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);
    public static void Error(string message) => Write("ERR", message);
    public static void Error(string message, Exception ex) => Write("ERR", $"{message}: {ex.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(LogDir);
                Rotate();
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                File.AppendAllText(LogPath, $"{timestamp} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Never crash the tray over a logging failure.
        }
    }

    private static void Rotate()
    {
        try
        {
            if (!File.Exists(LogPath))
                return;
            if (new FileInfo(LogPath).Length < MaxBytes)
                return;
            File.Copy(LogPath, BackupPath, overwrite: true);
            File.Delete(LogPath);
        }
        catch
        {
            // Best effort — if rotation fails, keep appending.
        }
    }
}
