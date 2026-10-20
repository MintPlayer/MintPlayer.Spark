using System.Net;
using System.Net.Http.Json;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// R2-C1 / R2-C2 — <c>/spark/replicas/register</c> and <c>/spark/sync/apply</c> are cross-module
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
    public async Task Unauth_post_replica_register_with_unknown_requesting_module_is_refused()
    {
        using var http = SparkClientFactory.CreateHttpClient(_fixture.Host);

        var body = new
        {
            RequestingModule = "Attacker-Not-Registered",
            Items = new[]
            {
                new
                {
                    TargetCollection = "Users",
                    SourceCollection = "SparkUsers",
                    Manifest = "{\"v\":1,\"source\":\"SparkUsers\",\"target\":\"Users\",\"fields\":[[\"Email\",\"Email\"]]}",
                    Script = "loadToUsers({Email: this.Email})",
                    Hash = "sha256:00",
                }
            }
        };

        var response = await http.PostAsJsonAsync("/spark/replicas/register", body);

        // Production: the caller named a module, so the cert check runs first and no cert was
        // presented. Critically NOT 200 — before R2 there was no check at all, and a body like this
        // put an attacker-chosen script and target into RavenDB.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "replica registration must refuse a caller that presents no client certificate");
    }

    [Fact]
    public async Task Unauth_post_sync_apply_with_unknown_requesting_module_is_refused()
    {
        using var http = SparkClientFactory.CreateHttpClient(_fixture.Host);

        var response = await http.PostAsJsonAsync("/spark/sync/apply", DeleteUser("Attacker-Not-Registered"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "sync apply must refuse a caller that presents no client certificate — this endpoint "
            + "can change or delete documents, and was previously fully unauthenticated");
    }

    [Fact]
    public async Task Post_sync_apply_with_empty_requesting_module_is_refused()
    {
        using var http = SparkClientFactory.CreateHttpClient(_fixture.Host);

        var response = await http.PostAsJsonAsync("/spark/sync/apply", DeleteUser(""));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an empty RequestingModule is refused before the certificate is even considered, so "
            + "this is 403 where the named-module cases above are 401");
    }

    /// <summary>A well-formed write-back body (every required member present), so the answer is the gate's, not the binder's.</summary>
    private static object DeleteUser(string module) => new
    {
        RequestingModule = module,
        OpId = "e2e-op",
        ActionType = "Delete",
        SourceCollection = "SparkUsers",
        DocumentId = "users/victim",
        Changes = Array.Empty<object>(),
    };
}
