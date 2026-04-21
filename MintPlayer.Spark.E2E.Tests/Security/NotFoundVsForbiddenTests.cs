using System.Net;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Client.Authorization;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Security;

/// <summary>
/// M-3 — authenticated callers must not be able to distinguish "record does not exist" from
/// "record exists but you can't read it". Otherwise the framework becomes an existence oracle
/// for IDs the caller doesn't own.
///
/// The server can legitimately surface the "denied" outcome as 403 (entity-type check failed),
/// 404 (nonexistent id), or null-via-row-level-filter — what matters for M-3 is that both
/// shapes (nonexistent id, real-but-forbidden id) produce the *same* client-observable
/// outcome. This test captures whichever shape comes back and asserts equality between them.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class NotFoundVsForbiddenTests
{
    private static readonly Guid CarTypeId = Guid.Parse("facb6829-f2a1-4ae2-a046-6ba506e8c0ce");

    private readonly FleetE2ECollectionFixture _fixture;
    public NotFoundVsForbiddenTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Nonexistent_id_and_forbidden_id_return_the_same_client_visible_shape()
    {
        // Admin seeds a Car — from a plain user's perspective this is a real-but-forbidden id.
        string adminCarId;
        using (var adminClient = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host))
        {
            var plate = $"NF{Guid.NewGuid():N}".Substring(0, 8).ToUpperInvariant();
            var created = await adminClient.CreatePersistentObjectAsync(new PersistentObject
            {
                Name = "Car",
                ObjectTypeId = CarTypeId,
                Attributes =
                [
                    new PersistentObjectAttribute { Name = "LicensePlate", Value = plate },
                    new PersistentObjectAttribute { Name = "Model",        Value = "M3" },
                    new PersistentObjectAttribute { Name = "Year",         Value = 2024 },
                ],
            });
            adminCarId = created.Id
                ?? throw new InvalidOperationException(
                    $"admin create returned no id\n--- Fleet log ---\n{_fixture.Host.RecentLog()}");
        }

        // Plain-user client: register + try to log in. If Fleet requires email confirmation
        // the login may fail, which is fine — the oracle-distinguishability assertion still
        // holds for the resulting anonymous principal (the Everyone group), who also has no
        // Car rights.
        using var plainClient = SparkClientFactory.ForFleet(_fixture.Host);
        var email = $"plain-{Guid.NewGuid():N}@e2e.local";
        var pass = _fixture.Host.AdminPass;
        try { await plainClient.RegisterAsync(email, pass); }
        catch (SparkClientException) { /* already-registered / validation edge-cases are tolerable */ }
        try { await plainClient.LoginAsync(email, pass); }
        catch (SparkClientException) { /* email-confirmation gate is out of scope for this test */ }

        var nonExistent = await CaptureOutcomeAsync(() =>
            plainClient.GetPersistentObjectAsync(CarTypeId, $"cars/definitely-does-not-exist-{Guid.NewGuid():N}"));
        var forbidden = await CaptureOutcomeAsync(() =>
            plainClient.GetPersistentObjectAsync(CarTypeId, adminCarId));

        forbidden.Should().Be(nonExistent,
            "the real-but-forbidden id and the nonexistent id must produce identical client outcomes — " +
            "any difference gives the caller an existence oracle for IDs they can't read");
    }

    /// <summary>
    /// Collapses either a null return or a non-success status into a single comparable value.
    /// </summary>
    private static async Task<HttpStatusCode?> CaptureOutcomeAsync(Func<Task<PersistentObject?>> call)
    {
        try
        {
            var po = await call();
            // Null-on-success is the "invisible" signal; pin it as a sentinel status.
            return po is null ? HttpStatusCode.NotFound : HttpStatusCode.OK;
        }
        catch (SparkClientException ex)
        {
            return ex.StatusCode;
        }
    }
}
