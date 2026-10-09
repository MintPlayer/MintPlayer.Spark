using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Shared;

namespace Fleet.Tests;

/// <summary>
/// Fleet's route table against <c>RouteSnapshots/Fleet.txt</c>
/// (<c>docs/endpoints_generator_completion_plan.md</c> M0).
/// </summary>
/// <remarks>
/// Fleet composes everything through <c>AddSparkFull</c>. The settings are the E2E host's (<c>FleetTestHost</c>):
/// with an authority configured, Fleet accepts JWT bearer tokens. It no longer hosts an identity provider (SparkId does).
/// See <see cref="RouteTableSnapshot"/> for what a line records; set <c>SPARK_UPDATE_ROUTE_SNAPSHOT=1</c>
/// to accept an intended change, then review the fixture diff like code.
/// </remarks>
public class RouteTableSnapshotTests : SparkTestDriver
{
    [Fact]
    public Task Route_table_matches_the_committed_snapshot()
        => SparkAppRouteHost<global::Fleet.FleetContext>.AssertSnapshotAsync(
            "Fleet/Fleet",
            Store,
            new Dictionary<string, string?>
            {
                ["Spark:JwtBearer:Authority"] = "http://localhost",
                ["Spark:JwtBearer:Audience"] = "fleet-api",
                ["Spark:Replication:ModuleUrl"] = "http://localhost",
                ["Spark:Replication:SparkModulesUrls:0"] = Store.Urls[0],
                ["Spark:Replication:SparkModulesDatabase"] = Store.Database + "-modules",
            },
            "apps/Fleet/Fleet.Tests/RouteSnapshots/Fleet.txt");
}
