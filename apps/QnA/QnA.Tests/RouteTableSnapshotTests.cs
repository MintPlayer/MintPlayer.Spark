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
            "apps/QnA/QnA.Tests/RouteSnapshots/QnA.txt",
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
        "POST /qna-test/moderation/credit | unannotated",
        "POST /qna-test/moderation/detect | unannotated",
        "POST /qna-test/moderation/recompute | unannotated",
        "POST /spark/actions/execute | unannotated",
        "POST /spark/actions/list | unannotated",
        "POST /spark/auth/confirm-email | unannotated",
        "POST /spark/auth/csrf-refresh | unannotated",
        "POST /spark/auth/external-login/two-factor | allow-anonymous",
        "POST /spark/auth/forgotPassword | unannotated",
        "POST /spark/auth/login | unannotated",
        "POST /spark/auth/logout | unannotated",
        "POST /spark/auth/refresh | unannotated",
        "POST /spark/auth/register | unannotated",
        "POST /spark/auth/resendConfirmationEmail | unannotated",
        "POST /spark/auth/resetPassword | unannotated",
        "POST /spark/lookupref/{name} | unannotated",
        "POST /spark/mail/bounces | unannotated",
        "POST /spark/mail/unsubscribe | unannotated",
        "POST /spark/moderation/audit | unannotated",
        "POST /spark/moderation/case | unannotated",
        "POST /spark/moderation/case/decide | unannotated",
        "POST /spark/moderation/cases | unannotated",
        "POST /spark/moderation/flag | unannotated",
        "POST /spark/moderation/lock | unannotated",
        "POST /spark/moderation/merge | unannotated",
        "POST /spark/moderation/reputation | unannotated",
        "POST /spark/moderation/reputation/history | unannotated",
        "POST /spark/moderation/status | unannotated",
        "POST /spark/moderation/suspend | unannotated",
        "POST /spark/moderation/unlock | unannotated",
        "POST /spark/moderation/unsuspend | unannotated",
        "POST /spark/moderation/vote | unannotated",
        "POST /spark/moderation/votes | unannotated",
        "POST /spark/po/create | unannotated",
        "POST /spark/po/delete | unannotated",
        "POST /spark/po/delete-many | unannotated",
        "POST /spark/po/delete-row | unannotated",
        "POST /spark/po/load | unannotated",
        "POST /spark/po/new | unannotated",
        "POST /spark/po/purge | unannotated",
        "POST /spark/po/refresh | unannotated",
        "POST /spark/po/restore | unannotated",
        "POST /spark/po/revert | unannotated",
        "POST /spark/po/revert-contribution | unannotated",
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
