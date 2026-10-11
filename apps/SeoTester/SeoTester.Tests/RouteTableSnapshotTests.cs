using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Shared;

namespace SeoTester.Tests;

/// <summary>
/// SeoTester's route table against <c>RouteSnapshots/SeoTester.txt</c>: every endpoint with its methods and whether
/// it is reachable anonymously. A new public route (such as <c>/bot</c>) is then a reviewed diff. Set
/// <c>SPARK_UPDATE_ROUTE_SNAPSHOT=1</c> to accept an intended change.
/// </summary>
[Collection(_Infrastructure.SeoTesterHostCollection.Name)]
public class RouteTableSnapshotTests : SparkTestDriver
{
    [Fact]
    public Task Route_table_matches_the_committed_snapshot()
        => SparkAppRouteHost<global::SeoTester.SeoContext>.AssertSnapshotAsync(
            "SeoTester/SeoTester",
            Store,
            new Dictionary<string, string?>
            {
                ["Spark:Auth:PublicBaseUrl"] = "http://localhost",
                ["Spark:Mail:PickupFolder"] = Path.Combine(Path.GetTempPath(), "seotester-route-tests-mail"),
                ["Spark:Auth:Providers:MicrosoftAccount:ClientId"] = "route-test",
                ["Spark:Auth:Providers:MicrosoftAccount:ClientSecret"] = "route-test",
                ["Spark:Auth:Providers:GitHub:ClientId"] = "route-test",
                ["Spark:Auth:Providers:GitHub:ClientSecret"] = "route-test",
                ["Spark:Auth:Providers:Twitter:ClientId"] = "route-test",
                ["Spark:Auth:Providers:Twitter:ClientSecret"] = "route-test",
                ["Spark:Auth:Providers:Google:ClientId"] = "route-test",
                ["Spark:Auth:Providers:Google:ClientSecret"] = "route-test",
            },
            "apps/SeoTester/SeoTester.Tests/RouteSnapshots/SeoTester.txt");
}
