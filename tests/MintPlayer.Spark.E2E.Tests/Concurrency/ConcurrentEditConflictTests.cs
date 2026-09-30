using Microsoft.Playwright;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Concurrency;

/// <summary>
/// Two browser contexts edit the same Fleet <c>Car</c> at once (contributions PRD §5, Q10; M1c).
/// </summary>
/// <remarks>
/// <para>
/// The ng-spark specs prove the merge and the page's handling of a 409 against a mocked service.
/// Only this proves the loop that matters to a user: the server really answers the second save with
/// 409, the page re-fetches, merges, <b>rebases onto the fresh change vector</b> — so the save after
/// that goes through instead of 409-ing forever, legacy MintPlayer's bug — and both people's edits
/// end up in the stored document.
/// </para>
/// <para>
/// ⚠️ Kept to one test on purpose: every browser boot spends a dozen <c>/spark</c> calls from the
/// rate-limit bucket all tests in this collection share (150 per 10 s on 127.0.0.1).
/// </para>
/// </remarks>
[Collection(FleetE2ECollection.Name)]
public class ConcurrentEditConflictTests
{
    private readonly FleetE2ECollectionFixture _fixture;
    public ConcurrentEditConflictTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Disjoint_edits_merge_and_a_same_field_edit_is_resolved_in_the_dialog()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);
        var created = await client.CreatePersistentObjectAsync(CarFixture.New(CarFixture.RandomLicensePlate("CC"), model: "M1", year: 2024));
        created.Id.Should().NotBeNullOrEmpty($"car create must succeed\n--- Fleet log tail ---\n{_fixture.Host.RecentLog()}");
        var id = created.Id!;

        await using var pages = new PageFactory(_fixture);
        var alice = await SignedInPageAsync(pages);
        var bob = await SignedInPageAsync(pages);

        // ---- 1. Disjoint fields: Alice changes Model, Bob (still on the old version) changes Year.
        await OpenEditAsync(alice, id, expectedModel: "M1");
        await OpenEditAsync(bob, id, expectedModel: "M1");

        await alice.Locator("input#Model").FillAsync("Alpha");
        await SaveAndWaitForDetailAsync(alice, "Alice's save is the first one and must succeed");

        await bob.Locator("input#Year").FillAsync("2025");
        await bob.Locator("button[type='submit']").ClickAsync();

        // The 409 was merged without a dialog, and the page says what the other person changed.
        var notice = bob.Locator(".spark-conflict-notice");
        await notice.WaitForAsync(new() { Timeout = 15_000 });
        (await notice.InnerTextAsync()).Should().Contain("Model", "the notice names the field the other user changed");
        (await bob.Locator("input#Model").InputValueAsync()).Should().Be("Alpha", "their change is merged into the form");
        (await bob.Locator("input#Year").InputValueAsync()).Should().Be("2025", "my change is kept");
        bob.Url.Should().Contain("/edit", "nothing is saved automatically after a merge");

        // Rebased onto the fresh change vector, so this save goes through.
        await SaveAndWaitForDetailAsync(bob, "the save after the merge carries the fresh etag and must not 409 again");

        var afterMerge = await ReloadAsync(client, id);
        Value(afterMerge, CarFixture.AttributeNames.Model).Should().Be("Alpha");
        Value(afterMerge, CarFixture.AttributeNames.Year).Should().Be("2025");

        // ---- 2. The same field: Alice sets Model to Bravo, Bob to Charlie, and Bob keeps his.
        await OpenEditAsync(alice, id, expectedModel: "Alpha");
        await OpenEditAsync(bob, id, expectedModel: "Alpha");

        await alice.Locator("input#Model").FillAsync("Bravo");
        await SaveAndWaitForDetailAsync(alice, "Alice's second save is again first and must succeed");

        await bob.Locator("input#Model").FillAsync("Charlie");
        await bob.Locator("button[type='submit']").ClickAsync();

        var dialog = bob.Locator(".spark-conflict-dialog");
        await dialog.WaitForAsync(new() { Timeout = 15_000 });
        (await dialog.Locator("tr.spark-conflict").CountAsync()).Should().Be(1, "only the true conflict is listed");
        var dialogText = await dialog.InnerTextAsync();
        dialogText.Should().Contain("Bravo", "theirs is shown");
        dialogText.Should().Contain("Charlie", "mine is shown");

        await dialog.Locator("tr.spark-conflict[data-path='Model'] input.spark-conflict-mine").CheckAsync();
        await bob.Locator(".spark-conflict-apply").ClickAsync();
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 15_000 });

        (await bob.Locator("input#Model").InputValueAsync()).Should().Be("Charlie", "my choice was applied");
        bob.Url.Should().Contain("/edit", "nothing is saved automatically after resolving");

        await SaveAndWaitForDetailAsync(bob, "the save after resolving carries the fresh etag and must not 409 again");

        var afterResolve = await ReloadAsync(client, id);
        Value(afterResolve, CarFixture.AttributeNames.Model).Should().Be("Charlie");
        Value(afterResolve, CarFixture.AttributeNames.Year).Should().Be("2025");
    }

    // ---------------------------------------------------------------------------------

    private async Task<IPage> SignedInPageAsync(PageFactory pages)
    {
        var page = await pages.NewPageAsync();
        await BrowserSignIn.SignInAsync(page, _fixture.Host.FleetUrl, _fixture.Host.AdminEmailAddress, _fixture.Host.AdminPass);
        return page;
    }

    private static async Task OpenEditAsync(IPage page, string id, string expectedModel)
    {
        await page.GotoAsync($"/po/{CarFixture.TypeId}/{Uri.EscapeDataString(id)}/edit");
        var model = page.Locator("input#Model");
        await model.WaitForAsync(new() { Timeout = 15_000 });
        // The form fills after the load resolves; a fill before that is overwritten.
        await PollAsync(async () => await model.InputValueAsync() == expectedModel,
            async () => $"the edit form should load Model = {expectedModel}, it shows '{await model.InputValueAsync()}'");
    }

    private static async Task SaveAndWaitForDetailAsync(IPage page, string because)
    {
        await page.Locator("button[type='submit']").ClickAsync();
        await PollAsync(() => Task.FromResult(!page.Url.Contains("/edit", StringComparison.Ordinal)),
            () => Task.FromResult($"{because}, but the page is still at {page.Url}"));
    }

    private static async Task<PersistentObject> ReloadAsync(MintPlayer.Spark.Client.SparkClient client, string id)
        => await client.GetPersistentObjectAsync(CarFixture.TypeId, id)
            ?? throw new InvalidOperationException($"Car {id} not re-fetchable");

    private static string? Value(PersistentObject po, string name)
        => po.Attributes.First(a => a.Name == name).Value?.ToString();

    /// <summary>
    /// Polls rather than waiting for a navigation event: the Angular router's same-document
    /// navigations were intermittently not seen by <c>WaitForURLAsync</c> under load (see
    /// <c>QnASubQueryTests.WaitForUrlAsync</c>). The deadline is a failure bound, not a measurement.
    /// </summary>
    private static async Task PollAsync(Func<Task<bool>> condition, Func<Task<string>> failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                var message = await failure();
                false.Should().BeTrue(message);
            }
            await Task.Delay(100);
        }
    }
}
