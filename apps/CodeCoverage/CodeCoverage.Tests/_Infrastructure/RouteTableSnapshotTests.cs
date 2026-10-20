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

    /// <summary>
    /// The exact anonymous surface at route level (RX3, FR5; <see cref="AnonymousSurface"/>): every route reachable
    /// without credentials, <c>[AllowAnonymous]</c> or unannotated (Spark has no fallback policy; <c>/spark/*</c>,
    /// <c>/connect/*</c> and the webhook authorize inside their handlers). <c>AnonymousSurfaceTests</c> keeps the
    /// controller-level view with its reasons. Derived from the committed route snapshot on 2026-10-19. A failure means a
    /// way in without credentials was added or removed: decide that first, then edit this list.
    /// </summary>
    [Fact]
    public void The_anonymous_route_surface_is_exactly_the_recorded_set()
        => AnonymousSurface.AssertExactly(AnonymousSurface.Capture(host.Factory.Services), ExpectedAnonymousSurface);

    private static readonly string[] ExpectedAnonymousSurface =
    [
        "CONNECT,GET /spark/live | unannotated",
        "DELETE /spark/lookupref/{name}/{key} | unannotated",
        "GET /.well-known/jwks | unannotated",
        "GET /.well-known/oauth-authorization-server | unannotated",
        "GET /.well-known/oauth-protected-resource | allow-anonymous",
        "GET /.well-known/oauth-protected-resource/mcp | allow-anonymous",
        "GET /.well-known/openid-configuration | unannotated",
        "GET /badge/{provider}/{owner}/{name}.svg | allow-anonymous",
        "GET /connect/authorize | unannotated",
        "GET /connect/consent | unannotated",
        "GET /connect/error | unannotated",
        "GET /connect/login | unannotated",
        "GET /health | unannotated",
        "GET /health/ready | unannotated",
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
        "POST /api/github/webhooks | unannotated",
        "POST /api/uploads/fork/{provider}/{owner}/{name}/pull/{number:int} | allow-anonymous",
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
