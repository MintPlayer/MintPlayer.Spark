using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;
using static MintPlayer.Spark.E2E.Tests._Infrastructure.QnATestHost;

namespace MintPlayer.Spark.E2E.Tests.QnA;

/// <summary>
/// The SPA half of the wiring (#460 M12 UI): the vote widget renders in the questions grid — where a
/// row carries no type and the column's <c>rendererOptions.type</c> must supply it — and on the detail
/// page, and a click casts a real vote. Everything else is proven over the API in the other classes;
/// this is the one place a missing renderer registration or provider would show.
/// </summary>
[Collection(QnAE2ECollection.Name)]
public class QnABrowserTests
{
    private readonly QnAE2ECollectionFixture fixture;

    public QnABrowserTests(QnAE2ECollectionFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task The_vote_widget_in_the_grid_casts_a_vote_the_detail_page_shows()
    {
        var host = fixture.Host;
        using var author = await host.CreateUserAsync("ui-author");
        using var voter = await host.CreateUserAsync("ui-voter");
        var title = "Browser vote " + Guid.NewGuid().ToString("N")[..10];
        var question = await author.Client.AskAsync(title);
        await host.WaitForIndexingAsync();

        var context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = host.AppUrl,
        });
        try
        {
            var page = await context.NewPageAsync();
            await BrowserSignIn.SignInAsync(page, host.AppUrl, voter.Email, voter.Password);

            await page.GotoAsync("/query/questions");
            var search = page.Locator("input[type='search'], input[placeholder*='Search']").First;
            await search.WaitForAsync(new() { Timeout = 15_000 });
            await search.FillAsync(title);

            var row = page.Locator("tbody tr").Filter(new() { HasTextString = title }).First;
            await row.WaitForAsync(new() { Timeout = 15_000 });

            var up = row.Locator("spark-vote .spark-vote-up");
            await up.WaitForAsync(new() { Timeout = 15_000 });
            await up.ClickAsync();
            await row.Locator("spark-vote .spark-vote-up[aria-pressed='true']").WaitForAsync(new() { Timeout = 15_000 });
            (await row.Locator("spark-vote .spark-vote-score").InnerTextAsync()).Trim().Should().Be("1");

            (await voter.Client.ScoreAsync(QuestionTypeId, question.Id!)).Should().Be(1, "the click wrote a real vote");

            // The detail page reads the same state for the object's own type.
            await row.Locator("a").First.ClickAsync();
            var detailScore = page.Locator("spark-vote .spark-vote-score").First;
            await detailScore.WaitForAsync(new() { Timeout = 15_000 });
            (await detailScore.InnerTextAsync()).Trim().Should().Be("1");
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    [Fact]
    public async Task An_anonymous_visitor_stays_on_the_questions_page()
    {
        // The author cell's reputation badge asked /spark/moderation/reputation, which answered an
        // anonymous visitor 401, and ng-spark-auth's interceptor sent the whole app to /login.
        var host = fixture.Host;
        using var author = await host.CreateUserAsync("ui-anon-author");
        var title = "Anonymous read " + Guid.NewGuid().ToString("N")[..10];
        await author.Client.AskAsync(title);
        await host.WaitForIndexingAsync();

        var context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = host.AppUrl,
        });
        try
        {
            var page = await context.NewPageAsync();
            var unauthorized = new List<string>();
            page.Response += (_, response) =>
            {
                // /spark/auth/me answers a signed-out visitor 401 by design; the interceptor skips it.
                if (response.Status == 401 && !response.Url.Contains("/spark/auth/"))
                    lock (unauthorized) unauthorized.Add(response.Url);
            };

            await page.GotoAsync("/query/questions");
            var search = page.Locator("input[type='search'], input[placeholder*='Search']").First;
            await search.WaitForAsync(new() { Timeout = 15_000 });
            await search.FillAsync(title);
            var row = page.Locator("tbody tr").Filter(new() { HasTextString = title }).First;
            await row.WaitForAsync(new() { Timeout = 15_000 });
            // The author cell renders its badge; let every request it starts settle before judging.
            await row.Locator(".qna-author").WaitForAsync(new() { Timeout = 15_000 });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            new Uri(page.Url).AbsolutePath.Should().Be("/query/questions");
            (await row.IsVisibleAsync()).Should().BeTrue();
            unauthorized.Should().BeEmpty();
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}
