using System.Linq.Expressions;
using CodeCoverage.Actions;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using NSubstitute;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Xunit;

namespace CodeCoverage.Tests.Actions;

/// <summary>
/// The row filters that guard the WRITE path on <c>Repository</c> and <c>Account</c>.
/// </summary>
/// <remarks>
/// Until <c>Edit</c> was granted so an owner could set <c>DeleteBranchOnPrClose</c>, neither type
/// had a usable write rule and neither needed one: <c>RepositoryActions</c> answered every
/// non-<c>Query</c> action with the anonymous-tier read filter (<c>!IsPrivate || owner</c>), and
/// <c>AccountActions</c> returned no filter at all — which <c>RowSecurity</c> reads as ALLOWED.
/// <para>
/// Granting the right turned both into live holes: any signed-in user could have edited any PUBLIC
/// repository's settings, and any account whatsoever. These tests exist because the fix is
/// invisible — the filters are the whole access control, and nothing else would have failed if they
/// were wrong. Removing either write branch must turn one of these red.
/// </para>
/// <para>
/// The filters are asserted by EXECUTION against RavenDB, not by inspecting the expression: they are
/// pushed into RQL, so a predicate that reads correctly but does not translate is the exact failure
/// that took both ApiToken query surfaces down in production. Running them proves both properties at
/// once.
/// </para>
/// </remarks>
public class WriteRowFilterTests : CoverageRavenTest
{
    private static ISparkVisibility VisibilityFor(params string[] owners)
    {
        var visibility = Substitute.For<ISparkVisibility>();
        visibility.GetAllowedOwnersAsync().Returns(Task.FromResult(owners));
        visibility.CanManageOwnerAsync(Arg.Any<string>())
            .Returns(call => Task.FromResult(owners.Contains(call.Arg<string>(), StringComparer.OrdinalIgnoreCase)));
        return visibility;
    }

    /// <summary>
    /// Builds the real actions class, resolving the generated constructor by parameter type so an
    /// added <c>[Inject]</c> field does not break this. Only the visibility service participates in
    /// a row filter; anything that cannot be proxied is passed as null, so a filter that starts
    /// using one fails loudly rather than passing against a stub.
    /// </summary>
    private static T CreateActions<T>(ISparkVisibility visibility)
    {
        var ctor = typeof(T).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters()
            .Select(p =>
            {
                if (p.ParameterType == typeof(ISparkVisibility)) return visibility;
                try { return Substitute.For([p.ParameterType], null); }
                catch { return null; }
            })
            .ToArray();
        return (T)ctor.Invoke(args);
    }

    private static async Task SeedRepositoriesAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Repository
        {
            GitHubId = 1, Name = "public-of-acme", FullName = "acme/public-of-acme",
            OwnerLogin = "acme", IsPrivate = false,
        }, Repository.DocumentId(1));
        await session.StoreAsync(new Repository
        {
            GitHubId = 2, Name = "public-of-other", FullName = "other/public-of-other",
            OwnerLogin = "other", IsPrivate = false,
        }, Repository.DocumentId(2));
        await session.StoreAsync(new Repository
        {
            GitHubId = 3, Name = "private-of-other", FullName = "other/private-of-other",
            OwnerLogin = "other", IsPrivate = true,
        }, Repository.DocumentId(3));
        await session.SaveChangesAsync();
    }

    private static async Task<string[]> MatchingRepositoriesAsync(
        IDocumentStore store, Expression<Func<Repository, bool>>? filter)
    {
        using var session = store.OpenAsyncSession();
        var query = session.Query<Repository>();
        if (filter is not null) query = query.Where(filter);
        var rows = await query.ToListAsync();
        return [.. rows.Select(r => r.FullName).OrderBy(n => n, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The regression this whole file exists for. A caller who manages nothing must not be able to
    /// edit a PUBLIC repository — which the read filter admits, and which is what the write path
    /// used to receive.
    /// </summary>
    [Theory]
    [InlineData("Edit")]
    [InlineData("New")]
    [InlineData("Delete")]
    public async Task A_write_on_Repository_is_scoped_to_owners_the_caller_manages(string action)
    {
        using var store = GetDocumentStore();
        await SeedRepositoriesAsync(store);
        var actions = CreateActions<RepositoryActions>(VisibilityFor("acme"));

        var filter = await actions.GetRowFilterAsync(action);

        filter.Should().NotBeNull("a null filter means UNRESTRICTED, which on a write path is the hole");
        (await MatchingRepositoriesAsync(store, filter))
            .Should().Equal("acme/public-of-acme");
    }

    [Fact]
    public async Task A_caller_who_manages_nothing_can_write_to_no_repository()
    {
        using var store = GetDocumentStore();
        await SeedRepositoriesAsync(store);
        var actions = CreateActions<RepositoryActions>(VisibilityFor());

        var filter = await actions.GetRowFilterAsync("Edit");

        (await MatchingRepositoriesAsync(store, filter)).Should().BeEmpty();
    }

    /// <summary>
    /// Reads are deliberately wider than writes — a public repository stays publicly readable, which
    /// is what the badges and shared report links depend on. Pinned so that tightening the write
    /// path cannot quietly tighten the read one too.
    /// </summary>
    [Theory]
    [InlineData("Query")]
    [InlineData("Read")]
    public async Task A_read_on_Repository_still_admits_public_repositories(string action)
    {
        using var store = GetDocumentStore();
        await SeedRepositoriesAsync(store);
        var actions = CreateActions<RepositoryActions>(VisibilityFor("acme"));

        var filter = await actions.GetRowFilterAsync(action);

        (await MatchingRepositoriesAsync(store, filter))
            .Should().Equal("acme/public-of-acme", "other/public-of-other");
    }

    private static async Task SeedAccountsAsync(IDocumentStore store)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Account { GitHubId = 1, Login = "acme" }, Account.DocumentId(1));
        await session.StoreAsync(new Account { GitHubId = 2, Login = "other" }, Account.DocumentId(2));
        await session.SaveChangesAsync();
    }

    private static async Task<string[]> MatchingAccountsAsync(
        IDocumentStore store, Expression<Func<Account, bool>>? filter)
    {
        using var session = store.OpenAsyncSession();
        var query = session.Query<Account>();
        if (filter is not null) query = query.Where(filter);
        var rows = await query.ToListAsync();
        return [.. rows.Select(a => a.Login).OrderBy(n => n, StringComparer.Ordinal)];
    }

    /// <summary>
    /// <c>AccountActions</c> had no <c>GetRowFilterAsync</c> at all, and a missing rule is ALLOWED —
    /// so granting <c>Edit/Account</c> without this would have let any signed-in user edit any
    /// account in the database.
    /// </summary>
    [Theory]
    [InlineData("Edit")]
    [InlineData("New")]
    [InlineData("Delete")]
    public async Task A_write_on_Account_is_scoped_to_owners_the_caller_manages(string action)
    {
        using var store = GetDocumentStore();
        await SeedAccountsAsync(store);
        var actions = CreateActions<AccountActions>(VisibilityFor("acme"));

        var filter = await actions.GetRowFilterAsync(action);

        filter.Should().NotBeNull("null means unrestricted; on a write path that is the defect");
        (await MatchingAccountsAsync(store, filter)).Should().Equal("acme");
    }

    /// <summary>
    /// Accounts stay public to read — a GitHub login and avatar is data GitHub already publishes,
    /// and the public dashboard depends on it. Null here is correct and must not be "fixed".
    /// </summary>
    [Theory]
    [InlineData("Query")]
    [InlineData("Read")]
    public async Task A_read_on_Account_stays_unfiltered(string action)
    {
        using var store = GetDocumentStore();
        await SeedAccountsAsync(store);
        var actions = CreateActions<AccountActions>(VisibilityFor());

        var filter = await actions.GetRowFilterAsync(action);

        filter.Should().BeNull("accounts are public to read, deliberately");
    }
}
