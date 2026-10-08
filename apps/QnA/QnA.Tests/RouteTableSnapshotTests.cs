using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Shared;

namespace QnA.Tests;

/// <summary>
/// QnA's route table against <c>RouteSnapshots/QnA.txt</c>
/// (<c>docs/endpoints_generator_completion_plan.md</c> M0).
/// </summary>
/// <remarks>
/// The settings are the E2E host's (<c>QnATestHost</c>): the test seams are on, so
/// <c>/qna-test/moderation/*</c> is mapped too.
/// See <see cref="RouteTableSnapshot"/> for what a line records; set <c>SPARK_UPDATE_ROUTE_SNAPSHOT=1</c>
/// to accept an intended change, then review the fixture diff like code.
/// </remarks>
public class RouteTableSnapshotTests : SparkTestDriver
{
    [Fact]
    public Task Route_table_matches_the_committed_snapshot()
        => SparkAppRouteHost<global::QnA.QnAContext>.AssertSnapshotAsync(
            "QnA/QnA",
            Store,
            new Dictionary<string, string?>
            {
                ["Spark:Auth:PublicBaseUrl"] = "http://localhost",
                ["QnA:TestSeams:Enabled"] = "true",
            },
            "apps/QnA/QnA.Tests/RouteSnapshots/QnA.txt");
}
