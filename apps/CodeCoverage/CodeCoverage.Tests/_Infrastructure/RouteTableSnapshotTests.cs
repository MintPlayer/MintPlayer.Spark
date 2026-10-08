using MintPlayer.Spark.Testing;
using Xunit;

namespace CodeCoverage.Tests._Infrastructure;

/// <summary>
/// The production app's route table against a committed snapshot
/// (<c>docs/endpoints_generator_completion_plan.md</c> M0).
/// </summary>
/// <remarks>
/// Boots the real <c>Program</c> through <see cref="CoverageWebHostFixture"/> (not Development, so no
/// Angular dev server) and compares its endpoint data source with <c>RouteSnapshots/CodeCoverage.txt</c>.
/// <c>/health</c> and <c>/health/ready</c> are polled anonymously by docker compose and the deploy
/// workflow, and the MVC controllers carry <c>[SparkAuthorize]</c>; a refactor that moves or
/// re-guards any of them shows up here as a one-line diff. See <see cref="RouteTableSnapshot"/> for
/// what a line records; set <c>SPARK_UPDATE_ROUTE_SNAPSHOT=1</c> to accept an intended change.
/// </remarks>
[Collection(CoverageWebHostCollection.Name)]
public class RouteTableSnapshotTests(CoverageWebHostFixture host)
{
    [Fact]
    public void Route_table_matches_the_committed_snapshot()
    {
        var snapshot = RouteTableSnapshot.Capture(host.Factory.Services);

        RouteTableSnapshot.AssertMatchesFixture(snapshot, "apps/CodeCoverage/CodeCoverage.Tests/RouteSnapshots/CodeCoverage.txt");
    }
}
