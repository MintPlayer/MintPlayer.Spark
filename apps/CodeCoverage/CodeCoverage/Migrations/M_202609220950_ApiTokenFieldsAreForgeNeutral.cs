using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Renames the two GitHub-shaped fields on <c>ApiToken</c> and records which forge each token
/// authorizes against.
/// </summary>
/// <remarks>
/// <para>
/// <c>AccountGitHubId</c> → <c>AccountId</c>, <c>GithubRepositories</c> → <c>RepositoryIds</c>, plus
/// a new <c>Provider</c>. Neither value changes — only the names, and the fact that the forge is now
/// written down instead of assumed.
/// </para>
/// <para>
/// ⚠️ <b>The provider is the substantive part, not the renames.</b> A numeric account id is unique
/// only <em>within</em> a forge, so a token carrying an id and no forge is ambiguous — and the
/// upload path resolved that ambiguity with a literal <c>EForgeProvider.GitHub</c>. That was correct
/// while GitHub was the only forge and silently wrong the moment it was not: a GitLab group and a
/// GitHub user with the same numeric id would have authorized each other's uploads.
/// </para>
/// <para>
/// Every existing token is GitHub's, because GitHub is the only forge that has ever issued one — so
/// the backfill is a constant rather than a lookup, and cannot be wrong.
/// </para>
/// <para>
/// ⚠️ <b>Why a rename is safe here when the same move on a queued message was not.</b> An
/// <c>ApiToken</c> is a document this migration can see and rewrite; a queued message is a payload
/// already written, which nothing re-reads before it is deserialized. Json.NET ignoring an unknown
/// member turns that into a silent zero — so that rename needed an empty queue, and this one needs
/// only this patch.
/// </para>
/// <para>
/// Idempotent: each rename is guarded on the old name still being present, so a re-run is a no-op
/// and a partially-applied run completes.
/// </para>
/// <para>
/// ⚠️ <b>Runs BEFORE the <c>AccountId</c> backfill, and the order is a correctness property.</b>
/// The entity no longer declares <c>AccountGitHubId</c>, so until this has run every legacy token
/// deserializes <c>AccountId</c> as <b>null</b>. A backfill running first would therefore find its
/// "already stamped" guard dead on every production document, re-derive each id <em>from the
/// login</em> — the resolution its own remarks forbid — and, because the RavenDB client re-serializes
/// the entity on save, <b>delete the authoritative <c>AccountGitHubId</c> it was about to need</b>.
/// Where a forge has since reassigned a login, that writes a different account's id over the real
/// one, unrecoverably.
/// </para>
/// </remarks>
public partial class M_202609220950_ApiTokenFieldsAreForgeNeutral : ISparkMigration
{
    public static long Version => 202609220950;
    public static string? Description => "ApiToken fields carry a forge instead of naming one";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ILogger<M_202609220950_ApiTokenFieldsAreForgeNeutral> logger;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
        {
            Query = """
                from ApiTokens as d update {
                    if (d.AccountGitHubId !== undefined) {
                        d.AccountId = d.AccountGitHubId;
                        delete d.AccountGitHubId;
                    }
                    if (d.GithubRepositories !== undefined) {
                        d.RepositoryIds = d.GithubRepositories;
                        delete d.GithubRepositories;
                    }
                    if (d.Provider === undefined || d.Provider === null) {
                        d.Provider = 'GitHub';
                    }
                }
                """,
        }), token: cancellationToken);

        var result = await operation.WaitForCompletionAsync<BulkOperationResult>();

        // Scanned, not renamed — the guards no-op documents already converted, so a re-run reports
        // the collection size. Logged because ZERO is the informative case: it means the collection
        // is empty or the name is wrong.
        logger.LogInformation("ApiToken fields made forge-neutral: {Count} tokens scanned.", result.Total);
    }
}
