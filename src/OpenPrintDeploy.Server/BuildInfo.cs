using System.Reflection;

namespace OpenPrintDeploy.Server;

/// <summary>Build metadata surfaced in the admin UI.</summary>
public static class BuildInfo
{
    /// <summary>
    /// The product version — <see cref="AssemblyInformationalVersionAttribute"/>,
    /// which is stamped from the csproj <c>Version</c> (the CI release passes the
    /// git tag) — falling back to the plain assembly version. Any build-metadata
    /// suffix (e.g. <c>+&lt;commit&gt;</c> from SourceLink) is trimmed for display.
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    private static string ResolveVersion()
    {
        var informational = typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        var raw = informational
            ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString()
            ?? "(unknown)";

        var plus = raw.IndexOf('+');
        return plus >= 0 ? raw[..plus] : raw;
    }
}
