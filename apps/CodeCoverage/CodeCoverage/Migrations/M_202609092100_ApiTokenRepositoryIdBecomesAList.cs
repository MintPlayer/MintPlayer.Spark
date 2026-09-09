using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Moves a repository-scoped token's single <c>RepositoryGitHubId</c> into the
/// <c>GithubRepositories</c> document-id list, so a token can serve several repositories.
/// </summary>
/// <remarks>
/// ⚠️ <b>This one carries the value across rather than dropping it</b> — the opposite of
/// <c>M_202609081600</c>, and for the opposite reason. <c>DeleteBranchOnPrClose</c> was inert when
/// it moved, so deleting it cost nothing; <c>RepositoryGitHubId</c> is read on every upload, so
/// dropping it would revoke working CI credentials.
///
/// <para>
/// The value shape changes as well as the field: a document id (<c>Repositories/{gitHubId}</c>)
/// rather than a bare number, because that is what a Spark reference stores and what the
/// authentication handler now puts on the wire.
/// </para>
///
/// <para>
/// <b>Deploy ordering.</b> Migrations run at startup, by which point the new code is already
/// serving. Between the process starting and this patch completing, a repository-scoped token has
/// an empty list and would be refused. That window is accepted deliberately: the owner confirmed
/// breaking changes are allowed and that production data may be modified directly, so the
/// alternative — teaching the handler to read both shapes for one release — would add a
/// compatibility path with no one to serve.
/// </para>
///
/// <para>
/// Idempotent: the filter self-excludes once the member is gone, so a replay, a restored backup or
/// a fresh environment is a no-op. Account-scoped tokens are untouched — an absent member leaves the
/// C# initializer's empty list in place, which is exactly "covers every repository of the account".
/// </para>
/// </remarks>
public partial class M_202609092100_ApiTokenRepositoryIdBecomesAList : ISparkMigration
{
    public static long Version => 202609092100;

    public static string? Description =>
        "Move ApiToken.RepositoryGitHubId into the GithubRepositories document-id list";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery
            {
                // RQL strings rather than typed references: a migration is a historical fact about
                // the database at a moment in time, and a later rename must not change what an
                // already-applied migration meant.
                Query = """
                    from ApiTokens as t
                    where t.RepositoryGitHubId != null
                    update {
                        t.GithubRepositories = ["Repositories/" + t.RepositoryGitHubId];
                        t.Scope = "Repository";
                        delete this.RepositoryGitHubId;
                    }
                    """,
            }),
            token: cancellationToken);

        // Wait, so a throw here aborts startup and the migration is retried on the next start
        // rather than being marked done half-applied.
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
