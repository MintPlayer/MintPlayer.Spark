using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Shared;

namespace HR.Tests;

/// <summary>
/// HR's route table against <c>RouteSnapshots/HR.txt</c>
/// (<c>docs/endpoints_generator_completion_plan.md</c> M0).
/// </summary>
/// <remarks>
/// The settings are the E2E host's (<c>HRTestHost</c>): the identity provider needs a signing key
/// outside Development, and replication a modules database.
/// See <see cref="RouteTableSnapshot"/> for what a line records; set <c>SPARK_UPDATE_ROUTE_SNAPSHOT=1</c>
/// to accept an intended change, then review the fixture diff like code.
/// </remarks>
public class RouteTableSnapshotTests : SparkTestDriver
{
    [Fact]
    public Task Route_table_matches_the_committed_snapshot()
        => SparkAppRouteHost<global::HR.HRContext>.AssertSnapshotAsync(
            "HR/HR",
            Store,
            new Dictionary<string, string?>
            {
                ["SparkIdentityProvider:Issuer"] = "http://localhost",
                ["SparkIdentityProvider:SigningKeyPath"] = SparkAppSigningKey.NewFile(),
                ["Spark:Replication:ModuleUrl"] = "http://localhost",
                ["Spark:Replication:SparkModulesUrls:0"] = Store.Urls[0],
                ["Spark:Replication:SparkModulesDatabase"] = Store.Database + "-modules",
            },
            "apps/HR/HR.Tests/RouteSnapshots/HR.txt");
}
