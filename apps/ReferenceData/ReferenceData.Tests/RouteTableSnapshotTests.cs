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
            "apps/ReferenceData/ReferenceData.Tests/RouteSnapshots/ReferenceData.txt",
            ExpectedAnonymousSurface);

    /// <summary>
    /// The exact anonymous surface (RX3, FR5; <see cref="AnonymousSurface"/>): every route reachable without
    /// credentials, either <c>[AllowAnonymous]</c> or unannotated (Spark has no fallback policy; <c>/spark/*</c>
    /// authorizes inside its handlers). ⚠️ UNVERIFIED: this app has no committed snapshot yet (M22 creates it), so the
    /// set was assembled from reading the code on 2026-10-19 (Spark core as DemoApp, /spark/auth as HR, the /api/v1
    /// group, MapHealthChecks with no verb metadata); confirm it on the first run. A failure means a
    /// way in without credentials was added or removed: decide that first, then edit this list.
    /// </summary>
    private static readonly string[] ExpectedAnonymousSurface =
    [
        "* /health | allow-anonymous",
        "CONNECT,GET /spark/live | unannotated",
        "DELETE /spark/lookupref/{name}/{key} | unannotated",
        "GET /api/v1/by-code/{cc}/{scheme}/{code} | allow-anonymous",
        "GET /api/v1/countries | allow-anonymous",
        "GET /api/v1/countries/{cc}/profile | allow-anonymous",
        "GET /api/v1/places/{**placePath} | allow-anonymous",
        "GET /api/v1/postalcodes/{cc}/{code} | allow-anonymous",
        "GET /api/v1/releases/latest | allow-anonymous",
        "GET /api/v1/releases/{release}/{**file} | allow-anonymous",
        "GET /api/v1/resolve | allow-anonymous",
        "GET /api/v1/sources | allow-anonymous",
        "GET /api/v1/streets/suggest | allow-anonymous",
        "GET /api/v1/suggest | allow-anonymous",
        "GET /api/v1/timezones | allow-anonymous",
        "GET /spark | unannotated",
        "GET /spark/aliases | unannotated",
        "GET /spark/auth/capabilities | unannotated",
        "GET /spark/auth/confirm-external-link | allow-anonymous",
        "GET /spark/auth/confirmEmail | unannotated",
        "GET /spark/auth/external-login | unannotated",
        "GET /spark/auth/external-login-callback | allow-anonymous",
        "GET /spark/auth/external-login/two-factor | allow-anonymous",
        "GET /spark/auth/me | unannotated",
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
        "POST /spark/auth/confirm-email | unannotated",
        "POST /spark/auth/csrf-refresh | unannotated",
        "POST /spark/auth/external-login/two-factor | allow-anonymous",
        "POST /spark/auth/forgotPassword | unannotated",
        "POST /spark/auth/login | unannotated",
        "POST /spark/auth/logout | unannotated",
        "POST /spark/auth/passkeys/request-options | unannotated",
        "POST /spark/auth/passkeys/sign-in | unannotated",
        "POST /spark/auth/refresh | unannotated",
        "POST /spark/auth/register | unannotated",
        "POST /spark/auth/resendConfirmationEmail | unannotated",
        "POST /spark/auth/resetPassword | unannotated",
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
