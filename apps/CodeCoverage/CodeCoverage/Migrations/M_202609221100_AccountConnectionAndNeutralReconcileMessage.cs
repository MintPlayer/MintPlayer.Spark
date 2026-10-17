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

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ILogger<M_202609221100_AccountConnectionAndNeutralReconcileMessage> logger;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        // ⚠️ `d.Connection === undefined` rather than a falsy test: the field is an enum serialised
        // as a string, and re-running this must not re-decide an account somebody has since
        // reconnected. Only documents that have never carried the field are touched.
        var backfilled = await BulkOperation.RunAsync(store, new PatchByQueryOperation(new IndexQuery
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
        },
        // Wait for the index rather than throw "Index is stale": a bulk operation on a stale index is refused outright.
        new QueryOperationOptions { StaleTimeout = TimeSpan.FromMinutes(5) }), cancellationToken);
        logger.LogInformation("Account.Connection backfilled: {Count} accounts scanned.", backfilled.Total);

        // The type name is assembly-qualified in SparkMessage.MessageType, so match on the simple
        // name rather than reconstructing it — a message written by an older assembly version
        // carries that version's string and would not match an exact comparison.
        //
        // ⚠️ A scan of the collection, not `where startsWith(d.MessageType, ...)`. A `where` on a field
        // makes RavenDB build an auto index (Auto/SparkMessages/ByMessageType) on every fresh database,
        // and indexes belong to the application (D-O22). That auto index also crash-looped a deploy
        // once: stale by definition when new, and RavenDB refuses a bulk delete on a stale index. The
        // scan reads two fields per message and needs no index, so nothing can be stale.
        var dropped = await DropLegacyReconcileMessagesAsync(cancellationToken);
        if (dropped > 0)
        {
            // ⚠️ Warning, because the queue was supposed to be empty. Finding one here means the
            // assumption the rename rested on was wrong, and it is worth knowing how wrong.
            logger.LogWarning(
                "Dropped {Count} queued ReconcileAccountMessage(s) written before the payload became "
                + "forge-neutral. The nightly sweep re-derives whatever they would have done.",
                dropped);
        }
    }

    private const string LegacyReconcileMessageType = "CodeCoverage.Ingestion.ReconcileAccountMessage";

    private sealed class MessageTypeRow
    {
        public string? Id { get; set; }
        public string? MessageType { get; set; }
    }

    private async Task<int> DropLegacyReconcileMessagesAsync(CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        using (var scan = store.OpenAsyncSession())
        {
            var query = scan.Advanced.AsyncRawQuery<MessageTypeRow>(
                "from SparkMessages as d select id(d) as Id, d.MessageType as MessageType");
            await using var stream = await scan.Advanced.StreamAsync(query, cancellationToken);
            while (await stream.MoveNextAsync())
            {
                var row = stream.Current.Document;
                if (row.Id is not null && row.MessageType?.StartsWith(LegacyReconcileMessageType, StringComparison.Ordinal) == true)
                    ids.Add(row.Id);
            }
        }

        if (ids.Count == 0)
            return 0;

        using var session = store.OpenAsyncSession();
        foreach (var id in ids)
            session.Delete(id);
        await session.SaveChangesAsync(cancellationToken);
        return ids.Count;
    }
}
