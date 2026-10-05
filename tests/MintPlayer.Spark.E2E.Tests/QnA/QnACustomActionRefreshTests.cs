using System.Text.Json.Nodes;
using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;
using static MintPlayer.Spark.E2E.Tests._Infrastructure.QnATestHost;

namespace MintPlayer.Spark.E2E.Tests.QnA;

/// <summary>
/// #319, in the browser: one click on a <c>refreshOnCompleted</c> custom action re-runs the detail
/// page's sub-query <b>once</b>, also when the action's server code emits a <c>refreshQuery</c> for
/// that query, by its alias or its id, in any case.
///
/// <para>
/// No QnA action emits <c>refreshQuery</c>, so the test adds one to the real <c>actions/execute</c>
/// response. Everything after the wire — dispatcher, refresh service, page, grid, datatable — is the
/// shipped code. Before the fix, the page bumped the grid again after its awaited re-fetch of the
/// question, and the grid fetched twice.
/// </para>
/// </summary>
/// <remarks>
/// Request budget (the shared rate-limit bucket): one browser session and two API calls per case.
/// </remarks>
[Collection(QnAE2ECollection.Name)]
public class QnACustomActionRefreshTests
{
    private const int Timeout = 15_000;
    private const string QueryExecute = "/spark/queries/execute";
    private readonly QnAE2ECollectionFixture fixture;

    public QnACustomActionRefreshTests(QnAE2ECollectionFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData(null)]
    [InlineData("question-answers")]
    [InlineData("4E13A000-0000-4000-8000-000000000001")]
    public async Task Closing_a_question_reruns_its_answers_once(string? serverRefreshKey)
    {
        var host = fixture.Host;
        // One account per case: the name is only unique within a run.
        using var author = await host.CreateUserAsync(serverRefreshKey switch
        {
            null => "refresh-plain",
            "question-answers" => "refresh-alias",
            _ => "refresh-id",
        });
        var question = await author.Client.AskAsync("Refresh once " + Guid.NewGuid().ToString("N")[..10]);
        await host.WaitForIndexingAsync();

        var context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            BaseURL = host.AppUrl,
            ViewportSize = new() { Width = 1600, Height = 1000 },
        });
        try
        {
            var page = await context.NewPageAsync();
            await BrowserSignIn.SignInAsync(page, host.AppUrl, author.Email, author.Password);

            if (serverRefreshKey is not null)
                await page.RouteAsync("**/spark/actions/execute", route => AddRefreshQueryAsync(route, serverRefreshKey));

            // The page, its answers grid included, has loaded and gone quiet before counting starts.
            var mounted = page.WaitForResponseAsync(r => r.Url.Contains(QueryExecute), new() { Timeout = Timeout });
            await page.GotoAsync($"/po/question/{Uri.EscapeDataString(question.Id!)}");
            await mounted;
            var close = page.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true });
            await close.WaitForAsync(new() { Timeout = Timeout });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            var executes = 0;
            page.Request += (_, request) =>
            {
                if (request.Method == "POST" && request.Url.Contains(QueryExecute))
                    Interlocked.Increment(ref executes);
            };

            var rerun = page.WaitForResponseAsync(r => r.Url.Contains(QueryExecute), new() { Timeout = Timeout });
            await close.ClickAsync();
            await rerun;
            await page.GetByRole(AriaRole.Button, new() { Name = "Reopen", Exact = true }).WaitForAsync(new() { Timeout = Timeout });
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            executes.Should().Be(1, "one click must re-run the answers query once, whatever refreshes the server asked for");
        }
        finally
        {
            await context.CloseAsync();
        }
    }

    /// <summary>Adds a <c>refreshQuery</c> operation to the real response, as an action calling <c>RefreshQuery</c> would.</summary>
    private static async Task AddRefreshQueryAsync(IRoute route, string queryId)
    {
        var response = await route.FetchAsync();
        var envelope = JsonNode.Parse(await response.TextAsync())!.AsObject();
        if (envelope["operations"] is not JsonArray operations)
            envelope["operations"] = operations = [];
        operations.Add(new JsonObject { ["type"] = "refreshQuery", ["queryId"] = queryId });

        await route.FulfillAsync(new() { Response = response, Body = envelope.ToJsonString() });
    }
}
