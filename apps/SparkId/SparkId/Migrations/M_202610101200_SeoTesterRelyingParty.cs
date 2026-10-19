using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents.Session;

namespace SparkId.Migrations;

/// <summary>
/// Development only: registers SeoTester (<c>docs/seo_analyzer_PRD.md</c> M7, https 5013) as a relying party, so a
/// developer signs in to it locally without registering a Microsoft, GitHub, X or Google app. Production SeoTester
/// signs in with those four only; SparkId appears in its <c>appsettings.Development.json</c> alone.
/// </summary>
/// <remarks>
/// A migration of its own because databases that already ran <see cref="M_202610081200_RelyingParties"/> must get it
/// too. The secret is a published development-only value, the same string as SeoTester's
/// <c>appsettings.Development.json</c>; it is stored hashed. An existing <c>seotester</c> client is left alone.
/// </remarks>
public partial class M_202610101200_SeoTesterRelyingParty : ISparkMigration
{
    public static string? Description => "Development: register the SeoTester relying party";

    internal const string ClientId = "seotester";
    internal const string Origin = "https://localhost:5013";
    internal const string Secret = "dev-only-seotester-secret-not-for-production";

    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IHostEnvironment environment;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
            return;

        await DevelopmentRelyingParties.EnsureAsync(session, ClientId, "SeoTester", Origin, Secret, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
    }
}
