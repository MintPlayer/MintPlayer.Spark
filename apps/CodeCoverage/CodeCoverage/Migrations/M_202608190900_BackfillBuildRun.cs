using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Backfills <c>Build.Run</c>, which became a stored field when the Builds index
/// became generated.
///
/// It was a get-only <c>$"{CiRunId}.{CiRunAttempt}"</c>, so it has never been in
/// the document. A generated index maps <c>build.Run</c> straight through and that
/// map runs server-side against the JSON, where the property does not exist — so
/// without this every build already in the database indexes an empty Run, and the
/// generic grid projects through the index, which would blank the column for all
/// history while new builds looked fine.
///
/// Derived from two fields fixed at creation, so the value is reproducible and the
/// patch is idempotent — it recomputes the same string on a replay.
/// </summary>
public partial class M_202608190900_BackfillBuildRun : ISparkMigration
{
    public static long Version => 202608190900;
    public static string? Description => "Backfill Build.Run, now stored rather than computed";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(new IndexQuery
            {
                // Mirrors Build.ComposeRun. CI run ids are ~11 digits, well inside
                // the exact-integer range, so string concatenation is faithful.
                Query = "from Builds update { this.Run = this.CiRunId + '.' + this.CiRunAttempt; }",
            }),
            token: cancellationToken);

        // Wait, so a throw here aborts startup and the migration is retried on the
        // next start rather than being marked done half-applied.
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
