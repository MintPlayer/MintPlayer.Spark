using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.ReferencePicker;

/// <summary>
/// Reference add-new PRD (D16, D17): on HR's create page for a Person, the Company picker's [+] opens a popup for a new
/// Company, prefilled with the search term; its Save stores the company and picks it, and the person, still unsaved
/// until then, is stored pointing at it.
/// </summary>
/// <remarks>
/// Driven through the browser on purpose: the server half (context, prefill, create) is covered by
/// <c>ReferenceAddNewEndpointTests</c>, and only the real client shows the stacked popups and the id handed back.
/// </remarks>
[Collection(HRE2ECollection.Name)]
public class ReferenceAddNewTests
{
    private const int Timeout = 15_000;

    private readonly HRE2ECollectionFixture _fixture;
    public ReferenceAddNewTests(HRE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Add_new_from_the_company_picker_on_a_create_page_then_save_the_person()
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

            await page.GotoAsync($"/po/{HRTestHost.PersonTypeId}/new");
            var firstName = page.Locator("input#FirstName");
            await firstName.WaitForAsync(new() { Timeout = Timeout });
            var surname = $"Doe{Guid.NewGuid():N}"[..11];
            await firstName.FillAsync("Jane");
            await page.Locator("input#LastName").FillAsync(surname);

            // Company is in the Career group on the second tab; on a create page the Jobs table has no rows, so the
            // form's only single picker is Company's.
            var tab = page.GetByText("Employment", new() { Exact = true }).First;
            await tab.WaitForAsync(new() { Timeout = Timeout });
            await tab.ClickAsync();
            var picker = page.Locator("spark-reference-picker").First;
            await picker.Locator("button").First.ClickAsync();

            var companyName = $"Initech {Guid.NewGuid():N}"[..16];
            var search = page.Locator(".reference-modal input[type='text']").First;
            await search.WaitForAsync(new() { Timeout = Timeout });
            await search.FillAsync(companyName);
            // Professions' tree-select has its own [+] in its (closed) panel; the picker modal's is the visible one.
            await page.Locator("button.spark-reference-add-new").Filter(new() { Visible = true }).ClickAsync();

            // The popup's form: Company's Name is prefilled with the term (D17); EmployeeCount is required. Person has
            // neither attribute, so the ids are the popup's (the modal may render outside its host element).
            var name = page.Locator("input#Name");
            await name.WaitForAsync(new() { Timeout = Timeout });
            await PollAsync(async () => await name.InputValueAsync() == companyName,
                async () => $"the popup's Name should hold the search term '{companyName}'; it holds '{await name.InputValueAsync()}'");
            await page.Locator("input#EmployeeCount").FillAsync("12");

            var created = page.WaitForResponseAsync(
                response => response.Request.Method == "POST" && response.Url.Contains("/po/create", StringComparison.Ordinal),
                new() { Timeout = Timeout });
            await page.Locator("button.spark-po-new-save").ClickAsync();
            var createResponse = await created;
            if (!createResponse.Ok)
                createResponse.Ok.Should().BeTrue($"the popup's save should succeed; /po/create answered {createResponse.Status}: {await createResponse.TextAsync()}");

            // The popup and the picker close, the new company is picked, and the person is still on /new.
            var pickerInput = picker.Locator("input").First;
            await PollAsync(async () => (await pickerInput.InputValueAsync()).Contains(companyName, StringComparison.Ordinal),
                async () => $"the picker should show the new company '{companyName}'; it shows '{await pickerInput.InputValueAsync()}'");
            page.Url.Should().EndWith("/new", "Add new must not save the person (the parent stays unsaved)");

            await page.Locator("spark-po-create button[type='submit'], spark-po-form button[type='submit']").First.ClickAsync();
            var deadline = DateTime.UtcNow.AddMilliseconds(Timeout);
            while (page.Url.EndsWith("/new", StringComparison.Ordinal))
            {
                if (DateTime.UtcNow > deadline)
                {
                    var body = await page.Locator("body").InnerTextAsync();
                    false.Should().BeTrue($"the person's create should succeed and leave /new; the page shows:\n{body}\n--- HR log tail ---\n{host.RecentLog()}");
                }
                await Task.Delay(100);
            }

            // /po/person/{id}/{slug} (#602): the id is the third segment, not the last.
            var personId = Uri.UnescapeDataString(new Uri(page.Url).AbsolutePath.Split('/')[3]);
            var person = await host.LoadAsync<StoredPerson>(personId);
            person.Should().NotBeNull($"the person '{personId}' must be stored");
            person!.LastName.Should().Be(surname);
            person.Company.Should().NotBeNullOrEmpty("the person points at the company created from the picker");

            var company = await host.LoadAsync<StoredCompany>(person.Company!);
            company.Should().NotBeNull($"the company '{person.Company}' must be stored by the popup's save");
            company!.Name.Should().Be(companyName, "the popup prefilled Name with the search term");
            company.EmployeeCount.Should().Be(12);
        }
        finally
        {
            await context.CloseAsync();
        }
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

    /// <summary>The stored shapes this test reads; HR's entity assembly is not referenced by the E2E project.</summary>
    private sealed class StoredPerson
    {
        public string? LastName { get; set; }
        public string? Company { get; set; }
    }

    private sealed class StoredCompany
    {
        public string? Name { get; set; }
        public int EmployeeCount { get; set; }
    }
}
