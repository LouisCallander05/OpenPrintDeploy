using System.Text.Json;

namespace OpenPrintDeploy.Server.Auth;

/// <summary>
/// Persists the Settings-page admin grants to a small JSON file (alongside the
/// app in dev, under ProgramData on a Windows server). A file — not the DB — so
/// there's no migration, and an operator can read/fix it directly. All reads
/// degrade to "empty" on error so a corrupt file can't take down authorization.
/// </summary>
public sealed class AdminAccessStore
{
    private readonly string _path;
    private readonly ILogger<AdminAccessStore> _logger;
    private readonly object _lock = new();

    public AdminAccessStore(IHostEnvironment env, ILogger<AdminAccessStore> logger)
    {
        _logger = logger;
        _path = ResolvePath(env);
    }

    public string FilePath => _path;

    /// <summary>The grants only (empty when missing OR unreadable).</summary>
    public AdminAccess Load() => Read().Access;

    /// <summary>
    /// Loads the stored grants, distinguishing "no file yet" (legitimate
    /// first-run open state) from "file present but unreadable" (corrupt or
    /// tampered). The latter must NOT collapse to the open state — a damaged
    /// admin file silently re-granting every authenticated user is a fail-open
    /// hole, so callers treat <see cref="AdminAccessLoad.Unreadable"/> as sealed
    /// and rely on the appsettings break-glass grants to recover.
    /// </summary>
    public AdminAccessLoad Read()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                return new AdminAccessLoad(AdminAccess.Empty, Unreadable: false);
            }

            try
            {
                var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(_path));
                return dto is null
                    ? new AdminAccessLoad(AdminAccess.Empty, Unreadable: true)
                    : new AdminAccessLoad(new AdminAccess(Clean(dto.Groups), Clean(dto.Users)), Unreadable: false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Admin-access file {Path} exists but could not be read; sealing admin access (only " +
                    "appsettings Auth:Admin grants apply) rather than re-opening it to every authenticated user.",
                    _path);
                return new AdminAccessLoad(AdminAccess.Empty, Unreadable: true);
            }
        }
    }

    public void Save(AdminAccess access)
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            var dto = new Dto { Groups = Clean(access.Groups), Users = Clean(access.Users) };
            File.WriteAllText(_path, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
            _logger.LogInformation("Admin access updated: {Groups} group(s), {Users} user(s).",
                dto.Groups.Count, dto.Users.Count);
        }
    }

    private static List<string> Clean(IEnumerable<string>? items)
        => (items ?? [])
            .Select(s => s?.Trim() ?? string.Empty)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ResolvePath(IHostEnvironment env)
    {
        if (env.IsDevelopment() || !OperatingSystem.IsWindows())
        {
            return Path.Combine(env.ContentRootPath, "admin-access.json");
        }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "OpenPrintDeploy");
        return Path.Combine(dir, "admin-access.json");
    }

    private sealed class Dto
    {
        public List<string> Groups { get; set; } = [];
        public List<string> Users { get; set; } = [];
    }
}
