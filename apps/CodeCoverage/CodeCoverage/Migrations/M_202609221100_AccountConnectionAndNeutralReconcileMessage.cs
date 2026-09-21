using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Backfills <c>Account.Connection</c> from the GitHub installation id, and drops any queued
/// <c>ReconcileAccountMessage</c> written before its payload became forge-neutral.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the account needed a neutral connection field.</b> Three callers ask "can we still act on
/// this account", and two of them ask it <em>inside a RavenDB query</em> — the nightly reconcile
/// sweep and the manual resync. An interface method cannot be pushed into RQL, so the only shapes
/// that work are a stored field or every caller reading <c>InstallationId</c> and thereby naming
/// GitHub. This backfills the field so the queries can move.
/// </para>
/// <para>
/// ⚠️ <b>An account with no installation is marked disconnected, not deleted.</b> Its repositories
/// were already disconnected by whatever removed the installation; this only brings the account
/// document into line with what its repositories already say.
/// </para>
/// <para>
/// <b>The message drop.</b> <c>ReconcileAccountMessage.AccountGitHubId</c> became
/// <c>AccountId</c> + <c>Provider</c>, and <c>SparkMessage</c> persists the payload as JSON.
/// Json.NET ignores a member it cannot bind, so a message written with the old name would
/// deserialize with <c>AccountId = 0</c>, load no account and return silently — a no-op that looks
/// like success. The queue was verified empty before the rename, so this deletes stragglers rather
/// than leaving them to fail quietly; a reconcile is re-derivable and the nightly sweep repairs
/// anything lost.
/// </para>
/// </remarks>
public partial class M_202609221100_AccountConnectionAndNeutralReconcileMessage : ISparkMigration
{
    public static long Version => 202609221100;
    public static string? Description => "Account.Connection, and stale reconcile messages";

    /// <summary>How long to let a new auto-index catch up before the delete gives up.</summary>
    /// <remarks>
    /// Settable so a test does not wait ten minutes to prove a timeout. Matches the budget the
    /// forge-qualifying migration settled on after this same hazard took a deploy down.
    /// </remarks>
    internal static TimeSpan IndexCatchUpBudget { get; set; } = TimeSpan.FromMinutes(10);

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ILogger<M_202609221100_AccountConnectionAndNeutralReconcileMessage> logger;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        // ⚠️ `d.Connection === undefined` rather than a falsy test: the field is an enum serialised
        // as a string, and re-running this must not re-decide an account somebody has since
        // reconnected. Only documents that have never carried the field are touched.
        var backfill = await store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
        {
            Query = """
                from Accounts as d update {
                    if (d.Connection === undefined || d.Connection === null) {
                        d.Connection = d.InstallationId ? 'Connected' : 'Disconnected';
                        if (!d.InstallationId) {
                            d.DisconnectedReason = 'IntegrationRemoved';
                            d.DisconnectedAtUtc = new Date().toISOString();
                        }
                    }
                }
                """,
        }), token: cancellationToken);

        var backfilled = await backfill.WaitForCompletionAsync<BulkOperationResult>();
        logger.LogInformation("Account.Connection backfilled: {Count} accounts scanned.", backfilled.Total);

        // The type name is assembly-qualified in SparkMessage.MessageType, so match on the simple
        // name rather than reconstructing it — a message written by an older assembly version
        // carries that version's string and would not match an exact comparison.
        // ⚠️ `WaitForNonStaleResults` is NOT optional, and this exact omission crash-looped a
        // previous deploy. A `where` on a field forces a brand-new auto-index, which is stale by
        // definition until it has covered the collection — and RavenDB REFUSES a bulk delete on a
        // stale index ("Cannot perform bulk operation. Index is stale."), which throws, aborts
        // startup, and retries forever. The indexing pipeline is also saturated at this moment: the
        // migration two versions earlier has just patched ~220,000 FileCoverages, and the Accounts
        // patch above ran three statements ago.
        var drop = await store.Operations.SendAsync(new DeleteByQueryOperation(new IndexQuery
        {
            Query = """
                from SparkMessages as d
                where startsWith(d.MessageType, 'CodeCoverage.Ingestion.ReconcileAccountMessage')
                """,
            WaitForNonStaleResults = true,
            WaitForNonStaleResultsTimeout = IndexCatchUpBudget,
        }), token: cancellationToken);

        var dropped = await drop.WaitForCompletionAsync<BulkOperationResult>();
        if (dropped.Total > 0)
        {
            // ⚠️ Warning, because the queue was supposed to be empty. Finding one here means the
            // assumption the rename rested on was wrong, and it is worth knowing how wrong.
            logger.LogWarning(
                "Dropped {Count} queued ReconcileAccountMessage(s) written before the payload became "
                + "forge-neutral. The nightly sweep re-derives whatever they would have done.",
                dropped.Total);
        }
    }
}
