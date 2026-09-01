using CodeCoverage.Entities;
using CodeCoverage.Indexes;
using CodeCoverage.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using CodeCoverage.Tests;
using Raven.TestDriver;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// Two anonymous read surfaces serve the same documents — the <c>/api/browse</c>
/// controllers and Spark's generic <c>/spark</c> query API — and each decides
/// visibility its own way: one imperatively on a loaded document, one as a
/// RavenDB row filter. They have to agree forever, and until now a doc-comment
/// was the only thing saying so.
///
/// This pins them together. The next visibility concept (an org allowlist,
/// private-but-shared, an unlisted state) breaks this test unless it lands in
/// both, which is the point.
/// </summary>
public class RepositoryVisibilityParityTests : CoverageRavenTest
{
    private static Repository Repo(long id, string owner, string name, bool isPrivate) => new()
    {
        GitHubId = id,
        Name = name,
        FullName = $"{owner}/{name}",
        OwnerLogin = owner,
        IsPrivate = isPrivate,
    };

    private static readonly Repository[] Corpus =
    [
        Repo(1, "acme", "public-one", isPrivate: false),
        Repo(2, "acme", "secret", isPrivate: true),
        Repo(3, "globex", "public-two", isPrivate: false),
        Repo(4, "globex", "hidden", isPrivate: true),
        Repo(5, "initech", "confidential", isPrivate: true),
    ];

    private async Task<IDocumentStore> SeedCorpus()
    {
        var store = GetDocumentStore();
        using var session = store.OpenAsyncSession();
        foreach (var repository in Corpus)
            await session.StoreAsync(repository, Repository.DocumentId(repository.GitHubId));
        await session.SaveChangesAsync();
        WaitForIndexing(store);
        return store;
    }

    [Theory]
    // Anonymous: the filter must reduce to "public only".
    [InlineData(new string[0], new[] { "acme/public-one", "globex/public-two" })]
    // One granted owner adds exactly that owner's private repositories.
    [InlineData(new[] { "acme" }, new[] { "acme/public-one", "acme/secret", "globex/public-two" })]
    [InlineData(new[] { "acme", "initech" },
        new[] { "acme/public-one", "acme/secret", "globex/public-two", "initech/confidential" })]
    // An owner we know nothing about grants nothing.
    [InlineData(new[] { "nobody" }, new[] { "acme/public-one", "globex/public-two" })]
    public async Task Both_surfaces_resolve_the_same_repositories_for_the_same_principal(
        string[] allowedOwners, string[] expected)
    {
        using var store = await SeedCorpus();
        using var session = store.OpenAsyncSession();

        // The /spark surface: a row filter pushed down to RavenDB.
        var throughRowFilter = await session.Query<Repository, Repositories_Overview>()
            .Where(RepositoryVisibility.Filter(allowedOwners))
            .ToListAsync();

        // The /api/browse surface: the same rule evaluated on a loaded document.
        var throughImperativeCheck = Corpus.Where(r => RepositoryVisibility.IsVisible(r, allowedOwners));

        throughRowFilter.Select(r => r.FullName).Should().BeEquivalentTo(expected);
        throughImperativeCheck.Select(r => r.FullName).Should().BeEquivalentTo(expected);
    }

    /// <summary>
    /// GitHub logins are case-insensitive, and the two surfaces reach the
    /// comparison by different routes — a Raven query term and a .NET string
    /// compare — so this is the most likely place for them to quietly diverge.
    /// </summary>
    [Fact]
    public async Task Both_surfaces_match_an_owner_login_case_insensitively()
    {
        using var store = await SeedCorpus();
        using var session = store.OpenAsyncSession();
        string[] allowed = ["ACME"];

        var throughRowFilter = await session.Query<Repository, Repositories_Overview>()
            .Where(RepositoryVisibility.Filter(allowed))
            .ToListAsync();

        throughRowFilter.Select(r => r.FullName).Should().Contain("acme/secret");
        RepositoryVisibility.IsVisible(Corpus[1], allowed).Should().BeTrue();
    }
}
