using System.Text.RegularExpressions;

namespace CodeCoverage.Git.Backports;

/// <summary>
/// A repository's <c>backport.markers</c> (#570 D8): the built-in marker kinds it turns off, and its own
/// platform-anchored packages (a first-party package whose major follows the platform, as <c>@mintplayer/ng-bootstrap</c>
/// follows Angular). A package pattern is a NuGet id or npm name with <c>*</c> as wildcard, matched without case; it
/// is a marker in MSBuild files and in package.json alike, and each pattern is its own family, so a package the pull
/// request adds takes the line's value of a sibling that matches the same pattern.
/// </summary>
public sealed record PlatformMarkerOptions(IReadOnlyCollection<string> Disable, IReadOnlyList<string> Packages)
{
    public const string Tfm = "tfm";
    public const string FrameworkPackages = "framework-packages";
    public const string GlobalJson = "global.json";
    public const string SetupAction = "setup-action";
    public const string NpmFrameworks = "npm-frameworks";

    /// <summary>The kinds <see cref="Disable"/> may name.</summary>
    public static readonly IReadOnlySet<string> KindNames = new HashSet<string>([Tfm, FrameworkPackages, GlobalJson, SetupAction, NpmFrameworks], StringComparer.OrdinalIgnoreCase);

    /// <summary>The characters of a package pattern: NuGet ids and npm names (scope included), plus <c>*</c>.</summary>
    public static readonly Regex PackagePattern = new(@"^(?=.*[A-Za-z0-9])[A-Za-z0-9@/._*-]{1,214}$", RegexOptions.CultureInvariant);
}
