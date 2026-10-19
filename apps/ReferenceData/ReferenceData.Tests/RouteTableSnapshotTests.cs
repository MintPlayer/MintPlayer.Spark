using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Shared;

namespace ReferenceData.Tests;

/// <summary>
/// apps/ReferenceData's route table against <c>RouteSnapshots/ReferenceData.txt</c>: it records the anonymous
/// <c>/api/v1</c> group, its CORS policy and its rate-limit policy per route (PRD §4.11, RX3 review). Set
/// <c>SPARK_UPDATE_ROUTE_SNAPSHOT=1</c> to accept an intended change, then review the fixture diff like code.
/// </summary>
public class RouteTableSnapshotTests : SparkTestDriver
{
    [Fact]
    public Task Route_table_matches_the_committed_snapshot()
        => SparkAppRouteHost<global::ReferenceData.ReferenceDataContext>.AssertSnapshotAsync(
            "ReferenceData/ReferenceData",
            Store,
            new Dictionary<string, string?> { ["Spark:ReferenceData:Seed:Enabled"] = "false" },
            "apps/ReferenceData/ReferenceData.Tests/RouteSnapshots/ReferenceData.txt");
}
