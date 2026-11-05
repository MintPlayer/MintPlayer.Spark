using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// One major of a platform a package can target (#568 D4, S10): .NET 10, Angular 22, Node.js 24. Its lifecycle decides
/// which release lines are still worth patching. Collected daily by <c>PlatformLifecycleCronJob</c> from the platform's
/// own release index (.NET) or endoflife.date, with npm's <c>next</c> dist-tag adding a framework's preview major.
/// </summary>
/// <remarks>
/// Public facts about public software, so the list is readable anonymously. Written only by the collector: security.json
/// grants <c>QueryRead</c> and nothing else. The id is natural, <c>PlatformReleases/{platform}/{major}</c>, so a run
/// updates the same document every day.
/// </remarks>
[GenerateIndex]
public class PlatformRelease
{
    public const string Prefix = "PlatformReleases/";

    public string? Id { get; set; }

    /// <summary>The platform's name as release lines spell their ecosystem: <c>dotnet</c>, <c>angular</c>, <c>nodejs</c>.</summary>
    public string Platform { get; set; } = string.Empty;

    public int Major { get; set; }

    /// <summary>The source's own name for the release: <c>10.0</c>, <c>22</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary><c>preview</c>, <c>active</c>, <c>maintenance</c> (security fixes only) or <c>eol</c>.</summary>
    public string Phase { get; set; } = string.Empty;

    public bool IsLts { get; set; }

    /// <summary>The first stable release, when the source says; null for a preview and for .NET, whose index has no GA date.</summary>
    public DateTime? ReleasedOn { get; set; }

    /// <summary>When active support ends and only security fixes follow; null when the source has no such phase.</summary>
    public DateTime? ActiveSupportEndsOn { get; set; }

    /// <summary>The end of support; null while it is not announced.</summary>
    public DateTime? SupportEndsOn { get; set; }

    /// <summary>The newest version of this major, as the source names it.</summary>
    public string? LatestVersion { get; set; }

    /// <summary>Where the facts came from.</summary>
    public string Source { get; set; } = string.Empty;

    public DateTime CollectedAtUtc { get; set; }

    public static string IdFor(string platform, int major) => $"{Prefix}{platform}/{major.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
