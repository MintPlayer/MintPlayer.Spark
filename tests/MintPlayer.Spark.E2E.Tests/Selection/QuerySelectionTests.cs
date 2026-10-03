using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Selection;

/// <summary>
/// #467 in the browser, on Fleet's Car lists: checkboxes, the toolbar's built-in Edit and Delete, a
/// selection across pages, the lists that render no checkbox column, and the two concurrency answers
/// a user sees (D14 stale delete, D15 save of a row someone deleted).
/// </summary>
/// <remarks>
/// The sub-query card's selection is covered by <c>QnASubQueryTests</c>; this class covers a query
/// page. Every test scopes the list with a model name of its own, so the rows other tests in this
/// collection create never enter a count.
/// </remarks>
[Collection(FleetE2ECollection.Name)]
public class QuerySelectionTests
{
    private const string Registrations = "/query/registrations";
    private readonly FleetE2ECollectionFixture _fixture;
    public QuerySelectionTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task One_row_opens_in_Edit_and_two_rows_are_deleted_after_confirming_their_count()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);
        var model = NewModel();
        var ids = await CreateCarsAsync(client, model, 3);

        await using var pages = new PageFactory(_fixture);
        var page = await AdminPageAsync(pages);
        var dialogs = new List<string>();
        page.Dialog += (_, dialog) => { dialogs.Add(dialog.Message); _ = dialog.AcceptAsync(); };

        var rows = await OpenListAsync(page, Registrations, model, expectedRows: 3);

        // Nothing ticked: Edit (=1) and Delete (>0) are shown, disabled.
        (await ToolbarAction(page, "Edit").IsDisabledAsync()).Should().BeTrue("Edit needs exactly one row");
        (await ToolbarAction(page, "Delete").IsDisabledAsync()).Should().BeTrue("Delete needs a row");

        // ---- One row: Edit opens its edit page, and Back returns to the list.
        await Tick(rows, 0);
        await WaitForChipAsync(page, 1);
        (await ToolbarAction(page, "Edit").IsDisabledAsync()).Should().BeFalse();
        await ToolbarAction(page, "Edit").ClickAsync();
        await PollAsync(() => Task.FromResult(page.Url.Contains("/edit", StringComparison.Ordinal)),
            () => Task.FromResult($"Edit should open the edit page, the page is at {page.Url}"));
        ids.Should().Contain(id => page.Url.Contains(Uri.EscapeDataString(id), StringComparison.Ordinal),
            "the edit page is the ticked row's");

        await page.GoBackAsync();
        rows = await OpenListAsync(page, Registrations, model, expectedRows: 3, navigate: false);

        // ---- Two rows: Edit no longer applies, Delete does, and the confirmation names the count.
        // Whether Back restores the earlier tick is not what this test is about; start from none.
        var clear = page.Locator(".spark-selection-clear");
        if (await clear.IsVisibleAsync()) await clear.ClickAsync();
        await Tick(rows, 0);
        await Tick(rows, 1);
        await WaitForChipAsync(page, 2);
        (await ToolbarAction(page, "Edit").IsDisabledAsync()).Should().BeTrue("Edit is '=1'");
        (await ToolbarAction(page, "Delete").IsDisabledAsync()).Should().BeFalse("Delete is '>0'");

        await ToolbarAction(page, "Delete").ClickAsync();
        await WaitForRowsAsync(page, 1);
        dialogs.Should().ContainSingle().Which.Should().Contain("2", "the confirmation names how many rows go");

        (await ExistingAsync(client, ids)).Should().HaveCount(1, "the two ticked cars are deleted, the third is not");
    }

    [Fact]
    public async Task A_row_changed_after_the_list_loaded_is_not_deleted()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);
        var model = NewModel();
        var id = (await CreateCarsAsync(client, model, 1))[0];

        await using var pages = new PageFactory(_fixture);
        var page = await AdminPageAsync(pages);
        page.Dialog += (_, dialog) => _ = dialog.AcceptAsync();
        var rows = await OpenListAsync(page, Registrations, model, expectedRows: 1);

        // Someone else edits the car after this list loaded it (D14: the list's etag is now stale).
        var current = await client.GetPersistentObjectAsync(CarFixture.TypeId, id)
            ?? throw new InvalidOperationException($"Car {id} not loadable");
        var year = current.Attributes.First(a => a.Name == CarFixture.AttributeNames.Year);
        year.Value = 2023;
        year.IsValueChanged = true;
        await client.UpdatePersistentObjectAsync(current);

        await Tick(rows, 0);
        await WaitForChipAsync(page, 1);
        await ToolbarAction(page, "Delete").ClickAsync();

        var alert = page.Locator("spark-query-grid bs-alert");
        await alert.WaitForAsync(new() { Timeout = 15_000 });
        (await alert.InnerTextAsync()).Should().NotBeNullOrWhiteSpace("the 409 is explained, not swallowed");
        (await ExistingAsync(client, [id])).Should().ContainSingle("a row changed since it was listed is refused, not lost");
    }

    [Fact]
    public async Task A_selection_survives_paging_and_counts_the_rows_on_other_pages()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);
        var model = NewModel();
        await CreateCarsAsync(client, model, 11);

        await using var pages = new PageFactory(_fixture);
        var page = await AdminPageAsync(pages);
        await OpenListAsync(page, Registrations, model, expectedRows: 11);

        await page.Locator("mp-pagination.datatable-per-page button[aria-label='Page 10']").ClickAsync();
        var rows = await WaitForRowsAsync(page, 10);
        await Tick(rows, 0);
        await WaitForChipAsync(page, 1);

        await page.Locator("mp-pagination.datatable-pagination button[aria-label='Next']").ClickAsync();
        rows = await WaitForRowsAsync(page, 1);
        await Tick(rows, 0);
        await WaitForChipAsync(page, 2);

        var offPage = page.Locator(".spark-selection-off-page");
        await offPage.WaitForAsync(new() { Timeout = 15_000 });
        (await offPage.InnerTextAsync()).Should().Contain("1", "one ticked row is on the first page");
    }

    [Fact]
    public async Task A_query_declaring_selectionMode_none_renders_no_checkboxes()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);
        var model = NewModel();
        await CreateCarsAsync(client, model, 1); // Year 2024: on Recent_Cars

        await using var pages = new PageFactory(_fixture);
        var page = await AdminPageAsync(pages);

        // The same administrator gets checkboxes on Registrations, so their absence below is the
        // declaration's doing, not the user's rights.
        await OpenListAsync(page, Registrations, model, expectedRows: 1);
        (await page.Locator("thead th.checkbox-cell").CountAsync()).Should().Be(1);

        var rows = await OpenListAsync(page, "/query/recent-cars", model, expectedRows: 1);
        (await rows.First.Locator("td.checkbox-cell").CountAsync()).Should().Be(0, "Recent_Cars declares selectionMode: none");
        (await page.Locator("thead th.checkbox-cell").CountAsync()).Should().Be(0);
        (await page.Locator(".spark-selection-bar").CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_read_only_role_sees_no_checkboxes_and_no_Edit_or_Delete()
    {
        var email = $"viewer-{Guid.NewGuid():N}@e2e.local";
        await _fixture.Host.SeedUserAsync(email, _fixture.Host.AdminPass, "Viewers");

        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();
        await BrowserSignIn.SignInAsync(page, _fixture.Host.FleetUrl, email, _fixture.Host.AdminPass);

        // Row security shows a Viewer only cars they created, and they cannot create any, so the list
        // is empty: the header is what tells. An administrator's header carries the checkbox cell
        // (asserted in the selectionMode test above).
        await page.GotoAsync(Registrations);
        await page.Locator("thead th").First.WaitForAsync(new() { Timeout = 30_000 });
        (await page.Locator("thead th.checkbox-cell").CountAsync()).Should().Be(0, "a Viewer has no action that takes a row");
        (await page.Locator(".spark-selection-bar").CountAsync()).Should().Be(0);
        (await page.Locator(".spark-actionbar [data-action='Edit']").CountAsync()).Should().Be(0, "Viewers hold no Edit right");
        (await page.Locator(".spark-actionbar [data-action='Delete']").CountAsync()).Should().Be(0, "Viewers hold no Delete right");
    }

    [Fact]
    public async Task Saving_a_row_someone_else_deleted_says_so_and_does_not_recreate_it()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);
        var model = NewModel();
        var id = (await CreateCarsAsync(client, model, 1))[0];

        await using var pages = new PageFactory(_fixture);
        var page = await AdminPageAsync(pages);
        await page.GotoAsync($"/po/{CarFixture.TypeId}/{Uri.EscapeDataString(id)}/edit");
        var modelInput = page.Locator("input#Model");
        await modelInput.WaitForAsync(new() { Timeout = 15_000 });
        await PollAsync(async () => await modelInput.InputValueAsync() == model,
            async () => $"the edit form should load Model = {model}, it shows '{await modelInput.InputValueAsync()}'");

        await client.DeleteAsLoadedAsync(CarFixture.TypeId, id);

        await modelInput.FillAsync(model + "x");
        await page.Locator("button[type='submit']").ClickAsync();

        var alert = page.Locator("bs-alert").Filter(new() { HasTextRegex = new Regex("deleted this record") });
        await alert.WaitForAsync(new() { Timeout = 15_000 });
        page.Url.Should().Contain("/edit", "the form stays open with the user's changes");
        (await ExistingAsync(client, [id])).Should().BeEmpty("a save never resurrects a deleted row (D15)");
    }

    // ---------------------------------------------------------------------------------

    /// <summary>A model name no other test uses, so a search for it lists only this test's cars.</summary>
    private static string NewModel() => $"Sel{Guid.NewGuid():N}"[..11];

    private static async Task<List<string>> CreateCarsAsync(SparkClient client, string model, int count)
    {
        var ids = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var created = await client.CreatePersistentObjectAsync(CarFixture.New(CarFixture.RandomLicensePlate("SE"), model: model));
            ids.Add(created.Id ?? throw new InvalidOperationException("car create returned no id"));
        }
        return ids;
    }

    private async Task<IPage> AdminPageAsync(PageFactory pages)
    {
        var page = await pages.NewPageAsync();
        await BrowserSignIn.SignInAsync(page, _fixture.Host.FleetUrl, _fixture.Host.AdminEmailAddress, _fixture.Host.AdminPass);
        return page;
    }

    /// <summary>Opens a list (or reuses the one shown), searches for <paramref name="model"/> and waits for its rows.</summary>
    private static async Task<ILocator> OpenListAsync(IPage page, string route, string model, int expectedRows, bool navigate = true)
    {
        if (navigate) await page.GotoAsync(route);
        var search = page.Locator("spark-search-box input");
        await search.WaitForAsync(new() { Timeout = 30_000 });
        if (await search.InputValueAsync() != model) await search.FillAsync(model);
        return await WaitForRowsAsync(page, expectedRows);
    }

    private static async Task<ILocator> WaitForRowsAsync(IPage page, int count)
    {
        var rows = page.Locator("tbody tr[data-row-key]:not([data-placeholder='true'])");
        await PollAsync(async () => await rows.CountAsync() == count,
            async () => $"the list should show {count} row(s), it shows {await rows.CountAsync()}");
        return rows;
    }

    private static Task Tick(ILocator rows, int index) => rows.Nth(index).Locator("td.checkbox-cell mp-checkbox").ClickAsync();

    /// <summary>The visible toolbar button: <c>bs-priority-nav</c> stamps each item more than once (see <c>QnASubQueryTests.CardAction</c>).</summary>
    private static ILocator ToolbarAction(IPage page, string name)
        => page.Locator($".spark-actionbar [data-action='{name}']").Filter(new() { Visible = true });

    private static Task WaitForChipAsync(IPage page, int count)
        => page.Locator(".spark-selection-chip").Filter(new() { HasTextRegex = new Regex($@"^\s*{count}\b") })
            .WaitForAsync(new() { Timeout = 15_000 });

    private static async Task<List<string>> ExistingAsync(SparkClient client, IEnumerable<string> ids)
    {
        var existing = new List<string>();
        foreach (var id in ids)
        {
            try { if (await client.GetPersistentObjectAsync(CarFixture.TypeId, id) is not null) existing.Add(id); }
            catch (SparkClientException) { }
        }
        return existing;
    }

    /// <summary>Polls rather than waiting on a navigation event (see <c>ConcurrentEditConflictTests.PollAsync</c>).</summary>
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
