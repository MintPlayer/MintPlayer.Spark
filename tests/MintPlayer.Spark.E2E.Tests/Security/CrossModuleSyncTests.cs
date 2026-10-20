using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MintPlayer.Spark.E2E.Tests._Infrastructure;
using MintPlayer.Spark.Replication.Abstractions.Models;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// Cross-module calls against a real Fleet host (warehouse §4.2, WM1): write-back through <c>/spark/sync/apply</c>
/// (per-property compare-and-set inside the owner's save path) and registration through
/// <c>/spark/replicas/register</c> (the approval gate).
/// <para>
/// The load-bearing tests are the <b>success</b> cases: F13 was a defect in M11's own fix (no endpoint established
/// <c>HttpContext.User</c>, so every module write was refused), and nothing failed because nothing exercised the whole
/// path. A suite that only checks refusals passes just as happily when the feature is broken end to end.
/// </para>
/// <para>
/// <b>What this does not cover:</b> the certificate itself. These run against a dedicated host in <c>Development</c>
/// certificate mode, which verifies the caller names a registered module but does not check a thumbprint. The host's
/// environment is <c>E2EModules</c>, not Development, so registrations wait for an administrator (WD16). The
/// requirement to present a certificate at all is covered by <c>ReplicationEndpointAuthTests</c>; thumbprint
/// pinning end to end needs a host with a real client certificate and is still missing (WM13).
/// </para>
/// </summary>
[Collection(CrossModuleE2ECollection.Name)]
public class CrossModuleSyncTests
{
    private readonly CrossModuleE2EFixture _fixture;
    public CrossModuleSyncTests(CrossModuleE2EFixture fixture) => _fixture = fixture;

    /// <summary>Fleet's security.json grants this module <c>Replicate/Cars</c> + <c>ReadEditNew/Car</c>.</summary>
    private const string GrantedModule = "HR";

    /// <summary>Registered, so authentication succeeds — but named in no right, so authorization must not.</summary>
    private const string UnprivilegedModule = "Marketing";

    private HttpClient NewClient() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    })
    { BaseAddress = new Uri(_fixture.Host.FleetUrl) };

    private async Task<string> SeedCarAsync(string plate)
    {
        var id = $"Cars/e2e-sync-{Guid.NewGuid():N}";
        await _fixture.Host.StoreRawAsync(id, new Dictionary<string, object?> { ["LicensePlate"] = plate, ["Model"] = "E2E" }, "Cars", "Fleet.Entities.Car, Fleet.Library");
        return id;
    }

    private static SyncApplyRequest EditPlate(string module, string documentId, string basePlate, string newPlate) => new()
    {
        RequestingModule = module,
        OpId = Guid.NewGuid().ToString("N"),
        ActionType = SyncActionType.Update,
        SourceCollection = "Cars",
        DocumentId = documentId,
        Changes =
        [
            new SyncPropertyChange
            {
                Property = "LicensePlate",
                Base = JsonSerializer.SerializeToElement(basePlate),
                Value = JsonSerializer.SerializeToElement(newPlate),
            },
        ],
        InitiatorId = "SparkUsers/e2e",
    };

    private async Task<string?> PlateAsync(string id)
        => (await _fixture.Host.LoadAsync<Dictionary<string, object?>>(id))?["LicensePlate"]?.ToString();

    [Fact]
    public async Task A_granted_module_writes_back_through_sync_apply()
    {
        await _fixture.Host.SeedModuleAsync(GrantedModule);
        var id = await SeedCarAsync("SYNC-BEFORE");
        using var client = NewClient();

        var response = await client.PostAsJsonAsync("/spark/sync/apply", EditPlate(GrantedModule, id, "SYNC-BEFORE", "SYNC-OK"));

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a module granted ReadEditNew/Car may edit Cars — anything else means the chokepoint is refusing a legitimate caller.\n"
            + "--- body ---\n" + await response.Content.ReadAsStringAsync()
            + "\n--- SparkModules ---\n" + await _fixture.Host.DescribeModulesAsync()
            + "\n--- Fleet log ---\n" + _fixture.Host.RecentLog(25));
        (await PlateAsync(id)).Should().Be("SYNC-OK");
    }

    /// <summary>
    /// WD18 end to end: the edit started from a value Fleet no longer holds. The answer is a 409 naming the property,
    /// and Fleet's value stands (red on e0c542f9: the stale edit overwrote it).
    /// </summary>
    [Fact]
    public async Task A_stale_write_back_is_a_conflict_and_the_owners_value_stands()
    {
        await _fixture.Host.SeedModuleAsync(GrantedModule);
        var id = await SeedCarAsync("CHANGED-AT-FLEET");
        using var client = NewClient();

        var response = await client.PostAsJsonAsync("/spark/sync/apply", EditPlate(GrantedModule, id, "SEEN-ON-REPLICA", "EDITED-ON-REPLICA"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var result = await response.Content.ReadFromJsonAsync<SyncApplyResult>();
        result!.Attributes.Should().BeEquivalentTo(["LicensePlate"]);
        (await PlateAsync(id)).Should().Be("CHANGED-AT-FLEET");
    }

    /// <summary>F12: a module without the right gets a 403 it can show its user, never a 207 recorded as success.</summary>
    [Fact]
    public async Task A_registered_module_with_no_rights_is_refused_and_writes_nothing()
    {
        await _fixture.Host.SeedModuleAsync(UnprivilegedModule);
        var id = await SeedCarAsync("SYNC-UNTOUCHED");
        using var client = NewClient();

        var response = await client.PostAsJsonAsync("/spark/sync/apply", EditPlate(UnprivilegedModule, id, "SYNC-UNTOUCHED", "SYNC-DENIED"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PlateAsync(id)).Should().Be("SYNC-UNTOUCHED");
    }

    [Fact]
    public async Task An_unregistered_module_is_refused_outright()
    {
        var id = await SeedCarAsync("SYNC-GHOST");
        using var client = NewClient();

        var response = await client.PostAsJsonAsync("/spark/sync/apply", EditPlate($"Ghost-{Guid.NewGuid():N}", id, "SYNC-GHOST", "HACKED"));

        // F1: Development mode used to accept any module name at all.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "naming a module that never registered is not authentication");
        (await PlateAsync(id)).Should().Be("SYNC-GHOST");
    }

    private static ReplicaRegistrationRequest Registration(string module, string sourceCollection = "Cars") => new()
    {
        RequestingModule = module,
        Items =
        [
            new ReplicaRegistrationItem
            {
                TargetCollection = "Cars",
                SourceCollection = sourceCollection,
                // HR.Replicated.Car's generated constants, rendered here from the same manifest.
                Manifest = Manifest(sourceCollection),
                Script = Script(sourceCollection),
                Hash = MintPlayer.Spark.Replication.Abstractions.Rendering.ReplicaScriptRenderer.Hash(Manifest(sourceCollection), Script(sourceCollection)),
            },
        ],
    };

    private static MintPlayer.Spark.Replication.Abstractions.Rendering.ReplicaManifest Parsed(string source) => new(source, "Cars",
    [
        new("LicensePlate", ["LicensePlate"]),
        new("Model", ["Model"]),
    ]);

    private static string Manifest(string source) => MintPlayer.Spark.Replication.Abstractions.Rendering.ReplicaScriptRenderer.ToJson(Parsed(source));

    private static string Script(string source) => MintPlayer.Spark.Replication.Abstractions.Rendering.ReplicaScriptRenderer.RenderScript(Parsed(source));

    [Fact]
    public async Task Registration_is_refused_for_a_collection_the_module_may_not_replicate()
    {
        await _fixture.Host.SeedModuleAsync(UnprivilegedModule);
        using var client = NewClient();

        var response = await client.PostAsJsonAsync("/spark/replicas/register", Registration(UnprivilegedModule));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "replication reads a whole collection, so it needs its own right — being a known module is not enough");
    }

    [Fact]
    public async Task Registration_of_a_collection_the_model_does_not_govern_is_not_found()
    {
        await _fixture.Host.SeedModuleAsync(GrantedModule);
        using var client = NewClient();

        var response = await client.PostAsJsonAsync("/spark/replicas/register", Registration(GrantedModule, "NoSuchCollection"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>WD16: outside Development a granted registration waits for an administrator, and the request is recorded.</summary>
    [Fact]
    public async Task A_granted_registration_waits_for_an_administrator()
    {
        await _fixture.Host.SeedModuleAsync(GrantedModule);
        using var client = NewClient();

        var response = await client.PostAsJsonAsync("/spark/replicas/register", Registration(GrantedModule));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "--- Fleet log ---\n{0}", _fixture.Host.RecentLog(25));
        var result = await response.Content.ReadFromJsonAsync<ReplicaRegistrationResult>();
        result!.Items.Single().Status.Should().Be(ReplicaRegistrationStatus.Pending);
    }
}
