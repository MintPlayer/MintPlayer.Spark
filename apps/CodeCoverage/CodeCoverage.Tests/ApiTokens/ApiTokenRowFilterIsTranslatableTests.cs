using CodeCoverage.Forge;
using CodeCoverage.Actions;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Queries;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using System.Linq.Expressions;
using Xunit;

namespace CodeCoverage.Tests.ApiTokens;

/// <summary>
/// <see cref="ApiTokenActions.GetRowFilterAsync"/> must return a predicate RavenDB can translate,
/// because Spark pushes it into the database query — and it must show a caller exactly the tokens
/// of the accounts they manage.
/// </summary>
/// <remarks>
/// The gap this closes is not "ApiToken had a bug" but "nothing ever translated an ApiToken row
/// filter". The save path <em>compiles</em> the filter and runs it in memory, where an
/// untranslatable form is perfectly valid. So the suite once stayed green while both ApiToken query
/// surfaces returned 500 in production:
/// <code>
/// NotSupportedException: Could not understand expression: from 'ApiTokens'
///   .Where(token => ... op_Implicit(Convert(owners, String[])).Contains(token.AccountLogin))
///   ---> Expression type not supported: System.Linq.Expressions.TypedParameterExpression
/// </code>
/// <para>
/// ⚠️ These tests call the <b>real</b> <c>GetRowFilterAsync</c>, against a real session, rather than
/// restating its expression. Since 2026-10-02 the filter matches <see cref="ApiToken.Account"/>
/// (a document id) against the ids of the accounts whose owner key the caller manages, which it
/// looks up through <c>Accounts_Overview</c> — so the lookup is under test as well.
/// </para>
/// </remarks>
public class ApiTokenRowFilterIsTranslatableTests : CoverageRavenTest
{
    protected override bool DeployIndexes => true;

    private static readonly string Alice = Account.DocumentId(EForgeProvider.GitHub, 1);
    private static readonly string Bob = Account.DocumentId(EForgeProvider.GitHub, 2);
    /// <summary>GitLab's <c>alice</c>: same login, same number, a different owner.</summary>
    private static readonly string AliceOnGitLab = Account.DocumentId(EForgeProvider.GitLab, 1);

    /// <summary>
    /// Builds the real actions class with substituted dependencies, resolving the generated
    /// constructor by parameter type so that adding an <c>[Inject]</c> field does not break this.
    /// </summary>
    /// <remarks>
    /// <paramref name="owners"/> is given as bare GitHub logins for readability and qualified here
    /// (<c>github:alice</c>), which is what the real visibility service returns.
    /// </remarks>
    private static ApiTokenActions Actions(IAsyncDocumentSession session, string[] owners)
    {
        var visibility = Substitute.For<ISparkVisibility>();
        visibility.GetAllowedOwnersAsync().Returns(Task.FromResult(
            owners.Select(ForgeOwner.KeyFromUnqualifiedLogin).ToArray()));

        var ctor = typeof(ApiTokenActions).GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        // The visibility service and the session participate in the row filter. Everything else is
        // a substitute where NSubstitute can make one, and null where it cannot (UserManager).
        var args = ctor.GetParameters()
            .Select(p =>
            {
                if (p.ParameterType == typeof(ISparkVisibility)) return visibility;
                if (p.ParameterType == typeof(IAsyncDocumentSession)) return session;
                try { return Substitute.For([p.ParameterType], null); }
                catch { return null; }
            })
            .ToArray();

        return (ApiTokenActions)ctor.Invoke(args);
    }

    private static async Task<Expression<Func<ApiToken, bool>>> RowFilterAsync(
        IAsyncDocumentSession session, string[] owners, string action = "Query")
    {
        var filter = await Actions(session, owners).GetRowFilterAsync(action);
        filter.Should().NotBeNull(); // an empty owner set must still produce a filter that matches nothing
        return filter!;
    }

    private static async Task SeedAsync(IDocumentStore store, params (string? account, string description)[] tokens)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Account { GitHubId = 1, Login = "alice" }, Alice);
        await session.StoreAsync(new Account { GitHubId = 2, Login = "bob" }, Bob);
        await session.StoreAsync(new Account { GitHubId = 1, Login = "alice", Provider = EForgeProvider.GitLab }, AliceOnGitLab);
        foreach (var (account, description) in tokens)
        {
            await session.StoreAsync(new ApiToken
            {
                Scope = "Account",
                Account = account,
                Description = description,
                CreatedAtUtc = DateTime.UtcNow,
            }, ApiToken.NewDocumentId());
        }
        await session.SaveChangesAsync();
    }

    /// <summary>
    /// The regression, and the scoping: translated by RavenDB, and only the managed account's token.
    /// </summary>
    [Fact]
    public async Task The_row_filter_translates_and_scopes_the_query()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, (Alice, "a"), (Bob, "b"));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var rows = await session.Query<ApiToken>()
            .Where(await RowFilterAsync(session, ["alice"]))
            .ToListAsync();

        rows.Select(t => t.Description).Should().Equal("a");
    }

    /// <summary>
    /// The filter composed on top of the real <c>Account_UploadTokens</c> custom query (an index
    /// query over <c>ApiTokens_Overview</c>) and its declared sort — the shape Spark executes.
    /// </summary>
    [Fact]
    public async Task The_filter_composes_with_the_custom_query_and_its_sort()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, (Alice, "a1"), (Alice, "a2"), (Bob, "b"));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var parent = new PersistentObject { Id = Alice, Name = nameof(Account), ObjectTypeId = Guid.NewGuid() };
        var args = new CustomQueryArgs
        {
            Query = new SparkQuery { Id = Guid.NewGuid(), Name = "Account_UploadTokens", Source = "Custom.Account_UploadTokens" },
            Parent = parent,
            ParentType = parent.Name,
        };
        var actions = Actions(session, ["alice", "bob"]);

        var rows = await actions.Account_UploadTokens(args)
            .Where((await actions.GetRowFilterAsync("Query"))!) // the row filter Spark composes on top
            .OrderByDescending(t => t.CreatedAtUtc)            // the query's declared sort
            .ToListAsync();

        rows.Select(t => t.Description).Should().BeEquivalentTo(["a1", "a2"]);
    }

    /// <summary>
    /// "Signed in, manages nothing" must match nothing rather than throw. An empty array is its own
    /// translation hazard, so it is asserted rather than assumed.
    /// </summary>
    [Fact]
    public async Task An_empty_owner_set_matches_nothing()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, (Alice, "a"));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var rows = await session.Query<ApiToken>().Where(await RowFilterAsync(session, [])).ToListAsync();

        rows.Should().BeEmpty();
    }

    /// <summary>
    /// A token with no account (one the migration could not resolve) is never visible.
    /// </summary>
    [Fact]
    public async Task A_token_with_no_account_is_never_matched()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, (null, "orphan"));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var rows = await session.Query<ApiToken>().Where(await RowFilterAsync(session, ["alice"])).ToListAsync();

        rows.Should().BeEmpty();
    }

    /// <summary>
    /// ⚠️ Forge-qualified: managing GitHub's <c>alice</c> does not show the tokens of GitLab's
    /// <c>alice</c>, whose document id differs only in the provider segment.
    /// </summary>
    [Fact]
    public async Task A_same_named_account_on_another_forge_is_not_shown()
    {
        using var store = GetDocumentStore();
        await SeedAsync(store, (Alice, "github"), (AliceOnGitLab, "gitlab"));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var rows = await session.Query<ApiToken>().Where(await RowFilterAsync(session, ["alice"])).ToListAsync();

        rows.Select(t => t.Description).Should().Equal("github");
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
        await SeedAsync(store, (Alice, "a"), (Bob, "b"));
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var rows = await session.Query<ApiToken>()
            .Where(await RowFilterAsync(session, ["bob"], action))
            .ToListAsync();

        rows.Select(t => t.Description).Should().Equal("b");
    }
}
