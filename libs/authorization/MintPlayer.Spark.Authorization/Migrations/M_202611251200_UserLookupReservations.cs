using Microsoft.Extensions.Logging;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Authorization.Migrations;

/// <summary>
/// Claims the email, user-name and login reservations of every stored <see cref="SparkUser"/> (#536).
///
/// <see cref="UserStore{TUser}"/> finds a user by name and by external login through the store's reservations (compare
/// exchange on RavenDB), as it already did by email: a query would need an index (D42), and a relational store keeps
/// the logins in a JSON column no query can filter. Users written before held only their email reservation, so their
/// names and logins are claimed here, once (and an email a seeded user lacks). A key another user already holds is left to that user and logged: the
/// account keeps working, it just isn't found under the duplicate.
///
/// RavenDB only: users on SQL Server, PostgreSQL or in memory were all written by the reserving store. A second run
/// claims nothing new (a holder claiming its own key again changes nothing).
/// </summary>
public partial class M_202611251200_UserLookupReservations : ISparkMigration
{
    public static long Version => 202611251200;
    public static string? Description => "Claim the user-name and login reservations of existing users";

    [Inject] private readonly IDocumentStore? raven;
    [Inject] private readonly ISparkStore store;
    [Inject] private readonly ILogger<M_202611251200_UserLookupReservations>? logger;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        if (raven is null || store.ProviderName != SparkStoreProviders.RavenDb)
            return;

        // Spliced into the scan below, because RQL takes no parameter for a collection; validated, because an
        // application can customise the convention to return anything.
        var collection = RqlIdentifier.Collection(raven.Conventions.FindCollectionName(typeof(SparkUser)));
        var users = new List<(string Id, SparkUser User)>();
        using (var session = raven.OpenAsyncSession())
        {
            // A collection scan, no index: nothing to wait for and nothing stale.
            var query = session.Advanced.AsyncRawQuery<UserRow>($"from '{collection}' select id() as Id, Email, NormalizedEmail, NormalizedUserName, Logins");
            await using var stream = await session.Advanced.StreamAsync(query, cancellationToken);
            while (await stream.MoveNextAsync())
            {
                var row = stream.Current.Document;
                var user = new SparkUser { Email = row.Email, NormalizedEmail = row.NormalizedEmail, NormalizedUserName = row.NormalizedUserName, Logins = row.Logins ?? [] };
                users.Add((row.Id ?? stream.Current.Id, user));
            }
        }

        foreach (var (id, user) in users)
        {
            var taken = await SparkUserReservations.ClaimAsync(store, id, user, cancellationToken);
            if (taken.Count > 0)
                logger?.LogWarning("User {UserId} keeps {Count} email, user name or login keys another user already holds; it is not found under them.", id, taken.Count);
        }
    }

    private sealed class UserRow
    {
        public string? Id { get; set; }
        public string? Email { get; set; }
        public string? NormalizedEmail { get; set; }
        public string? NormalizedUserName { get; set; }
        public List<SparkUserLogin>? Logins { get; set; }
    }
}
