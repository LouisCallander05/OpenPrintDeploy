using Microsoft.Extensions.Options;

namespace OpenPrintDeploy.Server.Directory;

/// <summary>
/// Config-backed directory for local development — no AD required. Resolves
/// groups and machine OUs from the <c>Directory:Stub</c> maps so the full sync
/// pipeline runs on a dev box.
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

    public Task<string?> GetMachineOuDnAsync(string machineName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(machineName))
        {
            return Task.FromResult<string?>(null);
        }

        return Task.FromResult(_stub.Machines.GetValueOrDefault(machineName.Trim()));
    }
}
