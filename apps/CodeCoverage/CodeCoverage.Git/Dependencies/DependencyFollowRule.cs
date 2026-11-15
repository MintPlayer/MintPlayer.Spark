using System.Text.RegularExpressions;

namespace CodeCoverage.Git.Dependencies;

/// <summary>
/// A coverage.yml <c>follow</c> entry (dependency-companion-pins-PRD §4 A): a pin in <see cref="Path"/> that is not a dependency of
/// its own, written with <see cref="Dependency"/>'s new version in that dependency's group, like the own image tag and the
/// <c>playwright-core@</c> install that follow <c>mcr.microsoft.com/playwright</c>.
/// </summary>
/// <remarks>
/// It lives here, not beside the coverage.yml parser, because the GitWriter's patch validator admits a follower's line by
/// re-running the rule (§7.1a F3), and the GitWriter references CodeCoverage.Git only.
/// </remarks>
/// <param name="Path">A repository-relative path glob.</param>
/// <param name="Match">A pattern with exactly one <c>(?&lt;version&gt;…)</c> group: the text that is replaced.</param>
/// <param name="Version">Optional: a pattern with one <c>version</c> group over the leader's new value; absent, the follower takes
/// that value's first <c>x.y.z[-pre]</c> (S7, <c>v1.64.1-noble</c> → <c>1.64.1</c>).</param>
public sealed partial record DependencyFollowRule(string Dependency, string Path, string Match, string? Version)
{
    /// <summary>A repository's pattern runs on the server: every match is bounded (§8, catastrophic backtracking).</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public const string VersionGroup = "version";

    /// <summary>The <c>version</c> group of every match in <paramref name="text"/>. Throws <see cref="RegexMatchTimeoutException"/>.</summary>
    public IReadOnlyList<Group> Find(string text)
        => [.. Compile(Match).Matches(text).Select(m => m.Groups[VersionGroup]).Where(g => g.Success)];

    /// <summary>The version the follower is written with, or null when the leader's new value has none.</summary>
    public string? FollowerVersion(string leaderValue)
    {
        var match = Version is null ? FirstVersion().Match(leaderValue) : Compile(Version).Match(leaderValue);
        if (!match.Success) return null;
        var group = Version is null ? match : match.Groups[VersionGroup];
        return group.Success && group.Length > 0 ? group.Value : null;
    }

    /// <summary>The first <c>x.y[.z…]</c> in <paramref name="value"/> (S7), or null: <c>v1.64.1-noble</c> → <c>1.64.1</c>. Lockstep
    /// compares a group's members by it (M3).</summary>
    public static string? DefaultVersion(string value) => FirstVersion().Match(value) is { Success: true } match ? match.Value : null;

    public static Regex Compile(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, MatchTimeout);

    /// <summary>Why <paramref name="pattern"/> cannot be a follow pattern, or null.</summary>
    public static string? Problem(string pattern)
    {
        Regex regex;
        try
        {
            regex = Compile(pattern);
        }
        catch (ArgumentException ex)
        {
            return $"is not a regular expression: {ex.Message}";
        }
        // .NET merges same-named groups, so count the declarations in the pattern itself.
        var declared = VersionGroupDeclaration().Count(pattern);
        return declared == 1 && regex.GetGroupNames().Contains(VersionGroup, StringComparer.Ordinal)
            ? null
            : "must have exactly one (?<version>...) group.";
    }

    /// <summary>The first <c>x.y[.z…]</c>, with a prerelease only when it starts with a prerelease word: a Docker tag's
    /// <c>-noble</c> or <c>-alpine</c> is a variant, <c>-beta.2</c> is not.</summary>
    [GeneratedRegex(@"[0-9]+(\.[0-9]+)+(-(alpha|beta|rc|preview|pre|next|canary|dev)(\.?[0-9A-Za-z]+)*)?", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 250)]
    private static partial Regex FirstVersion();

    [GeneratedRegex(@"\(\?(<version>|'version')", RegexOptions.CultureInvariant)]
    private static partial Regex VersionGroupDeclaration();
}
