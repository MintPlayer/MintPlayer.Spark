using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

namespace SparkId.Migrations;

/// <summary>
/// Development only: the resource-server demo (<c>docs/identity_provider_platform_PRD.md</c> I12). Seeds the API
/// resource <c>fleet</c> with the scope <c>fleet.read</c>, and lets HR ask for it, so an HR user's access token can
/// call Fleet's <c>/api/fleet/cars</c>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The resource's name is the audience: Fleet's <c>Spark:JwtBearer:Audience</c> is <c>fleet</c> in Development.</item>
/// <item>The <c>fleet</c> client (seeded by <see cref="M_202610081200_RelyingParties"/>) has the audience's name as its
/// client id, which is what lets Fleet introspect these tokens when it runs with <c>UseIntrospection</c>.</item>
/// <item>Optional on HR, so the user may untick it on the consent page. A separate migration, so databases that
/// already ran the relying-party seed get it too; an existing resource or scope entry is left alone.</item>
/// </list>
/// </remarks>
public partial class M_202610091000_FleetApi : ISparkMigration
{
    public static long Version => 202610091000;
    public static string? Description => "Development: seed the fleet API resource (fleet.read) and offer it to HR";

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IHostEnvironment environment;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
            return;

        var id = OidcScopeCatalog.ResourceId("fleet");
        if (!await session.Advanced.ExistsAsync(id, cancellationToken))
        {
            await session.StoreAsync(new OidcResource
            {
                Id = id,
                Kind = OidcResourceKinds.Api,
                Name = "fleet",
                DisplayName = TranslatedString.Create("Spark Fleet", "Spark Fleet", "Spark Fleet"),
                Enabled = true,
                ShowInDiscoveryDocument = true,
                Scopes =
                [
                    new OidcApiScope
                    {
                        Name = "fleet.read",
                        DisplayName = TranslatedString.Create("Read the fleet's cars", "Lire les voitures de la flotte", "De auto's van de vloot lezen"),
                        Enabled = true,
                    },
                ],
            }, cancellationToken);
        }

        var hr = await session.Query<OidcApplication>()
            .Where(a => a.ClientId == "hr", exact: true)
            .FirstOrDefaultAsync(cancellationToken);
        if (hr is not null && !hr.Scopes.Any(s => s.Name == "fleet.read"))
            hr.Scopes.Add(new OidcApplicationScope { Name = "fleet.read", Required = false });

        await session.SaveChangesAsync(cancellationToken);
    }
}
