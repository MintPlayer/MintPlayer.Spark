using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Shared;

namespace DemoApp.Tests;

/// <summary>
/// DemoApp's route table against <c>RouteSnapshots/DemoApp.txt</c>
/// (<c>docs/endpoints_generator_completion_plan.md</c> M0).
/// </summary>
/// <remarks>
/// DemoApp needs nothing beyond the database: its appsettings.json is complete.
/// See <see cref="RouteTableSnapshot"/> for what a line records; set <c>SPARK_UPDATE_ROUTE_SNAPSHOT=1</c>
/// to accept an intended change, then review the fixture diff like code.
/// </remarks>
public class RouteTableSnapshotTests : SparkTestDriver
{
    [Fact]
    public Task Route_table_matches_the_committed_snapshot()
        => SparkAppRouteHost<global::DemoApp.DemoSparkContext>.AssertSnapshotAsync(
            "DemoApp/DemoApp",
            Store,
            new Dictionary<string, string?>(),
            "apps/DemoApp/DemoApp.Tests/RouteSnapshots/DemoApp.txt");
}
