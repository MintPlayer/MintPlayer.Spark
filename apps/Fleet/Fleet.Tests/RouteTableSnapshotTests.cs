using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Shared;

namespace Fleet.Tests;

/// <summary>
/// Fleet's route table against <c>RouteSnapshots/Fleet.txt</c>
/// (<c>docs/endpoints_generator_completion_plan.md</c> M0).
/// </summary>
/// <remarks>
/// Fleet composes everything through <c>AddSparkFull</c>. The settings are the E2E host's (<c>FleetTestHost</c>):
/// with an authority configured, Fleet accepts JWT bearer tokens. People sign in at SparkId; Fleet hosts an identity
/// provider only as its AI agents' authorization server (MCP setup 3), which the settings below turn on as Development does.
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
                // The agents' authorization server of appsettings.Development.json (idp.ForMcp() when an issuer is set),
                // so its /connect and /.well-known routes are in the snapshot beside /mcp. The E2E host runs without it.
                ["Spark:IdentityProvider:Issuer"] = "http://localhost",
                ["Spark:IdentityProvider:SigningKeyPath"] = SparkAppSigningKey.NewFile(),
                ["Spark:Mcp:CanonicalUrl"] = "http://localhost/mcp",
            },
            "apps/Fleet/Fleet.Tests/RouteSnapshots/Fleet.txt");
}
