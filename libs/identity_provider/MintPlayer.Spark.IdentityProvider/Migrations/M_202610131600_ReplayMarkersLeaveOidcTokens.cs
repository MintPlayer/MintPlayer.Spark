using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;

namespace MintPlayer.Spark.IdentityProvider.Migrations;

/// <summary>
/// Deletes the replay markers the provider used to keep in <c>OidcTokens</c> (type <c>dpop_jti</c>, ids
/// <c>OidcTokens/replay:…</c>) for DPoP proofs, client assertions and request objects
/// (<c>docs/identity_provider_parity_PRD.md</c> A12).
/// </summary>
/// <remarks>
/// <para>
/// Those values are now spent in the cluster's compare-exchange (<c>SparkOneTimeUse</c>), which nothing reads back
/// from the documents, so a marker protects nothing any more. They carry <c>@expires</c>, but the provider never
/// enables document expiration itself, so on a database without it they would stay forever.
/// </para>
/// <para>
/// <c>PatchByQueryOperation</c> over the whole collection with the type test inside the script, as
/// <c>M_202610090900_OidcDataModel</c> does: a full scan on the collection, not a new auto-index on <c>Type</c>,
/// so there is no stale index to wait for. Idempotent: a second run finds none.
/// </para>
/// </remarks>
public partial class M_202610131600_ReplayMarkersLeaveOidcTokens : ISparkMigration
{
    public static long Version => 202610131600;
    public static string? Description => "Identity provider: delete the replay markers in OidcTokens, now kept in compare-exchange";

    [Inject] private readonly IDocumentStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        // The collection is a constant; the type is a parameter.
        var operation = await store.Operations.SendAsync(
            new PatchByQueryOperation(
                new IndexQuery
                {
                    Query = "from OidcTokens as t update { if (t.Type === $type) { del(id(t)); } }",
                    QueryParameters = new Parameters { ["type"] = "dpop_jti" },
                },
                new QueryOperationOptions { StaleTimeout = TimeSpan.FromMinutes(5) }),
            token: cancellationToken);
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(5));
    }
}
