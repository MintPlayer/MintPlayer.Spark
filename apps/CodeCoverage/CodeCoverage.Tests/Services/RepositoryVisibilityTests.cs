using CodeCoverage.Forge;
using CodeCoverage.Entities;
using CodeCoverage.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Operations;
using Xunit;

namespace CodeCoverage.Tests.Services;

/// <summary>
/// The two visibility rules, and the fact that they deliberately disagree.
/// <para>
/// Collapsing them back into one is the tempting simplification and would be a bug either way:
/// routing the by-name lookups through the listing rule 404s every badge already embedded in a
/// README the moment a repository is transferred, and routing the listings through the resolution
/// rule restores the original complaint — an account page advertising repositories the app lost
/// access to. So both directions are asserted, not just the new one.
/// </para>
/// </summary>
public class RepositoryVisibilityTests : CoverageRavenTest
{
    private static Repository Repo(long id, string owner, string name, bool isPrivate = false,
        RepositoryConnection connection = RepositoryConnection.Connected,
        EForgeProvider provider = EForgeProvider.GitHub)
        => new()
        {
            GitHubId = id,
            Name = name,
            FullName = $"{owner}/{name}",
            OwnerLogin = owner,
            Provider = provider,
            IsPrivate = isPrivate,
            Connection = connection,
        };

    /// <summary>
    /// The allowed set both rules take: owner KEYS, never bare logins.
    /// </summary>
    /// <remarks>
    /// ⚠️ These tests passed bare logins until 2026-09-21, which is how
    /// <see cref="RepositoryVisibility.IsListed"/> came to compare <c>OwnerLogin</c> — the test
    /// agreed with the bug, so the multi-forge merge moved its query twin to <c>OwnerKey</c> and
    /// left this one behind with nothing to notice. Spelling the key out here is deliberate: a
    /// helper that silently qualified a login would hide exactly the mistake that happened.
    /// </remarks>
    private static string[] Owners(params string[] logins)
        => [.. logins.Select(l => new ForgeOwner(EForgeProvider.GitHub, l).ToString())];

    [Fact]
    public void A_disconnected_public_repository_is_still_visible_but_no_longer_listed()
    {
        var repository = Repo(1, "acme", "widgets", connection: RepositoryConnection.Disconnected);

        Assert.True(RepositoryVisibility.IsVisible(repository, []));
        Assert.False(RepositoryVisibility.IsListed(repository, []));
    }

    [Fact]
    public void Its_owner_still_sees_it_listed()
    {
        var repository = Repo(1, "acme", "widgets", connection: RepositoryConnection.Disconnected);

        Assert.True(RepositoryVisibility.IsListed(repository, Owners("acme")));
    }

    [Fact]
    public void A_connected_public_repository_is_listed_for_everyone()
    {
        Assert.True(RepositoryVisibility.IsListed(Repo(1, "acme", "widgets"), []));
    }

    [Fact]
    public void A_private_repository_is_never_listed_to_a_stranger()
    {
        Assert.False(RepositoryVisibility.IsListed(Repo(1, "acme", "secret", isPrivate: true), []));
        Assert.True(RepositoryVisibility.IsListed(Repo(1, "acme", "secret", isPrivate: true), Owners("acme")));
    }

    /// <summary>
    /// The reason both rules compare a KEY and not a login: a login is unique only per forge.
    /// </summary>
    /// <remarks>
    /// ⚠️ <c>acme</c> on GitLab and <c>acme</c> on GitHub are unrelated principals that produce
    /// the same string. Compared by login, whoever holds one would list the other's private
    /// repositories — and the failure grants access, so nothing about it is visible. Both rules are
    /// asserted here because they are separately written and have already drifted apart once.
    /// </remarks>
    [Fact]
    public void The_same_login_on_another_forge_is_a_different_principal()
    {
        var gitlab = Repo(1, "acme", "secret", isPrivate: true, provider: EForgeProvider.GitLab);
        var github = Repo(1, "acme", "secret", isPrivate: true);

        // Holding the GitHub owner grants nothing on GitLab, and vice versa.
        Assert.False(RepositoryVisibility.IsListed(gitlab, Owners("acme")));
        Assert.False(RepositoryVisibility.IsVisible(gitlab, Owners("acme")));

        Assert.True(RepositoryVisibility.IsListed(github, Owners("acme")));
        Assert.True(RepositoryVisibility.IsVisible(github, Owners("acme")));
    }

    /// <summary>
    /// The trap this rule was written around. Every repository document predates the Connection
    /// field, so the property is simply absent from the stored JSON — and an absent field does not
    /// satisfy an equality in RavenDB. Written as <c>== Connected</c>, this filter would have hidden
    /// every existing repository from every anonymous visitor the moment it deployed.
    /// </summary>
    [Fact]
    public async Task A_document_stored_without_the_Connection_field_is_still_listed()
    {
        using var store = GetDocumentStore();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(Repo(1, "acme", "widgets"), Repository.DocumentId(EForgeProvider.GitHub, 1));
            await session.SaveChangesAsync();
        }

        // Actually remove the property, rather than setting it to null: "absent" and "null" are
        // different things to RavenDB, and "absent" is what a pre-existing document has.
        store.Operations.Send(new PatchOperation(
            Repository.DocumentId(EForgeProvider.GitHub, 1),
            changeVector: null,
            new PatchRequest { Script = "delete this.Connection;" }));

        WaitForIndexing(store);

        using (var session = store.OpenAsyncSession())
        {
            var listed = await session.Query<Repository, CodeCoverage.Indexes.Repositories_Overview>()
                .Where(RepositoryVisibility.ListingFilter([]))
                .ToListAsync();

            Assert.Single(listed);
        }
    }
}
