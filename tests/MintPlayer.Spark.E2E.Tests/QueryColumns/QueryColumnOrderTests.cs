using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.QueryColumns;

/// <summary>
/// #284 R9: a query's <c>columnOrder</c> decides its grid's columns, and every grid draws them: the main query
/// page, a detail page's sub-query card and the reference picker's popup.
/// </summary>
/// <remarks>
/// HR's model is the demo (<c>apps/HR/HR/App_Data/Model</c>):
/// <list type="bullet">
/// <item><description><c>GetPeople</c> has no list, so it draws the whole surface by <c>order</c>, Company included.</description></item>
/// <item><description><c>Company_People</c> lists <c>!Company</c>: on a company's page every row has the same company.</description></item>
/// <item><description><c>GetCompanies</c> moves Sector up to second place, and Person's Company picker runs that query,
/// so the popup must follow the list rather than the attributes' <c>order</c> (#284 D9).</description></item>
/// </list>
/// Seeded from <c>Data/hr-query-columns.json</c>: one company and two people working there.
/// </remarks>
[Collection(HRE2ECollection.Name)]
public class QueryColumnOrderTests : IAsyncLifetime
{
    private const int Timeout = 15_000;
    private static readonly string SeedFile = Path.Combine(AppContext.BaseDirectory, "QueryColumns", "Data", "hr-query-columns.json");

    /// <summary><c>GetPeople</c>'s id in <c>apps/HR/HR/App_Data/Model/Person.json</c>.</summary>
    private const string GetPeopleQueryId = "b20e8400-e29b-41d4-a716-446655440001";

    /// <summary>The <c>ObjectTypeId</c> of <c>Company</c> in <c>apps/HR/HR/App_Data/Model/Company.json</c>.</summary>
    private const string CompanyTypeId = "5a990917-387b-4f1e-8434-befc64da1958";

    private readonly HRE2ECollectionFixture _fixture;
    public QueryColumnOrderTests(HRE2ECollectionFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.Host.ImportFixturesAsync(SeedFile);
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_people_grid_shows_the_company_column()
    {
        await WithPageAsync(async page =>
        {
            await page.GotoAsync($"/query/{GetPeopleQueryId}");

            await ExpectHeadersAsync(page.Locator("thead").First,
                ["Full Name", "Email", "Company", "Address"],
                "GetPeople has no columnOrder, so it draws every Query attribute by order");
        });
    }

    [Fact]
    public async Task A_companys_people_card_hides_the_constant_company_column()
    {
        await WithPageAsync(async page =>
        {
            await page.GotoAsync($"/po/{CompanyTypeId}/{Uri.EscapeDataString("Companies/columns-works")}");

            await ExpectHeadersAsync(page.Locator("spark-query-card thead").First,
                ["Full Name", "Email", "Address"],
                "Company_People lists !Company: the column is hidden on the company's own page");
        });
    }

    [Fact]
    public async Task The_company_picker_draws_the_columns_in_the_queries_column_order()
    {
        await WithPageAsync(async page =>
        {
            await page.GotoAsync($"/po/{HRTestHost.PersonTypeId}/{Uri.EscapeDataString("People/columns-alpha")}/edit");

            // Company lives in the Career group on the second tab, "Employment".
            var tab = page.GetByText("Employment", new() { Exact = true }).First;
            await tab.WaitForAsync(new() { Timeout = Timeout });
            await tab.ClickAsync();

            var picker = page.Locator("spark-reference-picker", new() { Has = page.Locator("input#Company") });
            await picker.Locator("button").ClickAsync();
            var modal = page.Locator(".reference-modal");

            await ExpectHeadersAsync(modal.Locator("thead").First,
                ["Name", "Sector", "Employee Count", "Website", "Brand Color", "Accent Color"],
                "the popup draws the GetCompanies result's columns, in its columnOrder, not by the attributes' order");
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

    /// <summary>
    /// Polls the header row until it reads <paramref name="expected"/>. A header's first line is its label; the
    /// selection checkbox column and the row-actions column are not attributes and are skipped. The deadline is a
    /// failure bound, not a measurement.
    /// </summary>
    private static async Task ExpectHeadersAsync(ILocator thead, string[] expected, string because)
    {
        await thead.Locator("th").First.WaitForAsync(new() { Timeout = Timeout });

        var deadline = DateTime.UtcNow.AddMilliseconds(Timeout);
        string[] labels;
        while (!(labels = await LabelsAsync(thead)).SequenceEqual(expected))
        {
            if (DateTime.UtcNow > deadline)
                labels.Should().Equal(expected, because);
            await Task.Delay(100);
        }
    }

    private static async Task<string[]> LabelsAsync(ILocator thead)
    {
        // The row-actions column is the grid's own, not an attribute: it is labelled only by a
        // visually-hidden "Actions" span, so it is identified by that span and skipped.
        var headers = thead.Locator("th:not(.checkbox-cell)");
        var labels = new List<string>();
        for (var i = 0; i < await headers.CountAsync(); i++)
        {
            var header = headers.Nth(i);
            if (await header.Locator(".visually-hidden").CountAsync() > 0 && (await header.InnerTextAsync()).Trim() == "Actions")
                continue;
            var label = (await header.InnerTextAsync())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(label))
                labels.Add(label);
        }
        return [.. labels];
    }
}
