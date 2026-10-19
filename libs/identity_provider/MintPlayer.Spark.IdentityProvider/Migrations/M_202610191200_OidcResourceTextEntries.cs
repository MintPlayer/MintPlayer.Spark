using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;

namespace MintPlayer.Spark.IdentityProvider.Migrations;

/// <summary>
/// Moves the stored texts of <c>OidcResources</c> (resource and scope display names and descriptions)
/// to the one <c>TranslatedString</c> shape, <c>{"Entries":[{"Tag":"en","Value":"…"}]}</c>
/// (commerce X17, S24 M1). Production holds these in CodeCoverage and SeoTester.
/// <para>
/// Ordered after <see cref="M_202610090900_OidcDataModel"/> and
/// <see cref="M_202610091100_StandardIdentityResources"/>, which wrote the old shape on databases that
/// ran them before X17. On a fresh database those two already write the new shape, and this finds
/// nothing to do. The work is <see cref="OidcResourceTextUpgrade"/>, which the MCP seeding also runs.
/// </para>
/// </summary>
public partial class M_202610191200_OidcResourceTextEntries : ISparkMigration
{
    public static long Version => 202610191200;
    public static string? Description => "Identity provider: OidcResources texts to the TranslatedString entries shape (X17)";

    [Inject] private readonly IDocumentStore store;

    public Task UpAsync(CancellationToken cancellationToken)
        => OidcResourceTextUpgrade.RunAsync(store, cancellationToken);
}
