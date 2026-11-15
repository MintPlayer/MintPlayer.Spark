using System.Text.RegularExpressions;
using System.Xml.Linq;
using CodeCoverage.Versioning.Versions;

namespace CodeCoverage.PlatformLifecycle;

/// <summary>
/// companion-version-maps-PRD §6.4: the MSBuild, Roslyn and NuGet versions one .NET SDK feature band ships, read from the
/// VMR tag of the band's first release (S1). One document per band, not per SDK: a band's patch releases keep the
/// components' major.minor, and the companion ranges use nothing finer (<c>&lt;=18.12</c>). Written only by
/// <c>PlatformLifecycleCronJob</c>; the dependency planner loads all of them in one request.
/// </summary>
public sealed class SdkBandComponents
{
    public const string Prefix = "SdkBandComponents/";

    public string? Id { get; set; }

    /// <summary>The feature band: <c>11.0.1xx</c>.</summary>
    public string Band { get; set; } = string.Empty;

    /// <summary>The VMR tag read: <c>v11.0.100</c>, or a preview SDK's own tag while the band has no release. A new tag for
    /// the band (the preview's release) is read again.</summary>
    public string Tag { get; set; } = string.Empty;

    public string? Msbuild { get; set; }
    public string? Roslyn { get; set; }
    public string? Nuget { get; set; }

    /// <summary>The tag does not exist (404): recorded so it is not asked again until the band's tag changes.</summary>
    public bool TagMissing { get; set; }

    public DateTime CollectedAtUtc { get; set; }

    public static string IdFor(string band) => $"{Prefix}dotnet/{band}";
}

/// <summary>companion-version-maps-PRD S1: which VMR tags to read, and the component versions in the files read there.</summary>
public static partial class SdkComponentSources
{
    /// <summary>The first SDK the VMR tags (<c>v10.0.100</c>; <c>v9.0.300</c> and <c>v8.0.201</c> answer 404, S1).</summary>
    public const int FirstMajor = 10;

    public const string MsbuildPath = "src/msbuild/eng/Versions.props";
    public const string RoslynPath = "src/roslyn/eng/Versions.props";
    public const string NugetPath = "src/nuget-client/build/config.props";

    [GeneratedRegex(@"^(\d+)\.(\d+)\.(\d)(\d\d)(-[0-9A-Za-z.]+)?$")]
    private static partial Regex SdkVersion();

    /// <summary>
    /// The bands of a channel and the tag each is read at, from the channel's newest SDK. A released SDK (<c>10.0.403</c>)
    /// covers every band up to its own, each read at its <c>x00</c> release tag; a preview (<c>11.0.100-rc.3.26522.148</c>)
    /// only its own band, at its own tag. A channel before <see cref="FirstMajor"/> has none.
    /// </summary>
    public static IReadOnlyList<(string Band, string Tag)> Bands(string? latestSdk)
    {
        if (latestSdk is null || SdkVersion().Match(latestSdk) is not { Success: true } m
            || !int.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var major) || major < FirstMajor)
            return [];
        var (channel, band) = ($"{m.Groups[1].Value}.{m.Groups[2].Value}", int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
        if (band == 0)
            return [];
        if (m.Groups[5].Success)
            return [($"{channel}.{band}xx", $"v{latestSdk}")];
        return [.. Enumerable.Range(1, band).Select(b => ($"{channel}.{b}xx", $"v{channel}.{b}00"))];
    }

    /// <summary>MSBuild's <c>VersionPrefix</c> (the unconditioned one).</summary>
    public static string? ReadMsbuild(string xml)
        => Valid(Elements(xml, "VersionPrefix").FirstOrDefault(e => e.Attribute("Condition") is null)?.Value);

    /// <summary>Roslyn's <c>MajorVersion</c>.<c>MinorVersion</c>.<c>PatchVersion</c>.</summary>
    public static string? ReadRoslyn(string xml) => Join(xml, "MajorVersion", "MinorVersion", "PatchVersion");

    /// <summary>NuGet's <c>MajorNuGetVersion</c>.<c>MinorNuGetVersion</c>.<c>PatchNuGetVersion</c>.</summary>
    public static string? ReadNuget(string xml) => Join(xml, "MajorNuGetVersion", "MinorNuGetVersion", "PatchNuGetVersion");

    private static string? Join(string xml, params string[] names)
    {
        var parts = names.Select(n => Elements(xml, n).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0 && v.All(char.IsAsciiDigit))).ToList();
        return parts.Any(p => p is null) ? null : Valid(string.Join('.', parts));
    }

    private static IEnumerable<XElement> Elements(string xml, string name)
        => XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == name);

    private static string? Valid(string? version) => SemVer.TryParse(version?.Trim(), out _) ? version!.Trim() : null;
}
