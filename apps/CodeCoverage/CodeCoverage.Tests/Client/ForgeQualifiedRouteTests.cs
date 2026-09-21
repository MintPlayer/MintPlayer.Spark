using System.Text.RegularExpressions;
using Xunit;

namespace CodeCoverage.Tests.Client;

/// <summary>
/// Every in-app link the SPA builds carries the forge.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This exists because the check that was supposed to prove it missed six sites, and they
/// shipped.</b> The A11 verification grepped for the two-segment URL form as a <em>string</em> —
/// and Angular's router-array form is not a string:
/// </para>
/// <code>
/// this.router.navigate(['/r', owner, name, 'c', sha, 'f'])   // invisible to a "/r/" grep
/// </code>
/// <para>
/// Production answered <c>NG04002: 'r/MintPlayer/MintPlayer.AI/c/16a31b92…/f'</c>. The file page
/// was unreachable from the sunburst, from the file list, and from its own breadcrumb; the
/// short-sha link in every commit grid was dead too. Nothing failed to compile, no test covered it,
/// and the server was answering correctly the whole time — the client was asking for a route that
/// no longer existed.
/// </para>
/// <para>
/// A route is <c>/{provider}/r/{owner}/{repo}…</c> or <c>/{provider}/a/{login}</c>, so a navigation
/// whose <b>first segment</b> is the literal <c>r</c> or <c>a</c> is wrong by construction. That is
/// a property, not a spelling, which is why it can be asserted rather than reviewed.
/// </para>
/// <para>
/// It lives in the .NET suite rather than beside the components because the Angular test runner
/// bundles for the browser, where there is no filesystem to walk.
/// </para>
/// </remarks>
public class ForgeQualifiedRouteTests
{
    /// <summary>
    /// Both spellings: the router-array form (which is what shipped broken) and the interpolated
    /// string form (which the original grep did catch).
    /// </summary>
    private static readonly Regex LegacyRoute = new(
        """\[\s*['"]/[ra]['"]\s*,|['"`]/[ra]/\$\{|routerLink\s*=\s*"/[ra]/""",
        RegexOptions.Compiled);

    private static string ClientSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nx.json")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException(
                $"Could not locate the repository root above {AppContext.BaseDirectory}.");

        return Path.Combine(dir.FullName, "apps", "CodeCoverage", "CodeCoverage", "ClientApp", "src");
    }

    private static string[] SourceFiles() =>
    [
        .. Directory.EnumerateFiles(ClientSourceRoot(), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ts", StringComparison.Ordinal)
                     || f.EndsWith(".html", StringComparison.Ordinal))
            .Where(f => !f.EndsWith(".spec.ts", StringComparison.Ordinal))
    ];

    [Fact]
    public void No_client_source_builds_a_route_whose_first_segment_is_r_or_a()
    {
        var offenders = SourceFiles()
            .Where(file => LegacyRoute.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(ClientSourceRoot(), file).Replace('\\', '/'))
            .Order()
            .ToArray();

        offenders.Should().BeEmpty(
            "these build pre-forge URLs; a route is /{provider}/r/… or /{provider}/a/…, and the "
            + "router answers NG04002 for anything else");
    }

    /// <summary>
    /// The guard has to be able to fail.
    /// </summary>
    /// <remarks>
    /// A scan that silently matches nothing — a wrong pattern, or a walk that found no files —
    /// is exactly the failure that let the original bug through: the A11 grep ran, returned clean,
    /// and was believed. These cases pin the pattern against the shapes that actually shipped, and
    /// against the correct shape so it cannot cry wolf and get deleted.
    /// </remarks>
    [Theory]
    [InlineData("""this.router.navigate(['/r', owner, name, 'c', sha, 'f'])""", true)]
    [InlineData("""<a [routerLink]="['/a', owner()]">""", true)]
    [InlineData("""return ['/r', owner, name, 'c', sha];""", true)]
    [InlineData("""`/r/${owner}/${name}`""", true)]
    [InlineData("""['/', provider(), 'r', owner(), name()]""", false)]
    [InlineData("""['/', this.provider(), 'a', login]""", false)]
    [InlineData("""`/${provider}/r/${owner}/${name}`""", false)]
    public void The_pattern_matches_what_shipped_broken_and_not_what_is_correct(string source, bool expected)
        => LegacyRoute.IsMatch(source).Should().Be(expected);

    [Fact]
    public void The_scan_walks_a_non_empty_set_of_files()
        => SourceFiles().Length.Should().BeGreaterThan(20,
            "a guard that scans nothing passes for the wrong reason");
}
