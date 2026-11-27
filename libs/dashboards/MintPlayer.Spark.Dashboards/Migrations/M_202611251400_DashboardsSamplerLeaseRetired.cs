using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Migrations;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;

namespace MintPlayer.Spark.Dashboards.Migrations;

/// <summary>
/// Deletes the sampler lease RavenDB applications held before the dashboards ran on every store (#536 D45, D46):
/// the compare-exchange value <c>spark/dashboards/sampler</c>, a JSON object. The lease is
/// <c>dashboards/sampler</c> now, a text value of <c>ISparkAtomicValues</c>, and nothing reads the old one.
/// <para>RavenDB only: no other store ever held it. A second run finds nothing to delete.</para>
/// </summary>
public partial class M_202611251400_DashboardsSamplerLeaseRetired : ISparkMigration
{
    public static long Version => 202611251400;
    public static string? Description => "Dashboards: delete the retired sampler lease spark/dashboards/sampler";

    private const string RetiredKey = "spark/dashboards/sampler";

    [Inject] private readonly IDocumentStore? raven;
    [Inject] private readonly ISparkStore store;

    public async Task UpAsync(CancellationToken cancellationToken)
    {
        if (raven is null || store.ProviderName != SparkStoreProviders.RavenDb)
            return;

        var current = await raven.Operations.SendAsync(new GetCompareExchangeValueOperation<object>(RetiredKey), token: cancellationToken);
        if (current is null)
            return;

        // Against its index: a host of the old version that renews it in between keeps it, and the next start deletes it.
        await raven.Operations.SendAsync(new DeleteCompareExchangeValueOperation<object>(RetiredKey, current.Index), token: cancellationToken);
    }
}
