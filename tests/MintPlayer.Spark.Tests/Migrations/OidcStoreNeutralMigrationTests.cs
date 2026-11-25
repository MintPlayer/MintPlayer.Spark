using MintPlayer.Spark.Authorization.Identity;
using MintPlayer.Spark.IdentityProvider.Migrations;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.CompareExchange;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.Tests.Migrations;

/// <summary>
/// The RavenDB data migrations of the identity provider's store-neutral port (#536 D46, D47): the developer status moves
/// off the user document, and every application saved before reservations existed gets its client-id reservation.
/// </summary>
public class OidcStoreNeutralMigrationTests : RavenSparkTestDriver
{
    private sealed class LegacyUserView
    {
        public object? Developer { get; set; }
    }

    [Fact]
    public async Task The_developer_status_moves_into_its_own_document_and_leaves_the_user()
    {
        var requestedAt = new DateTime(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new SparkUser { UserName = "ada" }, "SparkUsers/ada");
            await session.StoreAsync(new SparkUser { UserName = "alan" }, "SparkUsers/alan");
            await session.StoreAsync(new SparkUser { UserName = "grace" }, "SparkUsers/grace");
            // Alan's status was moved already (a second run, or a write after the upgrade): it is kept.
            await session.StoreAsync(new OidcDeveloper { UserId = "SparkUsers/alan", Status = OidcDeveloperStatuses.Approved },
                OidcDevelopers.DocumentId("SparkUsers/alan"));
            await session.SaveChangesAsync();
        }
        await Store.Operations.SendAsync(new PatchOperation("SparkUsers/ada", null, new PatchRequest
        {
            Script = "this.Developer = { Status: 'Requested', TermsVersion: 3, RequestedAt: args.at, Note: 'please' };",
            Values = { ["at"] = requestedAt },
        }));
        await Store.Operations.SendAsync(new PatchOperation("SparkUsers/alan", null, new PatchRequest
        {
            Script = "this.Developer = { Status: 'Rejected', TermsVersion: 1 };",
        }));

        await new M_202611251300_OidcDevelopersCollection(raven: Store, store: Store.AsSparkStore()).UpAsync(CancellationToken.None);

        using (var check = Store.OpenAsyncSession())
        {
            var ada = (await check.LoadAsync<OidcDeveloper>(OidcDevelopers.DocumentId("SparkUsers/ada")))!;
            ada.UserId.Should().Be("SparkUsers/ada");
            ada.Status.Should().Be(OidcDeveloperStatuses.Requested);
            ada.TermsVersion.Should().Be(3);
            ada.RequestedAt.Should().Be(requestedAt);
            ada.Note.Should().Be("please");

            (await check.LoadAsync<OidcDeveloper>(OidcDevelopers.DocumentId("SparkUsers/alan")))!.Status
                .Should().Be(OidcDeveloperStatuses.Approved, "a status document that exists already is kept");
            (await check.LoadAsync<OidcDeveloper>(OidcDevelopers.DocumentId("SparkUsers/grace")))
                .Should().BeNull("a user who never asked has no status");
        }

        using (var raw = Store.OpenAsyncSession())
        {
            (await raw.LoadAsync<LegacyUserView>("SparkUsers/ada"))!.Developer.Should().BeNull("the old field is dropped");
            (await raw.LoadAsync<LegacyUserView>("SparkUsers/alan"))!.Developer.Should().BeNull();
        }

        await new M_202611251300_OidcDevelopersCollection(raven: Store, store: Store.AsSparkStore()).UpAsync(CancellationToken.None);
        AutoIndexes().Should().BeEmpty("a scan builds no index (D42)");
    }

    [Fact]
    public async Task Every_application_gets_its_client_id_reservation_and_the_index_goes()
    {
        await Store.Maintenance.SendAsync(new PutIndexesOperation(new IndexDefinition
        {
            Name = "OidcApplications/ByClientId",
            Maps = { "from a in docs.OidcApplications select new { a.ClientId }" },
        }));
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new OidcApplication { ClientId = "alpha", DisplayName = "Alpha" }, "OidcApplications/alpha");
            await session.StoreAsync(new OidcApplication { ClientId = "Beta", DisplayName = "Beta" }, "OidcApplications/beta");
            await session.StoreAsync(new OidcApplication { ClientId = "gamma", DisplayName = "Gamma" }, "OidcApplications/gamma");
            // A duplicate saved before reservations existed (O17): the first application keeps the client id.
            await session.StoreAsync(new OidcApplication { ClientId = "ALPHA", DisplayName = "Alpha again" }, "OidcApplications/alpha-2");
            await session.SaveChangesAsync();
        }
        // Beta holds its reservation already; gamma's key is left behind by a deleted application.
        await Store.Operations.SendAsync(new PutCompareExchangeValueOperation<string>(OidcClientIdReservation.Key("Beta"), "OidcApplications/beta", 0));
        await Store.Operations.SendAsync(new PutCompareExchangeValueOperation<string>(OidcClientIdReservation.Key("gamma"), "OidcApplications/deleted", 0));

        var migration = () => new M_202611251310_OidcClientIdReservations(raven: Store, store: Store.AsSparkStore(), logger: null).UpAsync(CancellationToken.None);
        await migration();

        (await HolderAsync("alpha")).Should().Be("oidcapplications/alpha");
        (await HolderAsync("beta")).Should().Be("oidcapplications/beta");
        (await HolderAsync("gamma")).Should().Be("oidcapplications/gamma", "a key whose holder no longer exists is taken over");
        (await Store.Maintenance.SendAsync(new GetIndexNamesOperation(0, 1024)))
            .Should().NotContain("OidcApplications/ByClientId", "the reservation replaces the index");

        await migration.Should().NotThrowAsync("a second run claims nothing");
        AutoIndexes().Should().BeEmpty("a scan builds no index (D42)");
    }

    /// <summary>The holder's id in lower case: ids compare ignoring case, and a streamed id comes back lower-cased.</summary>
    private async Task<string?> HolderAsync(string clientId)
        => (await Store.Operations.SendAsync(new GetCompareExchangeValueOperation<string>(OidcClientIdReservation.Key(clientId))))?.Value?.ToLowerInvariant();

    private IEnumerable<string> AutoIndexes()
        => Store.Maintenance.Send(new GetIndexNamesOperation(0, 1024)).Where(n => n.StartsWith("Auto/", StringComparison.OrdinalIgnoreCase));
}
