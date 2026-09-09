using CodeCoverage.Actions;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using System.Linq.Expressions;
using Xunit;

namespace CodeCoverage.Tests.ApiTokens;

/// <summary>
/// <see cref="ApiTokenActions.GetRowFilterAsync"/> must return a predicate RavenDB can translate,
/// because Spark pushes it into the database query.
/// </summary>
/// <remarks>
/// The gap this closes is not "ApiToken had a bug" but "nothing ever translated an ApiToken row
/// filter". Every other ApiToken test reads the model JSON or exercises the save path — and the save
/// path <em>compiles</em> the filter and runs it in memory, where the untranslatable form is
/// perfectly valid. So the suite stayed green while both ApiToken query surfaces returned 500 in
/// production:
/// <code>
/// NotSupportedException: Could not understand expression: from 'ApiTokens'
///   .Where(token => ... op_Implicit(Convert(owners, String[])).Contains(token.AccountLogin))
///   ---> Expression type not supported: System.Linq.Expressions.TypedParameterExpression
/// </code>
/// <para>
/// ⚠️ These tests call the <b>real</b> <c>GetRowFilterAsync</c> rather than restating its
/// expression. A copy of the fixed predicate would pass whatever the actions class did, which is
/// the same way the original defect stayed invisible — the property under test is that
/// <em>this method's</em> output translates, not that some correct expression exists.
/// </para>
/// <para>
/// <c>RepositoryVisibility</c>, <c>GitHubProjectVisibility</c> and <c>CommitActions</c> each state
/// in a comment that <c>In()</c> rather than <c>Contains</c> is load-bearing. A comment is not a
/// test, and the one actions class written after those comments ignored all three.
/// </para>
/// </remarks>
public class ApiTokenRowFilterIsTranslatableTests : CoverageRavenTest
{
    /// <summary>
    /// Builds the real actions class with substituted dependencies, resolving the generated
    /// constructor by parameter type so that adding an <c>[Inject]</c> field does not break this.
    /// </summary>
    private static async Task<Expression<Func<ApiToken, bool>>> RowFilterAsync(string[] owners, string action = "Query")
    {
        var visibility = Substitute.For<ISparkVisibility>();
        visibility.GetAllowedOwnersAsync().Returns(Task.FromResult(owners));

        var ctor = typeof(ApiTokenActions).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        // Only the visibility service participates in the row filter. Everything else is supplied
        // as a substitute where NSubstitute can make one, and as null where it cannot — a concrete
        // class with no parameterless constructor (UserManager<SparkUser>) cannot be proxied, and
        // passing null is honest: if the filter ever starts using one of these, the test fails with
        // a NullReferenceException rather than quietly passing against a stub.
        var args = ctor.GetParameters()
            .Select(p =>
            {
                if (p.ParameterType == typeof(ISparkVisibility)) return visibility;
                try { return Substitute.For([p.ParameterType], null); }
                catch { return null; }
            })
            .ToArray();

        var actions = (ApiTokenActions)ctor.Invoke(args);

        var filter = await actions.GetRowFilterAsync(action);
        filter.Should().NotBeNull("an empty owner set must still produce a filter that matches nothing");
        return filter!;
    }

    private static async Task SeedAsync(IDocumentStore store, params (string? login, string description)[] tokens)
    {
        using var session = store.OpenAsyncSession();
        foreach (var (login, description) in tokens)
        {
            await session.StoreAsync(new ApiToken
            {
                Scope = "Account",
                AccountLogin = login,
                Description = description,
                CreatedAtUtc = DateTime.UtcNow,
            });
        }
        await session.SaveChangesAsync();
    }

    /// <summary>
    /// The regression. Before the fix this threw <c>NotSupportedException</c> during translation —
    /// the query never got as far as having rows to assert on.
    /// </summary>
    [Fact]
    public async Task The_row_filter_translates_and_scopes_the_query()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, ("alice", "a"), ("bob", "b"));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var rows = await session.Query<ApiToken>()
            .Where(await RowFilterAsync(["alice"]))
            .ToListAsync();

        rows.Select(t => t.AccountLogin).Should().Equal("alice");
    }

    /// <summary>
    /// The filter composed on top of the custom query's own predicate and its declared sort — the
    /// exact shape of the request that failed in production.
    /// </summary>
    [Fact]
    public async Task The_filter_composes_with_the_custom_query_and_its_sort()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, ("alice", "a"), ("bob", "b"));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var rows = await session.Query<ApiToken>()
            .Where(t => t.AccountLogin == "alice")        // Account_UploadTokens' own predicate
            .Where(await RowFilterAsync(["alice", "bob"])) // the row filter Spark composes on top
            .OrderByDescending(t => t.CreatedAtUtc)       // the query's declared sort
            .ToListAsync();

        rows.Select(t => t.AccountLogin).Should().Equal("alice");
    }

    /// <summary>
    /// "Signed in, manages nothing" must match nothing rather than throw. An empty array is its own
    /// translation hazard, so it is asserted rather than assumed.
    /// </summary>
    [Fact]
    public async Task An_empty_owner_set_matches_nothing()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, ("alice", "a"));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var rows = await session.Query<ApiToken>().Where(await RowFilterAsync([])).ToListAsync();

        rows.Should().BeEmpty();
    }

    /// <summary>
    /// A token with no owner is never visible. Pinned because the pre-fix filter carried an explicit
    /// <c>AccountLogin != null</c> guard: dropping it should be a decision the tests hold, not an
    /// incidental detail of the rewrite.
    /// </summary>
    [Fact]
    public async Task A_token_with_no_owner_is_never_matched()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, (null, "orphan"));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var rows = await session.Query<ApiToken>().Where(await RowFilterAsync(["alice"])).ToListAsync();

        rows.Should().BeEmpty();
    }

    /// <summary>
    /// The filter is returned for every action, not only Query — the type has no per-action
    /// divergence, and a save path that silently got no filter would be a hole rather than a
    /// convenience.
    /// </summary>
    [Theory]
    [InlineData("Query")]
    [InlineData("Read")]
    [InlineData("Edit")]
    public async Task Every_action_gets_a_translatable_filter(string action)
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, ("alice", "a"), ("bob", "b"));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var rows = await session.Query<ApiToken>()
            .Where(await RowFilterAsync(["bob"], action))
            .ToListAsync();

        rows.Select(t => t.AccountLogin).Should().Equal("bob");
    }
}
