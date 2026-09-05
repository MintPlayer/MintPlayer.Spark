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
        RepositoryConnection connection = RepositoryConnection.Connected)
        => new()
        {
            GitHubId = id,
            Name = name,
            FullName = $"{owner}/{name}",
            OwnerLogin = owner,
            IsPrivate = isPrivate,
            Connection = connection,
        };

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

        Assert.True(RepositoryVisibility.IsListed(repository, ["acme"]));
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
        Assert.True(RepositoryVisibility.IsListed(Repo(1, "acme", "secret", isPrivate: true), ["acme"]));
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
            await session.StoreAsync(Repo(1, "acme", "widgets"), Repository.DocumentId(1));
            await session.SaveChangesAsync();
        }

        // Actually remove the property, rather than setting it to null: "absent" and "null" are
        // different things to RavenDB, and "absent" is what a pre-existing document has.
        store.Operations.Send(new PatchOperation(
            Repository.DocumentId(1),
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
