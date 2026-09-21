using CodeCoverage.Entities;
using CodeCoverage.Forge;
using CodeCoverage.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// Who may see a project board — asserted against the owner-KEY set the real service returns.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Both forms of this rule were wrong, and there was no test at all.</b> `Filter` and
/// `IsVisible` each compared `GitHubProject.OwnerLogin` ("acme") against a set of
/// <c>provider:login</c> keys ("github:acme"), so <b>every board was hidden from everybody</b> on
/// production. The two agreed with each other perfectly, which is what the pair was designed to
/// guarantee — they were simply both wrong, and agreement is not correctness.
/// </para>
/// <para>
/// `GitHubProject` carries no `OwnerKey` and no `Provider`, unlike `Repository` — a board is a
/// GitHub Projects V2 concept and has no second forge to serve. So the fix narrows the key set to
/// the GitHub ones and unwraps them, which also has to survive translation to RQL.
/// </para>
/// <para>
/// ⚠️ Every test here runs BOTH forms on the same input. Testing one alone is what allowed them to
/// drift into agreeing on the wrong answer.
/// </para>
/// </remarks>
public class GitHubProjectVisibilityTests : CoverageRavenTest
{
    private const string Owner = "acme";
    private const string Stranger = "other";

    private static string[] Keys(params string[] logins)
        => [.. logins.Select(l => new ForgeOwner(EForgeProvider.GitHub, l).ToString())];

    private GitHubProject Board(string owner, RepositoryConnection connection = RepositoryConnection.Connected)
        => new()
        {
            Number = 1,
            Name = "Roadmap",
            OwnerLogin = owner,
            Connection = connection,
        };

    /// <summary>Runs the query form against embedded RavenDB, so RQL translation is exercised too.</summary>
    private async Task<bool> MatchesQueryAsync(IDocumentStore store, GitHubProject board, string[] allowedOwnerKeys)
    {
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(board, "GitHubProjects/1-A");
            await seed.SaveChangesAsync();
        }
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        return await session.Query<GitHubProject>()
            .Where(GitHubProjectVisibility.Filter(allowedOwnerKeys))
            .AnyAsync();
    }

    [Fact]
    public async Task An_owner_sees_their_own_board_in_both_forms()
    {
        using var store = GetDocumentStore();
        var board = Board(Owner);
        var allowed = Keys(Owner);

        Assert.True(GitHubProjectVisibility.IsVisible(board, allowed));
        Assert.True(await MatchesQueryAsync(store, board, allowed));
    }

    [Fact]
    public async Task A_stranger_sees_nothing_in_both_forms()
    {
        using var store = GetDocumentStore();
        var board = Board(Owner);
        var allowed = Keys(Stranger);

        Assert.False(GitHubProjectVisibility.IsVisible(board, allowed));
        Assert.False(await MatchesQueryAsync(store, board, allowed));
    }

    [Fact]
    public async Task An_anonymous_viewer_sees_nothing_because_boards_have_no_public_tier()
    {
        using var store = GetDocumentStore();
        var board = Board(Owner);

        Assert.False(GitHubProjectVisibility.IsVisible(board, []));
        Assert.False(await MatchesQueryAsync(store, board, []));
    }

    /// <summary>
    /// ⚠️ The reason the set holds keys at all: the same login on another forge is a different
    /// principal. Narrowing to the GitHub keys must not degenerate into ignoring the provider.
    /// </summary>
    [Fact]
    public async Task The_same_login_on_another_forge_grants_nothing()
    {
        using var store = GetDocumentStore();
        var board = Board(Owner);
        var gitlabOnly = new[] { new ForgeOwner(EForgeProvider.GitLab, Owner).ToString() };

        Assert.False(GitHubProjectVisibility.IsVisible(board, gitlabOnly));
        Assert.False(await MatchesQueryAsync(store, board, gitlabOnly));
    }

    /// <summary>
    /// A disconnected board is not advertised, even to its owner — unlike a repository, which stays
    /// reachable by name because its badge and report URLs are already published elsewhere.
    /// </summary>
    [Fact]
    public async Task A_disconnected_board_is_hidden_even_from_its_owner()
    {
        using var store = GetDocumentStore();
        var board = Board(Owner, RepositoryConnection.Disconnected);
        var allowed = Keys(Owner);

        Assert.False(GitHubProjectVisibility.IsVisible(board, allowed));
        Assert.False(await MatchesQueryAsync(store, board, allowed));
    }

    /// <summary>
    /// ⚠️ The absent-field rule: a board document written before `Connection` existed has no such
    /// property, and an absent field does not satisfy an equality in RavenDB. Written as
    /// <c>== Connected</c> this would hide every pre-existing board; written as
    /// <c>!= Disconnected</c> it reads as connected, which is what it is.
    /// </summary>
    [Fact]
    public async Task A_board_written_before_the_Connection_field_existed_is_still_visible()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(Board(Owner), "GitHubProjects/1-A");
            await seed.SaveChangesAsync();
        }

        var patch = store.Operations.Send(new Raven.Client.Documents.Operations.PatchByQueryOperation(
            "from GitHubProjects update { delete this.Connection; }"));
        var result = patch.WaitForCompletion<Raven.Client.Documents.Operations.BulkOperationResult>(
            TimeSpan.FromSeconds(30));
        Assert.Equal(1, result.Total);
        WaitForIndexing(store);

        using var session = store.OpenAsyncSession();
        var visible = await session.Query<GitHubProject>()
            .Where(GitHubProjectVisibility.Filter(Keys(Owner)))
            .AnyAsync();

        Assert.True(visible);
    }
}
