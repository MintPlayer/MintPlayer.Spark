using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Shared;

namespace SparkId.Tests;

/// <summary>
/// SparkId's route table against <c>RouteSnapshots/SparkId.txt</c>
/// (<c>docs/endpoints_generator_completion_plan.md</c> M0).
/// </summary>
/// <remarks>
/// The settings are the E2E host's (<c>SparkIdTestHost</c>): the identity provider needs a signing key
/// outside Development.
/// See <see cref="RouteTableSnapshot"/> for what a line records; set <c>SPARK_UPDATE_ROUTE_SNAPSHOT=1</c>
/// to accept an intended change, then review the fixture diff like code.
/// </remarks>
public class RouteTableSnapshotTests : SparkTestDriver
{
    [Fact]
    public Task Route_table_matches_the_committed_snapshot()
        => SparkAppRouteHost<global::SparkId.SparkIdContext>.AssertSnapshotAsync(
            "SparkId/SparkId",
            Store,
            new Dictionary<string, string?>
            {
                ["SparkIdentityProvider:Issuer"] = "http://localhost",
                ["SparkIdentityProvider:SigningKeyPath"] = SparkAppSigningKey.NewFile(),
            },
            "apps/SparkId/SparkId.Tests/RouteSnapshots/SparkId.txt");
}
