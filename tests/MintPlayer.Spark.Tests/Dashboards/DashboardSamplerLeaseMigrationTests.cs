using MintPlayer.Spark.Dashboards.Migrations;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Operations.CompareExchange;

namespace MintPlayer.Spark.Tests.Dashboards;

/// <summary>
/// #536 D46/M8c: the sampler lease RavenDB applications held before D45 (<c>spark/dashboards/sampler</c>, a JSON object)
/// is no longer read by anything; a RavenDB migration deletes it rather than leaving it behind.
/// </summary>
public class DashboardSamplerLeaseMigrationTests : RavenSparkTestDriver
{
    private sealed class LegacyLease
    {
        public string? Holder { get; set; }
        public long Slot { get; set; }
    }

    [Fact]
    public async Task The_retired_lease_is_deleted_and_the_current_one_kept()
    {
        await Store.Operations.SendAsync(new PutCompareExchangeValueOperation<LegacyLease>(
            "spark/dashboards/sampler", new LegacyLease { Holder = "host-a/1", Slot = 42 }, 0));
        await Store.Operations.SendAsync(new PutCompareExchangeValueOperation<string>("dashboards/sampler", "host-b/2|43|0", 0));

        var migration = () => new M_202611251400_DashboardsSamplerLeaseRetired(raven: Store, store: Store.AsSparkStore()).UpAsync(CancellationToken.None);
        await migration();

        (await Store.Operations.SendAsync(new GetCompareExchangeValueOperation<LegacyLease>("spark/dashboards/sampler")))
            .Should().BeNull("nothing reads the retired lease");
        (await Store.Operations.SendAsync(new GetCompareExchangeValueOperation<string>("dashboards/sampler")))!.Value
            .Should().Be("host-b/2|43|0", "the lease D45 introduced is not touched");

        await migration.Should().NotThrowAsync("a second run finds nothing to delete");
    }
}
