using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.IdentityProvider.Migrations;

/// <summary>
/// Claims the client-id reservation (<c>oidc/client-ids/{client id}</c>) of every stored application that lacks one, and
/// drops the static index <c>OidcApplications/ByClientId</c> (#536 D47).
/// <para>
/// The identity provider finds an application by its client id through the reservation now: the index was RavenDB's
/// alone, and it answered "which application is this?" eventually. Applications saved before reservations existed
/// (O17) hold none, and would not be found. A key another existing application holds is left to it and logged: that
/// is the duplicate O17 was written to prevent.
/// </para>
/// <para>RavenDB only: an application on any other store was saved with its reservation. A second run claims nothing.</para>
/// </summary>
public partial class M_202611251310_OidcClientIdReservations : ISparkMigration
{
    public static long Version => 202611251310;
    public static string? Description => "Identity provider: claim the client-id reservations of existing applications";

    private const string RetiredIndex = "OidcApplications/ByClientId";
    private const int Chunk = 10;

    [Inject] private readonly IDocumentStore? raven;
    [Inject] private readonly ISparkStore store;
    [Inject] private readonly ILogger<M_202611251310_OidcClientIdReservations>? logger;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        if (raven is null || store.ProviderName != SparkStoreProviders.RavenDb)
            return;

        var apps = new List<(string Id, string ClientId)>();
        using (var scan = store.OpenSession())
        {
            // A scan, no index (D42).
            await foreach (var row in scan.StreamAsync(scan.Query<OidcApplication>(), cancellationToken))
                if (row.Document is { } app && !app.Id.IsEmpty && !string.IsNullOrEmpty(app.ClientId))
                    apps.Add((app.Id.Value, app.ClientId));
        }

        // The holder of each key this run has settled. Two applications of one client id (saved before O17) both reach
        // here: the first claims it, and the second must not claim it in the same save, which would fail the whole save.
        var settled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // A few requests per application (read the holder, check it exists): small sessions stay under RavenDB's 30.
        foreach (var chunk in apps.Chunk(Chunk))
        {
            using var session = store.OpenSession();
            foreach (var (id, clientId) in chunk)
            {
                var key = OidcClientIdReservation.Key(clientId);
                if (!settled.TryGetValue(key, out var holder))
                {
                    holder = await session.GetReservationAsync(key, cancellationToken);
                    if (holder is not null && !string.Equals(holder, id, StringComparison.OrdinalIgnoreCase)
                        && !await session.ExistsAsync(typeof(OidcApplication), holder, cancellationToken))
                        holder = null; // left behind by a deleted application: Reserve takes it over
                    if (holder is null)
                        session.Reserve(key, holder = id);
                    settled[key] = holder;
                }

                if (!string.Equals(holder, id, StringComparison.OrdinalIgnoreCase))
                    logger?.LogWarning("Application {ApplicationId} shares client id {ClientId} with {Holder}; it is not found by that client id.", id, clientId, holder);
            }
            await session.SaveChangesAsync(cancellationToken);
        }

        // The index the reservation replaces. Deleting one that is not there does nothing.
        await raven.Maintenance.SendAsync(new DeleteIndexOperation(RetiredIndex), cancellationToken);
    }
}
