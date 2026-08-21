using System.Net;
using System.Net.Http.Json;
using MintPlayer.Spark.E2E.Tests._Infrastructure;
using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// Cross-module writes against a real host: <c>/spark/sync/apply</c> and <c>/spark/etl/deploy</c>.
/// <para>
/// M11 routed sync writes through <c>IDatabaseAccess</c>'s authorization chokepoint, and F12 added
/// per-collection read authorization to ETL deployment. Both were covered only by unit tests over
/// substituted parts — and that gap turned out to be load-bearing: <b>F13 was a defect in M11's own
/// fix</b>. Neither endpoint established <c>HttpContext.User</c>, so every authenticated module
/// arrived at the newly-added permission check holding nothing but <c>anonymous</c>'s rights, and
/// every cross-module write would have been refused in production. Nothing failed, because nothing
/// exercised the whole path.
/// </para>
/// <para>
/// So the load-bearing test here is the <b>success</b> case. A suite that only checks refusals
/// passes just as happily when the feature is broken end to end.
/// </para>
/// <para>
/// <b>What this does not cover:</b> the certificate itself. These run against a dedicated host in
/// <c>Development</c> certificate mode, which verifies the caller names a registered module but does
/// not check a thumbprint — so what is exercised here is the authorization chain
/// (identity → <c>security.json</c> → chokepoint) and the module-registration gate, not mTLS. The
/// requirement to present a certificate at all is covered end to end by
/// <c>ReplicationEndpointAuthTests</c> against the strict shared host, which is why that host was
/// left alone rather than relaxed. Thumbprint <i>pinning</i> has unit coverage only; an end-to-end
/// mTLS test needs a host configured with a real client certificate and is still missing.
/// </para>
/// </summary>
[Collection(CrossModuleE2ECollection.Name)]
public class CrossModuleSyncTests
{
    private readonly CrossModuleE2EFixture _fixture;
    public CrossModuleSyncTests(CrossModuleE2EFixture fixture) => _fixture = fixture;

    /// <summary>Fleet's security.json grants this module <c>Replicate/Cars</c> + <c>ReadEditNew/Car</c>.</summary>
    private const string GrantedModule = "HR";

    /// <summary>
    /// Registered, so authentication succeeds — but named in no right, so authorization must not.
    /// The pair with <see cref="GrantedModule"/> is what separates "the endpoint works" from
    /// "the endpoint lets any authenticated module write anything", which is exactly F4.
    /// </summary>
    private const string UnprivilegedModule = "Marketing";

    private HttpClient NewClient() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    { BaseAddress = new Uri(_fixture.Host.FleetUrl) };

    /// <summary>
    /// A well-formed ETL deployment request. <c>TargetDatabase</c> and <c>TargetUrls</c> are
    /// <c>required</c> on the contract, and omitting them fails model binding — surfacing as a 400
    /// that looks nothing like an authorization outcome.
    /// </summary>
    private EtlScriptRequest DeployRequest(string module, string sourceCollection) => new()
    {
        RequestingModule = module,
        TargetDatabase = $"{module}-e2e",
        TargetUrls = _fixture.Host.RavenUrls,
        Scripts =
        [
            new EtlScriptItem
            {
                SourceCollection = sourceCollection,
                Script = $"loadTo{sourceCollection}(this);",
            },
        ],
    };

    private static SyncActionRequest InsertCar(string module, string documentId, string licensePlate) => new()
    {
        RequestingModule = module,
        Actions =
        [
            new SyncAction
            {
                ActionType = SyncActionType.Insert,
                Collection = "Cars",
                DocumentId = documentId,
                Data = new Dictionary<string, object?>
                {
                    ["LicensePlate"] = licensePlate,
                },
            },
        ],
    };

    [Fact]
    public async Task A_granted_module_can_write_through_sync_apply()
    {
        await _fixture.Host.SeedModuleAsync(GrantedModule);
        var documentId = $"Cars/e2e-sync-{Guid.NewGuid():N}";
        using var client = NewClient();

        var response = await client.PostAsJsonAsync(
            "/spark/sync/apply", InsertCar(GrantedModule, documentId, "SYNC-OK"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a module granted ReadEditNew/Car may write Cars — anything else means the chokepoint "
            + "is refusing a legitimate caller.\n--- SparkModules ---\n"
            + await _fixture.Host.DescribeModulesAsync()
            + "\n--- Fleet log ---\n" + _fixture.Host.RecentLog(25));

        // The status code alone would pass if the endpoint reported success without writing.
        // Point-load rather than query: this must not depend on index freshness.
        var stored = await _fixture.Host.LoadAsync<Dictionary<string, object?>>(documentId);
        stored.Should().NotBeNull("the sync action reported success, so the document must exist");
    }

    [Fact]
    public async Task A_registered_module_with_no_rights_is_refused_and_writes_nothing()
    {
        await _fixture.Host.SeedModuleAsync(UnprivilegedModule);
        var documentId = $"Cars/e2e-sync-{Guid.NewGuid():N}";
        using var client = NewClient();

        var response = await client.PostAsJsonAsync(
            "/spark/sync/apply", InsertCar(UnprivilegedModule, documentId, "SYNC-DENIED"));

        // Authentication succeeded (the module is registered), so this is not a 401/403 at the
        // gate — the action itself fails inside the batch, which the endpoint reports as 207.
        response.StatusCode.Should().Be(HttpStatusCode.MultiStatus,
            "the module authenticates but holds no right on Car, so the action must fail while the "
            + "request itself is well-formed");

        var stored = await _fixture.Host.LoadAsync<Dictionary<string, object?>>(documentId);
        stored.Should().BeNull("a refused sync action must leave no document behind");
    }

    [Fact]
    public async Task An_unregistered_module_is_refused_outright()
    {
        var documentId = $"Cars/e2e-sync-{Guid.NewGuid():N}";
        using var client = NewClient();

        var response = await client.PostAsJsonAsync(
            "/spark/sync/apply", InsertCar($"Ghost-{Guid.NewGuid():N}", documentId, "SYNC-GHOST"));

        // F1: Development mode used to accept any module name at all, making
        // {"RequestingModule": "anything"} from an unauthenticated caller a write primitive.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "naming a module that never registered is not authentication");

        var stored = await _fixture.Host.LoadAsync<Dictionary<string, object?>>(documentId);
        stored.Should().BeNull();
    }

    [Fact]
    public async Task Etl_deployment_is_refused_for_a_collection_the_module_may_not_replicate()
    {
        await _fixture.Host.SeedModuleAsync(GrantedModule);
        using var client = NewClient();

        // HR is granted Replicate/Cars and nothing else. SparkUsers is the collection that makes
        // the point: without F12's per-script check, any authenticated module could have the user
        // store continuously pushed into a database it controls.
        var response = await client.PostAsJsonAsync("/spark/etl/deploy",
            DeployRequest(GrantedModule, "SparkUsers"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "ETL grants read access to a whole collection, so it needs its own right — being a "
            + "known module is not enough");
    }

    [Fact]
    public async Task Etl_deployment_is_accepted_for_a_granted_collection()
    {
        await _fixture.Host.SeedModuleAsync(GrantedModule);
        using var client = NewClient();

        var response = await client.PostAsJsonAsync("/spark/etl/deploy",
            DeployRequest(GrantedModule, "Cars"));

        // The paired positive: F12's check must gate on the right, not refuse everything. Without
        // it, a fix that returned 403 unconditionally would look correct.
        //
        // Asserts the deployment SUCCEEDED, not merely that it was authorized. That became possible
        // once the licence in use included the RavenDB ETL feature — the repository default does not,
        // a developer licence does — so this test now proves the thing it is named for instead of
        // proving the absence of a refusal.
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "HR is granted Replicate/Cars, so the deployment must be authorized and succeed — got {0}.\n"
            + "--- Fleet log ---\n{1}", response.StatusCode, _fixture.Host.RecentLog(25));

        // And the state it should have produced. A 200 alone would pass if the endpoint reported
        // success without creating anything; the task name is derived from the *requesting* module,
        // so HR pulling from Fleet creates "spark-etl-HR" on Fleet.
        (await _fixture.Host.EtlTaskExistsAsync($"spark-etl-{GrantedModule}"))
            .Should().BeTrue("a successful deployment creates the ongoing ETL task it reported creating");
    }
}
