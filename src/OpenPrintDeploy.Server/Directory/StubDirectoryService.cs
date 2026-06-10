using Microsoft.Extensions.Options;

namespace OpenPrintDeploy.Server.Directory;

/// <summary>
/// Config-backed directory for local development — no AD required. Resolves
/// user groups and the admin UI's group catalog from the <c>Directory:Stub</c>
/// maps so the full sync pipeline runs on a dev box.
/// </summary>
public sealed class StubDirectoryService : IDirectoryService
{
    private readonly StubOptions _stub;

    public StubDirectoryService(IOptions<DirectoryOptions> options)
    {
        _stub = options.Value.Stub;
    }

    public Task<IReadOnlySet<string>> GetGroupSidsAsync(string username, CancellationToken ct = default)
    {
        var key = DirectoryUsername.Normalize(username);
        IReadOnlySet<string> sids = _stub.Users.TryGetValue(key, out var configured)
            ? configured.ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        return Task.FromResult(sids);
    }

    public Task<bool> ValidateCredentialsAsync(string username, string password, CancellationToken ct = default)
        // Dev never uses Basic auth (it runs the Dev header scheme); accept any
        // non-empty pair so the method is usable if someone wires it up locally.
        => Task.FromResult(!string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password));

    public Task<string?> ResolveGroupSidByNameAsync(string name, CancellationToken ct = default)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        string? sid = _stub.Groups
            .FirstOrDefault(kvp => kvp.Key.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            .Value;
        return Task.FromResult<string?>(sid);
    }

    public Task<IReadOnlyList<DirectoryGroup>> SearchGroupsAsync(
        string query, int limit, CancellationToken ct = default)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        IReadOnlyList<DirectoryGroup> groups = _stub.Groups
            .Where(kvp => trimmed.Length == 0
                || kvp.Key.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, limit))
            .Select(kvp => new DirectoryGroup(kvp.Value, kvp.Key))
            .ToList();
        return Task.FromResult(groups);
    }

    public Task<string?> ResolveGroupNameAsync(string sid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sid))
        {
            return Task.FromResult<string?>(null);
        }

        string? name = _stub.Groups
            .FirstOrDefault(kvp => string.Equals(kvp.Value, sid, StringComparison.OrdinalIgnoreCase))
            .Key;
        return Task.FromResult<string?>(name);
    }

    public Task<DirectoryDiagnostics> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new DirectoryDiagnostics(
            Provider: "Stub",
            AuthMode: "n/a",
            Server: null,
            SearchBase: null,
            Connected: true,
            SampleGroupCount: _stub.Groups.Count,
            Error: null));
    }
}
