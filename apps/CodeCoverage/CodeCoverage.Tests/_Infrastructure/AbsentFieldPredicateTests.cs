using System.Text.RegularExpressions;
using Xunit;

namespace CodeCoverage.Tests.Infrastructure;

/// <summary>
/// No RavenDB query may test a late-added boolean with <c>!x</c> or <c>== false</c>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>This guards a bug that shipped.</b> <c>Commit.ContributedFromFork</c> is a non-nullable
/// <c>bool</c>, so <c>!c.ContributedFromFork</c> reads as obviously correct — but an index map runs
/// over stored JSON, and every commit written before the field existed simply has no such property.
/// An absent field does not satisfy an equality in RavenDB, so the predicate matched <b>none</b> of
/// them and blanked the commit list, history chart, sparklines, branch list and branch badge for
/// every pre-existing repository.
/// </para>
/// <para>
/// The whole .NET suite stayed green, because every fixture constructs a <c>Commit</c> and
/// therefore writes the field. It was caught by opening the application in a browser. Behaviour
/// tests cannot cover this cheaply — a seeded <c>false</c> reproduces nothing — so the shape of the
/// source is what gets asserted, the same approach <c>ForgeQualifiedRouteTests</c> takes.
/// </para>
/// <para>
/// The same reasoning already existed on <c>RepositoryVisibility.ListingFilter</c>, written as
/// <c>!= Disconnected</c> rather than <c>== Connected</c> for exactly this reason. That comment did
/// not stop the next occurrence; this test does.
/// </para>
/// </remarks>
public class AbsentFieldPredicateTests
{
    /// <summary>
    /// Booleans added to an entity after documents already existed. Add to this list whenever a
    /// non-nullable bool is introduced on a type that has stored documents.
    /// </summary>
    private static readonly string[] LateAddedBooleans = ["ContributedFromFork"];

    /// <summary>
    /// A LINQ negation (<c>!x.Field</c>) or an equality against false, on one of those names.
    /// </summary>
    /// <remarks>
    /// Deliberately does not match <c>x.Field != true</c>, which is the correct form, nor a bare
    /// <c>x.Field</c> used as a positive test — a positive test is safe, because an absent field
    /// correctly fails it.
    /// </remarks>
    private static Regex ForbiddenPredicate(string field) => new(
        $@"(![A-Za-z_]\w*\.{field}\b)|(\.{field}\s*==\s*false)",
        RegexOptions.Compiled);

    [Fact]
    public void No_query_predicate_negates_a_late_added_boolean()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            foreach (var field in LateAddedBooleans)
            {
                foreach (Match match in ForbiddenPredicate(field).Matches(text))
                {
                    var line = text.Take(match.Index).Count(c => c == '\n') + 1;
                    if (!IsQueryPredicate(text, match.Index)) continue;
                    offenders.Add($"{Path.GetFileName(file)}:{line}  {match.Value}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Use `x.Field != true`, never `!x.Field` — an absent JSON field does not satisfy an "
            + "equality in RavenDB, so the negation silently matches nothing:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Proves the detector fires, so a green run means "no offenders" rather than "no detection".
    /// </summary>
    [Theory]
    [InlineData(".Where(c => c.Repository == id && !c.ContributedFromFork)", true)]
    [InlineData(".Where(c => c.ContributedFromFork == false)", true)]
    [InlineData(".Where(c => c.ContributedFromFork != true)", false)]
    [InlineData(".Where(c => c.PullRequestNumber == pr)", false)]
    public void The_detector_matches_the_broken_shapes_and_not_the_correct_one(string snippet, bool shouldMatch)
        => Assert.Equal(shouldMatch, ForbiddenPredicate("ContributedFromFork").IsMatch(snippet));

    /// <summary>
    /// The lambda test, which is what separates a translated predicate from ordinary C#.
    /// </summary>
    [Theory]
    [InlineData(".Where(c => !c.ContributedFromFork)", true)]
    [InlineData("if (!commit.ContributedFromFork)", false)]
    [InlineData("if (!request.ContributedFromFork && commit.ParentSha is null)", false)]
    public void Only_a_lambda_counts_as_a_query_predicate(string snippet, bool isQuery)
    {
        var match = ForbiddenPredicate("ContributedFromFork").Match(snippet);
        Assert.True(match.Success, "the fixture must contain the shape being classified");
        Assert.Equal(isQuery, IsQueryPredicate(snippet, match.Index));
    }

    /// <summary>
    /// Whether the match sits inside a lambda, which is what makes it a RavenDB query predicate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <b>Only an expression tree is unsafe.</b> Negating a late-added bool on an already-loaded
    /// document is ordinary C# and perfectly correct — deserialization really does fill a
    /// non-nullable bool with <c>false</c>. It is only wrong when the predicate is translated to
    /// RQL and evaluated against the index, where the term is simply missing.
    /// </para>
    /// <para>
    /// The distinguishing feature is the lambda: <c>.Where(c =&gt; !c.Field)</c> is translated,
    /// <c>if (!commit.Field)</c> is not. Checking the enclosing LINE for <c>=&gt;</c> is crude, but
    /// it matches how every query in this app is written, and it errs toward reporting — a query
    /// split across lines so that the arrow is not on the match's line would be flagged, which is
    /// the harmless direction.
    /// </para>
    /// <para>
    /// This started as an allow-list of receiver names and was wrong within the hour: it flagged a
    /// genuine document test in <c>DeletePullRequestBuildsRecipient</c> because the local happened
    /// to be called <c>commit</c>. Naming locals is not a security boundary.
    /// </para>
    /// </remarks>
    private static bool IsQueryPredicate(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var lineEnd = text.IndexOf('\n', index);
        if (lineEnd < 0) lineEnd = text.Length;

        return text[lineStart..lineEnd].Contains("=>", StringComparison.Ordinal);
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = FindRepositoryRoot();
        var app = Path.Combine(root, "apps", "CodeCoverage");
        Assert.True(Directory.Exists(app), $"Expected the app at {app}");

        return Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            // The test project itself may spell a broken shape out as a fixture.
            .Where(f => !f.Contains("CodeCoverage.Tests", StringComparison.Ordinal));
    }

    /// <summary>Walks up from the test assembly until the solution file appears.</summary>
    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "apps")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// The sweep is only meaningful if it actually reads files, so assert it walks a real corpus.
    /// A path typo would otherwise make every run vacuously green.
    /// </summary>
    [Fact]
    public void The_sweep_reads_a_substantial_number_of_files()
        => Assert.True(SourceFiles().Count() > 100, "Expected the sweep to walk the whole app.");
}
