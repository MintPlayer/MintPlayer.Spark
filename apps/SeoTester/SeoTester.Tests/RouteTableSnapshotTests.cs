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
            "apps/SeoTester/SeoTester.Tests/RouteSnapshots/SeoTester.txt",
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
        "GET /.well-known/jwks | unannotated",
        "GET /.well-known/oauth-authorization-server | unannotated",
        "GET /.well-known/oauth-protected-resource | allow-anonymous",
        "GET /.well-known/oauth-protected-resource/mcp | allow-anonymous",
        "GET /.well-known/openid-configuration | unannotated",
        "GET /bot | unannotated",
        "GET /connect/authorize | unannotated",
        "GET /connect/consent | unannotated",
        "GET /connect/error | unannotated",
        "GET /connect/login | unannotated",
        "GET /health | unannotated",
        "GET /privacy | unannotated",
        "GET /seotester/badge.svg | unannotated",
        "GET /seotester/catalogue | unannotated",
        "GET /seotester/og-image | unannotated",
        "GET /seotester/report.pdf | unannotated",
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
        "GET /terms | unannotated",
        "POST /connect/authorize | unannotated",
        "POST /connect/consent | unannotated",
        "POST /connect/register | unannotated",
        "POST /connect/revoke | unannotated",
        "POST /connect/token | unannotated",
        "POST /spark/actions/execute | unannotated",
        "POST /spark/actions/list | unannotated",
        "POST /spark/auth/confirm-email | unannotated",
        "POST /spark/auth/csrf-refresh | unannotated",
        "POST /spark/auth/external-login/two-factor | allow-anonymous",
        "POST /spark/auth/logout | unannotated",
        "POST /spark/auth/passkeys/request-options | unannotated",
        "POST /spark/auth/passkeys/sign-in | unannotated",
        "POST /spark/lookupref/{name} | unannotated",
        "POST /spark/mail/bounces | unannotated",
        "POST /spark/mail/unsubscribe | unannotated",
        "POST /spark/po/create | unannotated",
        "POST /spark/po/delete | unannotated",
        "POST /spark/po/delete-many | unannotated",
        "POST /spark/po/delete-row | unannotated",
        "POST /spark/po/load | unannotated",
        "POST /spark/po/new | unannotated",
        "POST /spark/po/refresh | unannotated",
        "POST /spark/po/revert | unannotated",
        "POST /spark/po/revision | unannotated",
        "POST /spark/po/revisions | unannotated",
        "POST /spark/po/update | unannotated",
        "POST /spark/queries/distinct-values | unannotated",
        "POST /spark/queries/execute | unannotated",
        "POST /spark/queries/get | unannotated",
        "POST /spark/share/load | allow-anonymous",
        "POST /spark/share/query | allow-anonymous",
        "PUT /spark/lookupref/{name}/{key} | unannotated",
    ];
}
