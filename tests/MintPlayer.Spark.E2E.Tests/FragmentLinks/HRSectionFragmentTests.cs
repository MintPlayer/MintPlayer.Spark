using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.FragmentLinks;

/// <summary>
/// #721 (<c>docs/section_fragment_links_PRD.md</c> M6): a link's <c>#fragment</c> names a section of an object page, and
/// opening it shows that section. HR opts in (<c>withFragmentScrolling()</c> and the router's anchor scrolling, Dec10).
/// </summary>
/// <remarks>
/// Driven through the browser on purpose: the parts meet only in the real app. The server serves the ids (M1), ng-spark
/// renders them and opens the tab (M2), the router asks for the anchor, and Spark's scroller scrolls <c>#spark-main</c>,
/// not the window (M3). The viewport is short, so a section below the fold is reached only by scrolling.
/// Seeded from <c>QueryColumns/Data/hr-query-columns.json</c>: one company and two people working there.
/// </remarks>
[Collection(HRE2ECollection.Name)]
public class HRSectionFragmentTests : IAsyncLifetime
{
    private const int Timeout = 15_000;
    private static readonly string SeedFile = Path.Combine(AppContext.BaseDirectory, "QueryColumns", "Data", "hr-query-columns.json");

    /// <summary>The <c>ObjectTypeId</c> of <c>Company</c> in <c>apps/HR/HR/App_Data/Model/Company.json</c>.</summary>
    private const string CompanyTypeId = "5a990917-387b-4f1e-8434-befc64da1958";

    private readonly HRE2ECollectionFixture _fixture;
    public HRSectionFragmentTests(HRE2ECollectionFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.Host.ImportFixturesAsync(SeedFile);
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_group_fragment_opens_its_tab_and_shows_the_card()
    {
        await WithPageAsync(async page =>
        {
            // Career is a group on Person's second tab, Employment: the tab must open before the card can be shown.
            await page.GotoAsync($"/po/{HRTestHost.PersonTypeId}/{Uri.EscapeDataString("People/columns-alpha")}#career");

            var career = page.Locator("#career");
            await career.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = Timeout });
            (await page.Locator("#employment").EvaluateAsync<bool>("tab => tab.contains(document.getElementById('career'))"))
                .Should().BeTrue("the Employment tab's content carries the tab's id and holds the Career card");

            // The scroller marks the section it revealed for about 1.5 s (Dec5).
            await page.Locator("#career.spark-section-revealed").WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = Timeout });
            await ExpectInViewAsync(page, "career");
        });
    }

    [Fact]
    public async Task A_sub_query_fragment_scrolls_the_card_into_view()
    {
        await WithPageAsync(async page =>
        {
            // The People card's section id is its query's alias, company-people.
            await page.GotoAsync($"/po/{CompanyTypeId}/{Uri.EscapeDataString("Companies/columns-works")}#company-people");

            var people = page.Locator("spark-query-card#company-people");
            await people.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = Timeout });
            await page.Locator("#company-people.spark-section-revealed").WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = Timeout });

            var scrolled = await page.EvaluateAsync<double>("() => document.getElementById('spark-main').scrollTop");
            scrolled.Should().BeGreaterThan(0, "the card starts below the fold of a 400 px viewport, so the shell's scroll container scrolls to it");
            await ExpectInViewAsync(page, "company-people");
        });
    }

    /// <summary>
    /// The section's top is inside <c>#spark-main</c>'s visible box once the scroller has settled (it holds a section for up
    /// to 1 s without movement).
    /// </summary>
    private static async Task ExpectInViewAsync(IPage page, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var box = await page.EvaluateAsync<double[]>(
                """
                id => {
                  const main = document.getElementById('spark-main').getBoundingClientRect();
                  const section = document.getElementById(id).getBoundingClientRect();
                  return [section.top - main.top, main.height];
                }
                """, id);
            if (box[0] >= 0 && box[0] < box[1])
                return;
            if (DateTime.UtcNow > deadline)
            {
                false.Should().BeTrue($"#{id} should be scrolled into view: its top is {box[0]} px into #spark-main, whose height is {box[1]} px");
            }
            await Task.Delay(100);
        }
    }

    private async Task WithPageAsync(Func<IPage, Task> body)
    {
        var host = _fixture.Host;
        var context = await _fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = host.AppUrl,
            ViewportSize = new ViewportSize { Width = 1280, Height = 400 },
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
}
