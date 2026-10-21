using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenPrintDeploy.Server.Spooler;
using OpenPrintDeploy.Shared.Drivers;
using FileDirectory = System.IO.Directory;

namespace OpenPrintDeploy.Server.Drivers;

public sealed class DriverPackages(IPrintSpoolerService spooler, ILogger<DriverPackages> logger)
{
    private readonly SemaphoreSlim _gate = new(1);

    public async Task<(DriverManifest Manifest, string Zip)> GetAsync(string unc, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new NotSupportedException("Driver export requires a Windows print server.");
        await _gate.WaitAsync(ct);
        try
        {
            // Only locally published queues. Never pass a caller-supplied UNC to
            // OpenPrinter, pnputil, a filesystem operation or a remote shell.
            var queue = (await spooler.GetSharedPrintersAsync(ct)).SingleOrDefault(p =>
                p.UncPath.Equals(unc, StringComparison.OrdinalIgnoreCase));
            if (queue is null) throw new InvalidOperationException("This queue is not published by this OpenPrintDeploy server.");
            var driver = WindowsDriverStore.ForLocalQueue(queue.DisplayName);
            if (driver.Environment != "Windows x64" || string.IsNullOrWhiteSpace(driver.InfPath))
                throw new NotSupportedException("Automatic delivery requires an exportable Windows x64 driver package.");
            var inf = Path.GetFullPath(driver.InfPath);
            var store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "DriverStore", "FileRepository") + Path.DirectorySeparatorChar;
            if (!inf.StartsWith(store, StringComparison.OrdinalIgnoreCase) || !File.Exists(inf))
                throw new NotSupportedException("The queue driver is not backed by an exportable Driver Store package.");
            var source = Path.GetDirectoryName(inf)!;
            var fingerprint = new StringBuilder($"{driver.Name}|{driver.Version}|{driver.Date}|{inf}");
            foreach (var file in FileDirectory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var info = new FileInfo(file);
                fingerprint.Append('|').Append(file).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
            }
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString())));
            var root = PrivateDirectory.Create(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenPrintDeploy-DriverPackages"));
            var archive = Path.Combine(root, key + ".zip");
            var metadata = Path.Combine(root, key + ".json");
            if (File.Exists(archive) && File.Exists(metadata))
            {
                var cached = JsonSerializer.Deserialize<DriverManifest>(await File.ReadAllTextAsync(metadata, ct))!;
                return (cached with { UncPath = unc }, archive);
            }
            // Bound disk use. Old versions can be removed by an administrator;
            // existing downloads and installed drivers are never deleted here.
            if (FileDirectory.EnumerateFiles(root, "*.zip").Sum(p => new FileInfo(p).Length) > 4L * 1024 * 1024 * 1024)
                throw new IOException("Driver export cache is full; remove unused cached versions on the server.");
            var working = Path.Combine(root, Guid.NewGuid().ToString("N"));
            FileDirectory.CreateDirectory(working);
            var tempZip = Path.Combine(root, key + ".tmp");
            try
            {
                var published = WindowsDriverStore.PublishedInf(inf);
                if (!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(published), @"^oem[0-9]+\.inf$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    throw new NotSupportedException("Inbox drivers cannot be exported; use the Windows-provided driver on clients.");
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("/export-driver");
                start.ArgumentList.Add(Path.GetFileName(published));
                start.ArgumentList.Add(working);
                using var process = Process.Start(start) ?? throw new IOException("Could not start driver export.");
                var output = process.StandardOutput.ReadToEndAsync(ct);
                var errors = process.StandardError.ReadToEndAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); throw; }
                await Task.WhenAll(output, errors);
                if (process.ExitCode != 0) throw new IOException($"Windows driver export failed ({process.ExitCode}).");
                var exportedInf = FileDirectory.EnumerateFiles(working, "*.inf", SearchOption.AllDirectories)
                    .SingleOrDefault(p => Path.GetFileName(p).Equals(Path.GetFileName(inf), StringComparison.OrdinalIgnoreCase))
                    ?? throw new IOException("Export did not include the queue's INF.");
                if (FileDirectory.EnumerateFiles(working, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) > DriverProtocol.MaxPackageBytes)
                    throw new IOException("Driver package exceeds the 512 MB limit.");
                ZipFile.CreateFromDirectory(working, tempZip, CompressionLevel.Fastest, false);
                var length = new FileInfo(tempZip).Length;
                if (length > DriverProtocol.MaxPackageBytes) throw new IOException("Compressed driver package is too large.");
                await using var zip = File.OpenRead(tempZip);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(zip, ct));
                await zip.DisposeAsync();
                var manifest = new DriverManifest(unc, driver.Name, driver.Environment, driver.Version, driver.Date,
                    Path.GetRelativePath(working, exportedInf).Replace('\\', '/'), hash, length);
                File.Move(tempZip, archive, true);
                await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(manifest), ct);
                logger.LogInformation("Exported signed-driver candidate {Driver} version {Version} for {Queue}; clients validate signatures before installation",
                    driver.Name, driver.Version, unc);
                return (manifest, archive);
            }
            finally
            {
                if (File.Exists(tempZip)) File.Delete(tempZip);
                FileDirectory.Delete(working, true);
            }
        }
        finally { _gate.Release(); }
    }
}
