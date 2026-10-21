using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenPrintDeploy.Client.Core;
using OpenPrintDeploy.Shared.Drivers;

namespace OpenPrintDeploy.Client.Service;

internal sealed class DriverInstaller(ILogger<DriverInstaller> logger)
{
    public static string StorageRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenPrintDeploy-DriverClient");

    public async Task<DriverResponse> PrepareAsync(DriverRequest request, CancellationToken ct)
    {
        var settings = MachineSettings.Load();
        if (!settings.Enabled) return new(false, "Automatic driver preparation is paused by the administrator.");
        if (!DriverSourcePolicy.IsAllowed(request.UncPath, settings.Hosts)
            || string.IsNullOrEmpty(request.Ticket) || request.Ticket.Length > 8192)
            return new(false, "Driver request is not permitted by machine policy.");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        if (!string.IsNullOrWhiteSpace(settings.Pin))
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) => CertificatePinning.Matches(cert, settings.Pin);
        using var http = new HttpClient(handler) { BaseAddress = settings.Server, Timeout = TimeSpan.FromMinutes(2) };
        var manifest = await ReadManifestAsync(http, request, ct);
        if (!manifest.UncPath.Equals(request.UncPath, StringComparison.OrdinalIgnoreCase)
            || manifest.Environment != "Windows x64" || string.IsNullOrWhiteSpace(manifest.DriverName)
            || manifest.Length <= 0 || manifest.Length > DriverProtocol.MaxPackageBytes
            || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Server returned an invalid driver manifest.");
        var root = PrivateDirectory.Create(StorageRoot);
        var stamp = Path.Combine(root, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifest.DriverName))) + ".installed");
        var current = WindowsDriverStore.Find(manifest.DriverName);
        if (current is not null && current.Environment == manifest.Environment
            && current.Version == manifest.Version && current.Date == manifest.Date
            && File.Exists(stamp) && (await File.ReadAllTextAsync(stamp, ct)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            return new(true);
        var work = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var zipPath = Path.Combine(work, "package.zip");
            using var message = new HttpRequestMessage(HttpMethod.Post, "drivers/package")
            { Content = JsonContent.Create(new { request.Ticket, manifest.Sha256 }) };
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            await CheckResponseAsync(response, ct);
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await DriverWire.CopyLimitedAsync(source, target, manifest.Length, ct);
            if (new FileInfo(zipPath).Length != manifest.Length) throw new InvalidDataException("Incomplete driver download.");
            await using (var zip = File.OpenRead(zipPath))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(zip, ct));
                if (!hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Driver checksum mismatch.");
            }
            var extracted = Path.Combine(work, "files");
            Directory.CreateDirectory(extracted);
            DriverPackageFiles.Extract(zipPath, extracted);
            var inf = DriverPackageFiles.Resolve(extracted, manifest.InfPath);
            if (!inf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) || !File.Exists(inf))
                throw new InvalidDataException("Driver INF is missing.");
            // Run blocking native installation in a bounded child process. A
            // stuck spooler call must not pin the IPC service indefinitely.
            var job = Path.Combine(work, "install.json");
            await File.WriteAllTextAsync(job, JsonSerializer.Serialize(manifest), ct);
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "OpenPrintDeploy.Client.Service.exe"))
            { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--install-package");
            start.ArgumentList.Add(job);
            using var child = Process.Start(start) ?? throw new IOException("Could not start driver installer.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(CancellationToken.None); throw; }
            if (child.ExitCode != 0) throw new IOException($"Windows rejected the driver package (installer exit {child.ExitCode}).");
            current = WindowsDriverStore.Find(manifest.DriverName);
            if (current is null || current.Version != manifest.Version || current.Date != manifest.Date)
                throw new IOException("Installed driver does not match the print server version.");
            await File.WriteAllTextAsync(stamp, manifest.Sha256, ct);
            logger.LogInformation("Prepared {Driver} version {Version} for {Queue}", manifest.DriverName, manifest.Version, request.UncPath);
            return new(true);
        }
        finally { Directory.Delete(work, true); }
    }

    private static async Task<DriverManifest> ReadManifestAsync(HttpClient http, DriverRequest request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "drivers/manifest") { Content = JsonContent.Create(new { request.Ticket }) };
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await CheckResponseAsync(response, ct);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        await DriverWire.CopyLimitedAsync(source, buffer, DriverProtocol.MaxMessageBytes, ct);
        return JsonSerializer.Deserialize<DriverManifest>(buffer.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Empty driver manifest.");
    }

    private static async Task CheckResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        await DriverWire.CopyLimitedAsync(source, buffer, DriverProtocol.MaxMessageBytes, ct);
        string? detail = null;
        try
        {
            using var json = JsonDocument.Parse(buffer.ToArray());
            if (json.RootElement.TryGetProperty("detail", out var field)) detail = field.GetString();
        }
        catch (JsonException) { }
        throw new IOException($"Driver server returned {(int)response.StatusCode}: {detail ?? response.ReasonPhrase}");
    }
}
