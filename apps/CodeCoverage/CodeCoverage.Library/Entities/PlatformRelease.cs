using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// One channel of a platform a package can target (#568 D4, S10; #606 D6): .NET 10.0, Angular 22, Node.js 24, Python 3.14.
/// Its lifecycle decides which release lines are still worth patching, and the table of channels is what the dependency
/// lookup offers SDK moves from (#606 D9). Collected daily by <c>PlatformLifecycleCronJob</c> from Microsoft's release
/// index (.NET) or endoflife.date (everything else), with each platform's preview feed adding a channel not released yet.
/// </summary>
/// <remarks>
/// Public facts about public software, so the list is readable anonymously. Written only by the collector: security.json
/// grants <c>QueryRead</c> and nothing else. The id is natural, <c>PlatformReleases/{platform}/{channel}</c>, so a run
/// updates the same document every day. It is per channel, not per major: Python, Go, Ruby and Rust release a channel per
/// minor (3.13, 1.25), all on one major.
/// </remarks>
[GenerateIndex]
public class PlatformRelease
{
    public const string Prefix = "PlatformReleases/";

    public string? Id { get; set; }

    /// <summary>The platform's name, as release lines spell their ecosystem and the dependency updater its SDK:
    /// <c>dotnet</c>, <c>node</c>, <c>python</c>, <c>java</c>, <c>go</c>, <c>php</c>, <c>ruby</c>, <c>rust</c>,
    /// <c>angular</c>, <c>react</c>, <c>vue</c>.</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>The channel as the platform names it: <c>10.0</c>, <c>24</c>, <c>3.14</c>, <c>1.99</c>.</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>The channel's leading number, which release lines compare.</summary>
    public int Major { get; set; }

    /// <summary>The source's own name for the release: <c>10.0</c>, <c>22</c>; for a Rust beta, its dated toolchain.</summary>
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

    /// <summary>The newest version of this channel, as the source names it.</summary>
    public string? LatestVersion { get; set; }

    /// <summary>When <see cref="LatestVersion"/> was released, when the source says.</summary>
    public DateTime? LatestReleasedOn { get; set; }

    /// <summary>.NET: the newest SDK of the channel (<c>global.json</c> pins an SDK, not a runtime).</summary>
    public string? LatestSdk { get; set; }

    /// <summary>Where the facts came from.</summary>
    public string Source { get; set; } = string.Empty;

    public DateTime CollectedAtUtc { get; set; }

    /// <summary>The document id of a channel. The channel lands in an id, so only digits and dots are taken.</summary>
    public static string IdFor(string platform, string channel) => $"{Prefix}{platform}/{channel}";
}
