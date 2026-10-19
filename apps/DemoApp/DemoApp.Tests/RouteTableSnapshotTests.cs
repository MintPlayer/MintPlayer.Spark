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
            "apps/DemoApp/DemoApp.Tests/RouteSnapshots/DemoApp.txt",
            ExpectedAnonymousSurface);

    /// <summary>
    /// The exact anonymous surface (RX3, FR5; <see cref="AnonymousSurface"/>): every route reachable without
    /// credentials, either <c>[AllowAnonymous]</c> or unannotated (Spark has no fallback policy; <c>/spark/*</c>
    /// authorizes inside its handlers). Derived from the committed route snapshot on 2026-10-19. A failure means a
    /// way in without credentials was added or removed: decide that first, then edit this list.
    /// </summary>
    private static readonly string[] ExpectedAnonymousSurface =
    [
        "CONNECT,GET /spark/live | unannotated",
        "DELETE /spark/lookupref/{name}/{key} | unannotated",
        "GET /WeatherForecast | unannotated",
        "GET /spark | unannotated",
        "GET /spark/aliases | unannotated",
        "GET /spark/culture | unannotated",
        "GET /spark/lookupref | unannotated",
        "GET /spark/lookupref/{name} | unannotated",
        "GET /spark/permissions/{entityTypeId} | unannotated",
        "GET /spark/program-units | unannotated",
        "GET /spark/queries | unannotated",
        "GET /spark/translations | unannotated",
        "GET /spark/types | unannotated",
        "GET /spark/types/{id} | unannotated",
        "POST /spark/actions/execute | unannotated",
        "POST /spark/actions/list | unannotated",
        "POST /spark/lookupref/{name} | unannotated",
        "POST /spark/po/create | unannotated",
        "POST /spark/po/delete | unannotated",
        "POST /spark/po/delete-many | unannotated",
        "POST /spark/po/delete-row | unannotated",
        "POST /spark/po/load | unannotated",
        "POST /spark/po/new | unannotated",
        "POST /spark/po/refresh | unannotated",
        "POST /spark/po/update | unannotated",
        "POST /spark/queries/distinct-values | unannotated",
        "POST /spark/queries/execute | unannotated",
        "POST /spark/queries/get | unannotated",
        "POST /spark/share/load | allow-anonymous",
        "POST /spark/share/query | allow-anonymous",
        "PUT /spark/lookupref/{name}/{key} | unannotated",
    ];
}
