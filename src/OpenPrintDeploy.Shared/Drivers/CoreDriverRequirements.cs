namespace OpenPrintDeploy.Shared.Drivers;

/// <summary>All declared core dependencies must be present at a sufficient version.</summary>
public static class CoreDriverRequirements
{
    public const int MaxDependencies = 16;

    public static void Verify(IReadOnlyCollection<Guid> required,
        IReadOnlyCollection<CoreDriverIdentity>? expected, IReadOnlyCollection<CoreDriverIdentity> installed)
    {
        if (required.Count == 0) return;
        if (expected is null)
            throw new InvalidOperationException("The driver requires core packages that this server did not enumerate. Update the OPD server before repairing connections.");
        Validate(expected);
        Validate(installed);
        foreach (var id in required)
        {
            var target = expected.SingleOrDefault(d => d.Id == id)
                ?? throw new InvalidOperationException($"Core driver {id} is missing from server preparation metadata.");
            var local = installed.SingleOrDefault(d => d.Id == id);
            if (local is null || local.Date < target.Date || (local.Date == target.Date && local.Version < target.Version))
                throw new InvalidOperationException($"Core driver {id} is missing or older than the print server; waiting for SYSTEM driver preparation.");
        }
    }

    public static void Validate(IReadOnlyCollection<CoreDriverIdentity> drivers)
    {
        if (drivers.Count > MaxDependencies || drivers.Any(d => d.Id == Guid.Empty || d.Version == 0 || d.Date <= 0)
            || drivers.Select(d => d.Id).Distinct().Count() != drivers.Count)
            throw new InvalidDataException("Invalid core driver dependency metadata.");
    }
}
