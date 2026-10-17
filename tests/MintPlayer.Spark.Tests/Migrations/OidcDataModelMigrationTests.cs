using MintPlayer.Spark.IdentityProvider.Migrations;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Indexes;

namespace MintPlayer.Spark.Tests.Migrations;

/// <summary>
/// <see cref="M_202610090900_OidcDataModel"/> moves an application's legacy <c>AllowedScopes</c> list into
/// <c>Scopes</c> without making RavenDB build an index.
/// </summary>
/// <remarks>
/// D-O22: the migration used to find those applications with <c>where exists(AllowedScopes)</c>, which built
/// <c>Auto/OidcApplications/ByAllowedScopes</c> on every fresh database (the CodeCoverage app's startup, 2026-10-17).
/// Indexes belong to the application, so the migration scans the collection instead.
/// </remarks>
public class OidcDataModelMigrationTests : SparkTestDriver
{
    private sealed class LegacyView
    {
        public List<string>? AllowedScopes { get; set; }
    }

    [Fact]
    public async Task Legacy_allowed_scopes_move_into_scopes_and_no_auto_index_is_built()
    {
        using (var session = Store.OpenAsyncSession())
        {
            await session.StoreAsync(new OidcApplication { ClientId = "legacy", DisplayName = "Legacy" }, "OidcApplications/legacy");
            await session.StoreAsync(new OidcApplication { ClientId = "current", DisplayName = "Current" }, "OidcApplications/current");
            await session.SaveChangesAsync();
        }
        await Store.Operations.SendAsync(new PatchOperation("OidcApplications/legacy", null,
            new PatchRequest { Script = "this.AllowedScopes = ['openid', 'profile'];" }));

        await new M_202610090900_OidcDataModel(Store).UpAsync(CancellationToken.None);

        using (var check = Store.OpenAsyncSession())
        {
            var legacy = (await check.LoadAsync<OidcApplication>("OidcApplications/legacy"))!;
            legacy.Scopes.Select(s => s.Name).Should().BeEquivalentTo(["openid", "profile"]);
            legacy.Mode.Should().Be(OidcApplicationModes.Live);

            var current = (await check.LoadAsync<OidcApplication>("OidcApplications/current"))!;
            current.Scopes.Should().BeEmpty();
            current.Mode.Should().Be(OidcApplicationModes.Development, "an application without the legacy field is not touched");
        }

        using (var raw = Store.OpenAsyncSession())
            (await raw.LoadAsync<LegacyView>("OidcApplications/legacy"))!.AllowedScopes.Should().BeNull("the old field is dropped");

        (await Store.Maintenance.SendAsync(new GetIndexNamesOperation(0, 1024)))
            .Where(name => name.StartsWith("Auto/", StringComparison.OrdinalIgnoreCase))
            .Should().BeEmpty("indexes belong to the application");
    }
}
