using System.Reflection;

namespace Rtfm.Cli;

/// <summary>
/// The running <c>rtfm</c> version, read from the assembly's informational
/// version (set centrally in the repo-root <c>Directory.Build.props</c>).
/// </summary>
internal static class RtfmVersion
{
    /// <summary>e.g. <c>1.0.0</c>. Falls back to the assembly version if the attribute is absent.</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // Strip any build metadata suffix ("1.0.0+<gitsha>") defensively — the
        // props file disables it, but a different build config might not.
        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
