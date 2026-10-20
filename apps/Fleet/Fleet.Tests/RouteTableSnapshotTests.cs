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
                // The agents' authorization server of appsettings.Development.json (idp.ForMcp().AgentsOnly() when an issuer is set),
                // so its /connect and /.well-known routes are in the snapshot beside /mcp. The E2E host runs without it.
                ["Spark:IdentityProvider:Issuer"] = "http://localhost",
                ["Spark:IdentityProvider:SigningKeyPath"] = SparkAppSigningKey.NewFile(),
                ["Spark:Mcp:CanonicalUrl"] = "http://localhost/mcp",
            },
            "apps/Fleet/Fleet.Tests/RouteSnapshots/Fleet.txt",
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
        "GET /connect/authorize | unannotated",
        "GET /connect/consent | unannotated",
        "GET /connect/error | unannotated",
        "GET /connect/login | unannotated",
        "GET /connect/two-factor | unannotated",
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
        "POST /connect/authorize | unannotated",
        "POST /connect/consent | unannotated",
        "POST /connect/login | unannotated",
        "POST /connect/register | unannotated",
        "POST /connect/revoke | unannotated",
        "POST /connect/token | unannotated",
        "POST /connect/two-factor | unannotated",
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
        "POST /spark/etl/deploy | unannotated",
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
        "POST /spark/sync/apply | unannotated",
        "PUT /spark/lookupref/{name}/{key} | unannotated",
    ];
}
