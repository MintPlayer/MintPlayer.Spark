using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace CodeCoverage.Migrations;

/// <summary>
/// Renames the three stored <c>DisconnectedReason</c> values that named GitHub.
/// </summary>
/// <remarks>
/// <para>
/// <c>AppUninstalled</c> → <c>IntegrationRemoved</c>, <c>AppSuspended</c> →
/// <c>IntegrationSuspended</c>, <c>DeletedOnGitHub</c> → <c>DeletedOnForge</c>. Nothing else
/// changes: the connection state itself, the timestamps and every other reason are untouched.
/// </para>
/// <para>
/// ⚠️ <b>This is a migration precisely because a rename would not have been enough.</b> These are
/// not enum members — they are <c>const string</c>s whose values are written into documents, so
/// renaming the constants alone would leave every stored document carrying the old word while the
/// code compared the new one. Nothing would fail: <c>DisconnectedReason</c> is only ever displayed
/// and never branched on, so the page would simply keep showing "AppUninstalled" for ever, and the
/// rename would look complete.
/// </para>
/// <para>
/// <b>Why rename at all.</b> "App" is GitHub's word for its integration; GitLab and Bitbucket have
/// nothing called an App. A reason string is shown to an <em>owner</em>, so on a second forge the
/// word would name nothing they could act on — and the advice it implies ("reinstall the App") would
/// be wrong rather than merely vague.
/// </para>
/// <para>
/// Three collections carry the field, all implementing <c>IForgeConnectable</c>: <c>Repositories</c>,
/// <c>Accounts</c> and <c>GitHubProjects</c>.
/// </para>
/// <para>
/// ⚠️ Idempotent by construction: each statement matches the <em>old</em> value, so a re-run finds
/// nothing to change. It deliberately does not touch a document whose reason is already neutral, and
/// it cannot invent one for a connected document, because a connected document has no reason.
/// </para>
/// <para>
/// ⚠️ <c>PatchByQueryOperation</c> over a collection with no <c>where</c>, which is a full scan
/// against an existing collection index rather than a new auto-index — so unlike a
/// <c>DeleteByQueryOperation</c> filtered on a field, this needs no <c>WaitForNonStaleResults</c>.
/// The branch is inside the patch script instead, which is what keeps it off a stale index.
/// </para>
/// </remarks>
public partial class M_202609230900_DisconnectedReasonsStopNamingGitHub : ISparkMigration
{
    public static long Version => 202609230900;
    public static string? Description => "DisconnectedReason values stop naming GitHub";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ILogger<M_202609230900_DisconnectedReasonsStopNamingGitHub> logger;

    /// <summary>Old value → new value, applied to every collection below.</summary>
    private static readonly (string From, string To)[] Renames =
    [
        ("AppUninstalled", "IntegrationRemoved"),
        ("AppSuspended", "IntegrationSuspended"),
        ("DeletedOnGitHub", "DeletedOnForge"),
    ];

    /// <summary>The three collections whose documents implement <c>IForgeConnectable</c>.</summary>
    private static readonly string[] Collections = ["Repositories", "Accounts", "GitHubProjects"];

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        var total = 0L;

        foreach (var collection in Collections)
        {
            // One statement per collection, with every rename inside it — rather than one per
            // (collection, rename) pair — so the whole collection is scanned three times in total
            // instead of nine.
            var script = string.Join(
                "\n                        ",
                Renames.Select(r => $"if (d.DisconnectedReason === '{r.From}') {{ d.DisconnectedReason = '{r.To}'; }}"));

            var operation = await store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
            {
                Query = $$"""
                    from {{collection}} as d update {
                        {{script}}
                    }
                    """,
            }), token: cancellationToken);

            var result = await operation.WaitForCompletionAsync<BulkOperationResult>();
            total += result.Total;

            logger.LogInformation(
                "{Collection}: {Count} documents scanned for GitHub-named disconnection reasons.",
                collection, result.Total);
        }

        logger.LogInformation("DisconnectedReason rename complete across {Count} documents.", total);
    }
}
