using System.Text;

namespace OpenPrintDeploy.Shared.Drivers;

public static class DriverProcessOutput
{
    // Fits inside both IPC messages and the dashboard's per-printer error limit.
    public const int MaxDiagnosticCharacters = 2048;

    public static async Task<string> ReadDiagnosticAsync(TextReader reader, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int count;
        // Keep draining after the cap so a verbose child cannot block on its pipe.
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            var keep = Math.Min(count, MaxDiagnosticCharacters - result.Length);
            result.Append(buffer, 0, keep);
        }
        return result.ToString().Trim();
    }
}
