using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Moves upload tokens off <c>ApiTokens/{sha256}</c> ids and onto meaningless ones, copying the
/// hash into a field.
///
/// The hash was the document id because that made lookup a point-load and uniqueness free. It
/// stopped being viable when <c>ApiToken</c> became a Spark persistent object: a PersistentObject
/// always carries its id on the wire, so every row a caller could read shipped the token's hash to
/// the browser. The hash now lives in an <c>[IgnoreProperty]</c> field, which the model never
/// declares and Spark therefore never projects.
///
/// Rewriting is the only option — a document id cannot be changed in place — so this writes a new
/// document and deletes the old one.
/// </summary>
/// <remarks>
/// ⚠️ <b>RQL strings, never typed references.</b> A migration is a historical fact about a database;
/// an entity class keeps changing. Naming <c>ApiToken</c> or its properties as symbols would let a
/// later rename break a migration that has already run — or worse, still compile and quietly mean
/// something else.
/// <para>
/// Idempotent through <c>Hash == null</c>: a migrated document has the field, so a replay matches
/// nothing. That predicate is also what makes it safe for documents written after the deploy, which
/// already carry a guid id and a hash.
/// </para>
/// <para>
/// Measured against production 2026-09-09: two documents, one live and one already revoked.
/// </para>
/// </remarks>
public partial class M_202609091300_ApiTokenIdIsNoLongerTheHash : ISparkMigration
{
    public static long Version => 202609091300;
    public static string? Description => "Give upload tokens ids that do not contain their hash";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery
            {
                // ⚠️ The already-migrated check is INSIDE the script, not a where clause.
                // PatchByQueryOperation does not wait for non-stale results, so filtering on a field
                // would run against an index that may not have caught up -- and a stale index
                // matches nothing, which looks exactly like "there was nothing to migrate".
                // A collection scan with no where clause needs no index at all.
                //
                // `put` with a trailing slash lets the server assign the id — the point is only that
                // it carries no information. Every field is copied explicitly rather than spreading
                // the source, so a property added later is a deliberate edit here rather than a
                // silent passenger.
                Query = """
                    from ApiTokens as t
                    update {
                        if (t.Hash) { return; }
                        put('ApiTokens/', {
                            Scope: t.Scope,
                            AccountLogin: t.AccountLogin,
                            AccountGitHubId: t.AccountGitHubId,
                            RepositoryGitHubId: t.RepositoryGitHubId,
                            Description: t.Description,
                            CreatedByUserId: t.CreatedByUserId,
                            CreatedAtUtc: t.CreatedAtUtc,
                            RevokedAtUtc: t.RevokedAtUtc,
                            Hash: id(t).substring('ApiTokens/'.length),
                            '@metadata': { '@collection': 'ApiTokens' }
                        });
                        del(id(t));
                    }
                    """,
            }),
            token: cancellationToken);

        // Wait, so a throw here aborts startup and the migration is retried on the next start
        // rather than being marked done half-applied.
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
