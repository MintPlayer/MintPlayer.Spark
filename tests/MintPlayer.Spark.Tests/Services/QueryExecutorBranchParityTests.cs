using System.Reflection;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Tests.Services;

/// <summary>
/// The two source branches of <c>ExecuteQueryAsync</c> must be offered the same request refinements.
/// </summary>
/// <remarks>
/// This exists because of the shape of the #431 sub-query bug, which is worth stating plainly: every
/// refinement — row security, search, sorting, <c>restrictToIds</c> — was wired into both
/// <c>ExecuteDatabaseQueryAsync</c> and <c>ExecuteCustomQueryAsync</c>, and column filters were wired
/// into only one. There was no logic to review and no branch to notice: the parameter simply did not
/// exist on the custom method, so the value sat in a local at the call site and was never passed.
/// <para>
/// Every test in the suite stayed green, because filters were only ever exercised over
/// <c>Database.*</c> and sub-queries only ever over <c>Custom.*</c> without filters. And a declared
/// sub-query <em>must</em> be <c>Custom.*</c>, so the feature covered exactly the complement of where
/// it was asked for — on every sub-query in every app, for a full release.
/// </para>
/// <para>
/// A behavioural test cannot catch the next one, because the failure is an absence: you would have to
/// already suspect the refinement to write the test that misses it. This asserts the signatures
/// instead, which is the level the mistake actually lives at.
/// </para>
/// </remarks>
public class QueryExecutorBranchParityTests
{
    /// <summary>
    /// Request refinements: things a caller sends that must narrow, reorder or restrict the rows.
    /// </summary>
    /// <remarks>
    /// Deliberately not "every parameter". The two branches legitimately differ — the custom branch
    /// takes <c>methodName</c> and the raw <c>search</c> string, the database branch takes neither —
    /// and demanding identical signatures would be noise that gets suppressed rather than a guard that
    /// gets heeded. These are the ones where serving a caller's request on one branch and silently
    /// dropping it on the other is a bug by definition.
    /// </remarks>
    private static readonly string[] RequestRefinements =
    [
        "searchTerm",
        "restrictToIds",
        "columnFilters",
    ];

    [Theory]
    [InlineData("searchTerm")]
    [InlineData("restrictToIds")]
    [InlineData("columnFilters")]
    public void Both_source_branches_accept_the_same_request_refinements(string parameterName)
    {
        var database = Branch("ExecuteDatabaseQueryAsync");
        var custom = Branch("ExecuteCustomQueryAsync");

        var onDatabase = database.GetParameters().Any(p => p.Name == parameterName);
        var onCustom = custom.GetParameters().Any(p => p.Name == parameterName);

        onCustom.Should().Be(onDatabase,
            $"'{parameterName}' is a request refinement, so a query must not honour it on one source "
            + "and ignore it on the other. A parameter present on one signature and absent from the "
            + "other is how #431's column filters reached production working on no sub-query at all — "
            + "there was nothing to review, because there was no code, only a missing argument.");
    }

    [Fact]
    public void The_refinement_list_still_matches_the_database_branch()
    {
        // Without this, the theory above rots into a tautology: drop a refinement from BOTH branches
        // and every case still passes, because absent == absent. The database branch is the one that
        // has always been complete, so it is the reference.
        var database = Branch("ExecuteDatabaseQueryAsync").GetParameters().Select(p => p.Name).ToArray();

        foreach (var refinement in RequestRefinements)
            database.Should().Contain(refinement,
                $"'{refinement}' is listed as a request refinement but the database branch no longer "
                + "takes it — either it was renamed and this list is stale, or the guard is now "
                + "asserting that two branches agree about nothing");
    }

    private static MethodInfo Branch(string name)
        => typeof(QueryExecutor).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new InvalidOperationException(
               $"QueryExecutor.{name} was not found. If the branch was renamed, update this guard — "
               + "do not delete it: it is the only thing standing between a new refinement and the "
               + "exact omission that shipped #431 broken on every sub-query.");
}
