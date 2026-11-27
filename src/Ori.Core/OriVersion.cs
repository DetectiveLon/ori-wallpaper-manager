using System.Reflection;

namespace Ori.Core;

/// <summary>
/// Version Ori reports to external services. The number comes from the assembly
/// metadata stamped at build time from the project's single Version property
/// (overridden by release tags), so no source file keeps its own copy of it.
/// </summary>
public static class OriVersion
{
    /// <summary>The informational version without any +revision metadata.</summary>
    public static string UserAgentVersion
    {
        get
        {
            var informational = typeof(OriVersion).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(informational))
                return "unknown";

            var plus = informational.IndexOf('+');
            var version = (plus >= 0 ? informational[..plus] : informational).Trim();
            return version.Length == 0 ? "unknown" : version;
        }
    }

    /// <summary>User-Agent product token used on Wallhaven requests.</summary>
    public static string UserAgent => $"Ori-Wallpaper/{UserAgentVersion} (+https://github.com/)";
}
