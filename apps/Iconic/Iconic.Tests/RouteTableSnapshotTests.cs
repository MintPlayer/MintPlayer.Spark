using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Shared;

namespace Iconic.Tests;

/// <summary>
/// Iconic's route table against <c>RouteSnapshots/Iconic.txt</c>: every endpoint with its methods and whether it is
/// reachable anonymously. Only the shared-link reads and the report are meant to be (D20); a new anonymous route is
/// then a reviewed diff. Set <c>SPARK_UPDATE_ROUTE_SNAPSHOT=1</c> to accept an intended change.
/// </summary>
public class RouteTableSnapshotTests : SparkTestDriver
{
    [Fact]
    public Task Route_table_matches_the_committed_snapshot()
        => SparkAppRouteHost<global::Iconic.IconicContext>.AssertSnapshotAsync(
            "Iconic/Iconic",
            Store,
            new Dictionary<string, string?>
            {
                ["Spark:Auth:PublicBaseUrl"] = "http://localhost",
                ["Spark:Mail:PickupFolder"] = Path.Combine(Path.GetTempPath(), "iconic-route-tests-mail"),
                ["Spark:Auth:Providers:MicrosoftAccount:ClientId"] = "route-test",
                ["Spark:Auth:Providers:MicrosoftAccount:ClientSecret"] = "route-test",
                ["Spark:Auth:Providers:GitHub:ClientId"] = "route-test",
                ["Spark:Auth:Providers:GitHub:ClientSecret"] = "route-test",
                ["Spark:Auth:Providers:Google:ClientId"] = "route-test",
                ["Spark:Auth:Providers:Google:ClientSecret"] = "route-test",
            },
            "apps/Iconic/Iconic.Tests/RouteSnapshots/Iconic.txt");
}
