using System.Text.RegularExpressions;
using Xunit;

namespace CodeCoverage.Tests.Infrastructure;

/// <summary>
/// Nothing compares the viewer's allowed-owner set against a bare login.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This bug class has shipped three times.</b> `SparkVisibility.GetAllowedOwnersAsync` returns
/// <c>provider:login</c> KEYS. <c>"acme"</c> is not <c>"github:acme"</c>, so comparing an
/// <c>OwnerLogin</c> / <c>Login</c> / <c>AccountLogin</c> against that set matches <b>nothing</b>.
/// </para>
/// <para>
/// Every occurrence failed <em>closed</em>, which is precisely why none was caught: there is no
/// security hole and no exception — the feature simply stops working, and a security test asserting
/// "a stranger is refused" passes even more emphatically than before.
/// </para>
/// <list type="number">
///   <item><c>RepositoryVisibility.IsListed</c> — owners stopped seeing their own private and
///   disconnected repositories through every imperative caller (fixed 2026-09-21).</item>
///   <item><c>ApiTokenActions</c> ×2, <c>RevokeTokenAction</c>, <c>DeleteDataAction</c> — token
///   creation, repository-scoped token saves, token revocation and delete-data were refused for
///   every user on production (fixed 2026-09-21).</item>
/// </list>
/// <para>
/// The doc comment on <c>QueryAllowedOwnersAsync</c> actively described the old behaviour for weeks
/// after the code changed, so four call sites were written against a stale remark. A comment could
/// not hold this line; a test can.
/// </para>
/// </remarks>
public class OwnerKeyComparisonTests
{
    /// <summary>
    /// Properties that hold a BARE login and must never be compared against the allowed-owner set.
    /// </summary>
    private static readonly string[] BareLoginProperties = ["OwnerLogin", "AccountLogin"];

    /// <summary>
    /// The two shapes that consume the allowed-owner set: the membership helper, and
    /// <c>Contains</c> over the array it returns.
    /// </summary>
    private static Regex Comparison(string property) => new(
        $@"(CanManageOwnerAsync\([^)]*\.{property}\b)"
        + $@"|(owners\.Contains\(\s*[A-Za-z_]\w*\.{property}\b)"
        + $@"|(allowedOwners?\.Contains\(\s*[A-Za-z_]\w*\.{property}\b)",
        RegexOptions.Compiled);

    [Fact]
    public void No_allowed_owner_comparison_uses_a_bare_login()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            foreach (var property in BareLoginProperties)
            {
                foreach (Match match in Comparison(property).Matches(text))
                {
                    var line = text.Take(match.Index).Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetFileName(file)}:{line}  {match.Value.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "The allowed-owner set holds `provider:login` KEYS. Compare an OwnerKey, never a bare "
            + "login — \"acme\" is not \"github:acme\", so this matches nothing and fails CLOSED:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Proves the detector fires, so a green run means "no offenders" and not "no detection".
    /// </summary>
    [Theory]
    [InlineData("await visibility.CanManageOwnerAsync(repository.OwnerLogin)", true)]
    [InlineData("await visibility.CanManageOwnerAsync(token.AccountLogin)", true)]
    [InlineData("owners.Contains(repository.OwnerLogin, StringComparer.OrdinalIgnoreCase)", true)]
    [InlineData("await visibility.CanManageOwnerAsync(repository.OwnerKey)", false)]
    [InlineData("owners.Contains(repository.OwnerKey, StringComparer.OrdinalIgnoreCase)", false)]
    public void The_detector_matches_the_broken_shapes_and_not_the_correct_one(string snippet, bool shouldMatch)
    {
        var matched = BareLoginProperties.Any(p => Comparison(p).IsMatch(snippet));
        Assert.Equal(shouldMatch, matched);
    }

    private static IEnumerable<string> SourceFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "apps")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var app = Path.Combine(dir!.FullName, "apps", "CodeCoverage");
        Assert.True(Directory.Exists(app), $"Expected the app at {app}");

        return Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains("CodeCoverage.Tests", StringComparison.Ordinal));
    }

    [Fact]
    public void The_sweep_reads_a_substantial_number_of_files()
        => Assert.True(SourceFiles().Count() > 100, "Expected the sweep to walk the whole app.");
}
