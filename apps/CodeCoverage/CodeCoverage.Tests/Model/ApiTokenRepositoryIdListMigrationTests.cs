using CodeCoverage.Entities;
using CodeCoverage.Migrations;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// <c>M_202609092100_ApiTokenRepositoryIdBecomesAList</c> rewrites only tokens that carry the legacy
/// <c>RepositoryGitHubId</c>.
/// </summary>
/// <remarks>
/// Its <c>where t.RepositoryGitHubId != null</c> also matches a token WITHOUT the field, and every
/// such token became Repository-scoped for <c>"Repositories/undefined"</c> — an Account token that
/// then authorized no upload at all. Found through <c>UploadActionDogfoodTests</c>, whose seeded
/// Account token came out of a fresh database's migrations refusing every upload.
/// </remarks>
public class ApiTokenRepositoryIdListMigrationTests : CoverageRavenTest
{
    [Fact]
    public async Task A_token_without_the_legacy_field_is_left_alone()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new ApiToken { Scope = "Account", Hash = "h", Account = "Accounts/github/1" }, "ApiTokens/modern");
            await seed.SaveChangesAsync();
        }

        await new M_202609092100_ApiTokenRepositoryIdBecomesAList(store).UpAsync(CancellationToken.None);

        using var session = store.OpenAsyncSession();
        var raw = await session.LoadAsync<Dictionary<string, object?>>("ApiTokens/modern");
        (raw["Scope"]?.ToString()).Should().Be("Account");
        raw.Should().NotContainKey("GithubRepositories");
    }

    [Fact]
    public async Task A_token_with_the_legacy_field_becomes_repository_scoped()
    {
        using var store = GetDocumentStore();
        using (var seed = store.OpenAsyncSession())
        {
            await seed.StoreAsync(new ApiToken { Scope = "Account", Hash = "h" }, "ApiTokens/legacy");
            await seed.SaveChangesAsync();
        }
        var patch = await store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
        {
            Query = "from ApiTokens as d where id(d) = 'ApiTokens/legacy' update { d.RepositoryGitHubId = 42; }",
        }));
        await patch.WaitForCompletionAsync(TimeSpan.FromSeconds(30));

        await new M_202609092100_ApiTokenRepositoryIdBecomesAList(store).UpAsync(CancellationToken.None);

        using var session = store.OpenAsyncSession();
        var raw = await session.LoadAsync<Dictionary<string, object?>>("ApiTokens/legacy");
        (raw["Scope"]?.ToString()).Should().Be("Repository");
        (raw["GithubRepositories"]?.ToString()).Should().Contain("Repositories/42");
        raw.Should().NotContainKey("RepositoryGitHubId");
    }
}
