using System.Text.RegularExpressions;
using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.ReferencePicker;

/// <summary>
/// The reference picker's "…" popup must reach every candidate, not only the first page of the reference query
/// (<c>docs/default_index_PRD.md</c>, the note beside D10). HR's job rows pick a <c>Profession</c> through
/// <c>GetProfessions</c>; with more professions than one page, the popup used to page and search only the 50 rows
/// the form pre-loaded, so "Profession 080" could be neither found nor paged to.
/// </summary>
/// <remarks>
/// Seeded from <c>Data/hr-reference-picker.json</c>: 100 professions named "Profession 001".."Profession 100"
/// (zero-padded so <c>GetProfessions</c>' Description sort puts 080 and 090 well past the 50th row) and three people,
/// each with one job row. Reseeded before every test, which resets the rows the picking tests change.
/// </remarks>
[Collection(HRE2ECollection.Name)]
public class ReferencePickerPagingTests : IAsyncLifetime
{
    private const int Timeout = 15_000;
    private static readonly string SeedFile = Path.Combine(AppContext.BaseDirectory, "ReferencePicker", "Data", "hr-reference-picker.json");

    private readonly HRE2ECollectionFixture _fixture;
    public ReferencePickerPagingTests(HRE2ECollectionFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.Host.ImportFixturesAsync(SeedFile);
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Guard for D10: the label of a stored reference comes from the server, not from the picker's options, so a
    /// value past the first page still shows its name on the detail and the edit page.
    /// </summary>
    [Fact]
    public async Task A_job_row_pointing_past_the_first_page_shows_the_profession_name_on_detail_and_edit()
    {
        await WithPageAsync(async page =>
        {
            const string personId = "People/picker-label";

            await ExpectDetailShowsAsync(page, personId, "Profession 080", "Professions/picker-080");
            await OpenEditAsync(page, personId);
            await ExpectPickerShowsAsync(page, "Profession 080",
                "the job row's picker shows the server-resolved name of a profession outside the options page");
        });
    }

    [Fact]
    public async Task The_picker_finds_a_profession_past_the_first_50_by_search_and_saves_it()
    {
        await WithPageAsync(async page =>
        {
            const string personId = "People/picker-search";

            await OpenEditAsync(page, personId);
            await ExpectPickerShowsAsync(page, "Profession 001", "the seeded job row points at Profession 001");
            var modal = await OpenPickerAsync(page);

            await modal.Locator("input[type='text']").First.FillAsync("Profession 080");
            await PollAsync(async () => await Row(modal, "Profession 080").CountAsync() > 0,
                async () => $"searching 'Profession 080' should list it (it sorts 80th of 101); the popup shows:\n{await RowsTextAsync(modal)}");

            await Row(modal, "Profession 080").First.ClickAsync();
            await ExpectSavedAndShownAsync(page, personId, "Professions/picker-080", "Profession 080");
        });
    }

    [Fact]
    public async Task The_picker_pages_to_a_profession_past_the_first_50_and_saves_it()
    {
        await WithPageAsync(async page =>
        {
            const string personId = "People/picker-paging";

            await OpenEditAsync(page, personId);
            await ExpectPickerShowsAsync(page, "Profession 001", "the seeded job row points at Profession 001");
            var modal = await OpenPickerAsync(page);
            await PollAsync(async () => await Row(modal, "Profession 001").CountAsync() > 0,
                async () => $"the popup's first page should list Profession 001; it shows:\n{await RowsTextAsync(modal)}");

            // Profession 090 is the 90th of 101 rows: page 9 at the popup's default 10 per page.
            var next = modal.Locator("mp-pagination.datatable-pagination button[aria-label='Next']");
            for (var pageNumber = 1; await Row(modal, "Profession 090").CountAsync() == 0; pageNumber++)
            {
                if (pageNumber > 15 || await next.IsDisabledAsync())
                    false.Should().BeTrue($"paging through the popup should reach Profession 090, but page {pageNumber} is the last; it shows:\n{await RowsTextAsync(modal)}");

                var firstRow = await FirstRowTextAsync(modal);
                await next.ClickAsync();
                await PollAsync(async () => await FirstRowTextAsync(modal) != firstRow,
                    async () => $"clicking Next on page {pageNumber} should show another page; it still shows:\n{await RowsTextAsync(modal)}");
            }

            await Row(modal, "Profession 090").First.ClickAsync();
            await ExpectSavedAndShownAsync(page, personId, "Professions/picker-090", "Profession 090");
        });
    }

    private async Task WithPageAsync(Func<IPage, Task> body)
    {
        var host = _fixture.Host;
        var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = host.AppUrl,
        });
        try
        {
            var page = await context.NewPageAsync();
            await BrowserSignIn.SignInAsync(page, host.AppUrl, host.AdminEmailAddress, host.AdminPass);
            await body(page);
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    private static string Encoded(string id) => Uri.EscapeDataString(id);

    private static async Task OpenEditAsync(IPage page, string personId)
    {
        await page.GotoAsync($"/po/{HRTestHost.PersonTypeId}/{Encoded(personId)}/edit");
        await ShowEmploymentTabAsync(page);
        await JobRowPicker(page).Locator("input").WaitForAsync(new() { Timeout = Timeout });
    }

    /// <summary>Person's Jobs live in the Career group on the second tab, "Employment".</summary>
    private static async Task ShowEmploymentTabAsync(IPage page)
    {
        var tab = page.GetByText("Employment", new() { Exact = true }).First;
        await tab.WaitForAsync(new() { Timeout = Timeout });
        await tab.ClickAsync();
    }

    /// <summary>The job row's Profession picker; the form's other picker (Company) is not in a table cell.</summary>
    private static ILocator JobRowPicker(IPage page) => page.Locator("td spark-reference-picker").First;

    private static async Task ExpectPickerShowsAsync(IPage page, string expected, string because)
    {
        var input = JobRowPicker(page).Locator("input");
        await PollAsync(async () => await input.InputValueAsync() == expected,
            async () => $"{because}: expected '{expected}', the picker shows '{await input.InputValueAsync()}'");
    }

    private static async Task<ILocator> OpenPickerAsync(IPage page)
    {
        await JobRowPicker(page).Locator("button").ClickAsync();
        var modal = page.Locator(".reference-modal");
        await modal.Locator("input[type='text']").First.WaitForAsync(new() { Timeout = Timeout });
        return modal;
    }

    /// <summary>A popup row whose cell reads exactly <paramref name="description"/> (the Description column).</summary>
    private static ILocator Row(ILocator modal, string description)
        => modal.Locator("tr", new() { Has = modal.Page.Locator("td", new() { HasTextRegex = new Regex($"^\\s*{Regex.Escape(description)}\\s*$") }) });

    private static async Task<string> FirstRowTextAsync(ILocator modal)
    {
        var cells = modal.Locator("td");
        return await cells.CountAsync() == 0 ? string.Empty : (await cells.First.InnerTextAsync()).Trim();
    }

    private static async Task<string> RowsTextAsync(ILocator modal)
    {
        var rows = await modal.Locator("tr").AllInnerTextsAsync();
        return rows.Count == 0 ? "(no rows)" : string.Join("\n", rows.Select(r => r.Trim()));
    }

    private async Task ExpectSavedAndShownAsync(IPage page, string personId, string professionId, string label)
    {
        await ExpectPickerShowsAsync(page, label, "the picked row's name replaces the old one in the textbox");

        var saved = page.WaitForResponseAsync(
            response => response.Request.Method == "POST" && response.Url.Contains("/po/update", StringComparison.Ordinal),
            new() { Timeout = Timeout });
        await page.Locator("spark-po-form button[type='submit']", new() { HasTextString = "Save" }).ClickAsync();
        var response = await saved;
        // The body is read only on failure: a successful save navigates to the detail page, after which the browser
        // no longer holds the body ("Response body is not available for a response that was navigated away from").
        // An interpolated `because` is built before the assertion runs, so it must not touch the body either.
        if (!response.Ok)
            response.Ok.Should().BeTrue($"the save should succeed; /po/update answered {response.Status}: {await response.TextAsync()}");

        var stored = await _fixture.Host.LoadAsync<StoredPerson>(personId);
        stored.Should().NotBeNull();
        stored!.Jobs.Should().HaveCount(1);
        stored.Jobs[0].ProfessionId.Should().Be(professionId, "the picked profession is the one stored");

        await ExpectDetailShowsAsync(page, personId, label, professionId);
        await OpenEditAsync(page, personId);
        await ExpectPickerShowsAsync(page, label, "after a reload the edit page labels the saved profession by name");
    }

    private static async Task ExpectDetailShowsAsync(IPage page, string personId, string label, string rawId)
    {
        await page.GotoAsync($"/po/{HRTestHost.PersonTypeId}/{Encoded(personId)}");
        await ShowEmploymentTabAsync(page);
        var body = page.Locator("body");
        await PollAsync(async () => (await body.InnerTextAsync()).Contains(label, StringComparison.Ordinal),
            async () => $"the detail page should show '{label}'; it shows:\n{await body.InnerTextAsync()}");
        (await body.InnerTextAsync()).Should().NotContain(rawId, "a reference is shown by name, never by id");
    }

    /// <summary>Polls a condition; the deadline is a failure bound, not a measurement.</summary>
    private static async Task PollAsync(Func<Task<bool>> condition, Func<Task<string>> failure)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Timeout);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                false.Should().BeTrue(await failure());
            await Task.Delay(100);
        }
    }

    /// <summary>The stored shape this test reads; HR's entity assembly is not referenced by the E2E project.</summary>
    private sealed class StoredPerson
    {
        public List<StoredJob> Jobs { get; set; } = [];
    }

    private sealed class StoredJob
    {
        public string? ProfessionId { get; set; }
    }
}
