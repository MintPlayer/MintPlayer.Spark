using Microsoft.Playwright;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Visibility;

/// <summary>
/// #264, bug 1 (PRD §2.3/§8): Fleet's <c>Car.PoliceReportNumber</c> was <c>isVisible: false</c> in the model and
/// revealed by <c>CarActions.OnRefreshAsync</c> when the status became Stolen. The write gate treated the model's
/// <c>isVisible: false</c> as "not writable", so the number the user typed was posted, answered <c>null</c>, and
/// never stored.
/// </summary>
/// <remarks>
/// The second half is the load-time shape (G5/G7): a stolen car shows its police report number when it is
/// opened, without anyone touching the status first, and a car that is not stolen does not show the field.
/// </remarks>
[Collection(FleetE2ECollection.Name)]
public class StolenCarPoliceReportTests
{
    private const string PoliceReportNumber = "PoliceReportNumber";
    private const string Status = "Status";

    private readonly FleetE2ECollectionFixture _fixture;
    public StolenCarPoliceReportTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_stolen_cars_police_report_number_is_saved_and_stored()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);
        var created = await client.CreatePersistentObjectAsync(CarFixture.New(CarFixture.RandomLicensePlate("PR")));

        var car = await client.GetPersistentObjectAsync(CarFixture.TypeId, created.Id!)
            ?? throw new InvalidOperationException($"Car {created.Id} not re-fetchable");
        var report = $"PV-264-{Guid.NewGuid():N}"[..14];
        Set(car, Status, "Stolen");
        Set(car, PoliceReportNumber, report);

        var saved = await client.UpdatePersistentObjectAsync(car, onRetry: AnswerStolenPrompts);

        Value(saved, PoliceReportNumber).Should().Be(report,
            $"the save response must carry the number that was posted\n--- Fleet log tail ---\n{_fixture.Host.RecentLog()}");

        var stored = await _fixture.Host.LoadAsync<StoredCar>(created.Id!);
        stored.Should().NotBeNull();
        stored!.Status.Should().Be("Stolen");
        stored.PoliceReportNumber.Should().Be(report, "the police report number typed for a stolen car must be stored");
    }

    /// <summary>
    /// ⚠️ Two page loads, not more: Fleet's rate limiter is shared by every test in this collection (see
    /// <c>ViewerTimezoneRenderingTests</c>), and each SPA boot costs a dozen <c>/spark</c> calls.
    /// </summary>
    [Fact]
    public async Task The_detail_page_shows_the_police_report_number_only_for_a_stolen_car()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);

        var stolenReport = $"PV-264-{Guid.NewGuid():N}"[..14];
        var stolenPlate = CarFixture.RandomLicensePlate("ST");
        var stolen = await CreateCarAsync(client, stolenPlate, "Stolen", stolenReport);

        // A recovered car keeps its old report number in storage; it is not drawn once the car is back in use.
        var recoveredPlate = CarFixture.RandomLicensePlate("RC");
        var recovered = await CreateCarAsync(client, recoveredPlate, "InUse", $"PV-264-{Guid.NewGuid():N}"[..14]);

        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();
        await BrowserSignIn.SignInAsync(page, _fixture.Host.FleetUrl, _fixture.Host.AdminEmailAddress, _fixture.Host.AdminPass);

        var stolenTerms = await OpenDetailAsync(page, stolen.Id!, stolenPlate);
        stolenTerms.Should().Contain(t => IsPoliceReportLabel(t),
            $"a stolen car shows its police report number on load; the page shows [{string.Join(" | ", stolenTerms)}]");
        (await page.Locator($"dd:has-text('{stolenReport}')").CountAsync()).Should().Be(1,
            "the stored police report number is the value shown");

        var recoveredTerms = await OpenDetailAsync(page, recovered.Id!, recoveredPlate);
        recoveredTerms.Should().NotContain(t => IsPoliceReportLabel(t),
            $"a car that is not stolen does not show the police report number; the page shows [{string.Join(" | ", recoveredTerms)}]");
    }

    // ---------------------------------------------------------------------------------

    private static bool IsPoliceReportLabel(string term)
        => term.Replace(" ", "").Contains(PoliceReportNumber, StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates a car with the status and police report number given, answering the "stolen" prompts.</summary>
    private static async Task<PersistentObject> CreateCarAsync(SparkClient client, string plate, string status, string report)
    {
        var template = CarFixture.New(plate);
        var car = new PersistentObject
        {
            Name = template.Name,
            ObjectTypeId = template.ObjectTypeId,
            Attributes =
            [
                .. template.Attributes,
                new PersistentObjectAttribute { Name = Status, Value = status },
                new PersistentObjectAttribute { Name = PoliceReportNumber, Value = report },
            ],
        };
        return await client.CreatePersistentObjectAsync(car, onRetry: AnswerStolenPrompts);
    }

    /// <summary>Opens the detail page of a car and returns the attribute labels (the <c>dt</c> texts) it draws.</summary>
    private static async Task<IReadOnlyList<string>> OpenDetailAsync(IPage page, string id, string plate)
    {
        await page.GotoAsync($"/po/{CarFixture.TypeId}/{Uri.EscapeDataString(id)}");
        await page.Locator($"dd:has-text('{plate}')").First.WaitForAsync(new() { Timeout = 15_000 });
        return (await page.Locator("dt").AllInnerTextsAsync()).Select(t => t.Trim()).ToList();
    }

    /// <summary>
    /// <c>CarActions.OnBeforeSaveAsync</c> asks twice when a car becomes stolen: confirm, then whether to notify
    /// the fleet managers. A save that only reaches one of them never stores anything.
    /// </summary>
    private static Task<RetryAnswer?> AnswerStolenPrompts(RetryActionPayload prompt, CancellationToken _)
        => Task.FromResult<RetryAnswer?>(prompt.Title switch
        {
            "Report vehicle as stolen" => RetryAnswer.Choose("Confirm"),
            "Notify fleet managers" => RetryAnswer.Choose("No, skip"),
            _ => null,
        });

    private static void Set(PersistentObject po, string name, object? value)
    {
        var attribute = po.Attributes.First(a => a.Name == name);
        attribute.Value = value;
        attribute.IsValueChanged = true;
    }

    private static string? Value(PersistentObject po, string name)
        => po.Attributes.FirstOrDefault(a => a.Name == name)?.Value?.ToString();

    /// <summary>The stored shape this test reads; Fleet's entity assembly is not referenced by the E2E project.</summary>
    private sealed class StoredCar
    {
        public string? Status { get; set; }
        public string? PoliceReportNumber { get; set; }
    }
}
