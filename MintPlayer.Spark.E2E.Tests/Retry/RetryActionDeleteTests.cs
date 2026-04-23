using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Client.Authorization;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Retry;

/// <summary>
/// PRD §3 — retry-action delete round-trip. Fleet's <c>CarActions.OnDeleteAsync</c>
/// pauses the delete via <c>manager.Retry.Action</c>, which surfaces as HTTP 449 with a
/// scaffolded <c>ConfirmDeleteCar</c> Virtual PO. The client is expected to fill
/// <c>Confirmation</c> and retry; the server only actually deletes when the typed plate
/// matches. SparkClient doesn't yet re-submit retryResults on Delete (tracked
/// separately), so these tests drive the wire with raw HTTP to verify the server loop.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class RetryActionDeleteTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public RetryActionDeleteTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Delete_with_correct_confirmation_completes_the_removal()
    {
        using var adminClient = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);
        var plate = CarFixture.RandomLicensePlate("RD");
        var created = await adminClient.CreatePersistentObjectAsync(CarFixture.New(plate));
        created.Id.Should().NotBeNullOrEmpty();

        // 1. First delete — server throws SparkRetryActionException, endpoint returns 449
        //    with a scaffolded ConfirmDeleteCar PO.
        var first = await adminClient.SendAsync(HttpMethod.Delete, DeleteUrl(created.Id!), requiresAntiforgery: true);
        ((int)first.StatusCode).Should().Be(449,
            $"first delete must surface the retry-action\n--- Fleet log tail ---\n{_fixture.Host.RecentLog()}");

        var payload = await first.Content.ReadFromJsonAsync<JsonElement>();
        payload.GetProperty("title").GetString().Should().Be("Delete car");
        payload.GetProperty("options").EnumerateArray().Select(o => o.GetString()).Should().Contain(["Delete", "Cancel"]);
        var step = payload.GetProperty("step").GetInt32();
        var po = payload.GetProperty("persistentObject").Clone();

        // 2. Fill the Confirmation attribute with the correct plate.
        var populated = PopulatePoConfirmation(po, plate);

        // 3. Retry with Option="Delete" — server validates Confirmation matches and deletes.
        var second = await adminClient.SendAsync(
            HttpMethod.Delete,
            DeleteUrl(created.Id!),
            JsonContent.Create(new { retryResults = new[] { new { step, option = "Delete", persistentObject = populated } } }),
            requiresAntiforgery: true);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"retry delete with correct plate must succeed — response: {await second.Content.ReadAsStringAsync()}");

        // 4. Confirm the car really is gone (admin would still see it via row-level rules;
        //    getting null confirms actual deletion vs row-level filtering).
        var refetch = await adminClient.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!);
        refetch.Should().BeNull("car must be gone after a confirmed delete");
    }

    [Fact]
    public async Task Delete_with_Cancel_leaves_the_car_intact()
    {
        using var adminClient = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);
        var plate = CarFixture.RandomLicensePlate("RC");
        var created = await adminClient.CreatePersistentObjectAsync(CarFixture.New(plate));

        var first = await adminClient.SendAsync(HttpMethod.Delete, DeleteUrl(created.Id!), requiresAntiforgery: true);
        ((int)first.StatusCode).Should().Be(449);
        var payload = await first.Content.ReadFromJsonAsync<JsonElement>();
        var step = payload.GetProperty("step").GetInt32();
        var po = payload.GetProperty("persistentObject").Clone();

        // Cancel — server returns NoContent but doesn't delete.
        var second = await adminClient.SendAsync(
            HttpMethod.Delete,
            DeleteUrl(created.Id!),
            JsonContent.Create(new { retryResults = new[] { new { step, option = "Cancel", persistentObject = po } } }),
            requiresAntiforgery: true);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refetch = await adminClient.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!);
        refetch.Should().NotBeNull("car must survive a Cancel");
    }

    [Fact]
    public async Task Delete_with_wrong_confirmation_refuses_the_delete_and_leaves_the_car()
    {
        using var adminClient = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);
        var plate = CarFixture.RandomLicensePlate("RW");
        var created = await adminClient.CreatePersistentObjectAsync(CarFixture.New(plate));

        var first = await adminClient.SendAsync(HttpMethod.Delete, DeleteUrl(created.Id!), requiresAntiforgery: true);
        ((int)first.StatusCode).Should().Be(449);
        var payload = await first.Content.ReadFromJsonAsync<JsonElement>();
        var step = payload.GetProperty("step").GetInt32();
        var po = payload.GetProperty("persistentObject").Clone();

        var populated = PopulatePoConfirmation(po, "WRONG-PLATE");
        var second = await adminClient.SendAsync(
            HttpMethod.Delete,
            DeleteUrl(created.Id!),
            JsonContent.Create(new { retryResults = new[] { new { step, option = "Delete", persistentObject = populated } } }),
            requiresAntiforgery: true);
        ((int)second.StatusCode).Should().BeGreaterOrEqualTo(400,
            "mismatched confirmation must refuse the delete (500 from InvalidOperationException)");

        var refetch = await adminClient.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!);
        refetch.Should().NotBeNull("car must survive a mismatched confirmation");
    }

    private static string DeleteUrl(string carId)
        => $"/spark/po/{CarFixture.TypeId}/{Uri.EscapeDataString(carId)}";

    /// <summary>
    /// Shallow-clones the scaffolded PersistentObject JSON, finds the <c>Confirmation</c>
    /// attribute, and sets its <c>value</c> / <c>isValueChanged</c>. Keeps every other
    /// field byte-equal so the server round-trips the rest of the metadata intact.
    /// </summary>
    private static JsonNode PopulatePoConfirmation(JsonElement scaffold, string confirmation)
    {
        var node = JsonNode.Parse(scaffold.GetRawText())!;
        var attributes = (JsonArray)node["attributes"]!;
        foreach (var attr in attributes)
        {
            var name = attr!["name"]?.GetValue<string>();
            if (name == "Confirmation")
            {
                attr["value"] = confirmation;
                attr["isValueChanged"] = true;
            }
        }
        return node;
    }

}
