using Microsoft.Playwright;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests;

/// <summary>
/// Fleet's home page is the <c>home</c> board (#530, <c>App_Data/boards.json</c>): the default renders,
/// a signed-in user removes a tile and saves, the customization survives a reload, and Reset brings
/// the app's default back.
/// </summary>
[Collection(FleetE2ECollection.Name)]
public class BoardsTests
{
    private const int DefaultTileCount = 5;

    private readonly FleetE2ECollectionFixture _fixture;
    public BoardsTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_removed_tile_stays_removed_after_a_reload_and_Reset_restores_the_default()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();
        await BrowserSignIn.SignInAsync(page, _fixture.Host.FleetUrl, _fixture.Host.AdminEmailAddress, _fixture.Host.AdminPass);
        await page.GotoAsync("/home");
        await ResetThroughApiAsync(page);

        await page.ReloadAsync();
        var titles = page.Locator("spark-board .spark-tile-title");
        await titles.Nth(DefaultTileCount - 1).WaitForAsync(new() { Timeout = 20_000 });
        (await titles.CountAsync()).Should().Be(DefaultTileCount, "the default board ships five tiles");

        await page.Locator(".spark-board-customize").ClickAsync();
        await page.Locator(".spark-tile-remove").First.ClickAsync();
        await page.Locator(".spark-board-save").ClickAsync();
        // A save without a layout edit asks nothing; the toolbar leaves edit mode once it is stored.
        await page.Locator(".spark-board-reset").WaitForAsync(new() { Timeout = 10_000 });

        await page.ReloadAsync();
        await page.Locator(".spark-board-reset").WaitForAsync(new() { Timeout = 20_000 });
        (await titles.CountAsync()).Should().Be(DefaultTileCount - 1, "the removal is stored for this user");

        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await page.Locator(".spark-board-reset").ClickAsync();
        await titles.Nth(DefaultTileCount - 1).WaitForAsync(new() { Timeout = 10_000 });
        (await titles.CountAsync()).Should().Be(DefaultTileCount);
        (await page.Locator(".spark-board-reset").CountAsync()).Should().Be(0, "a reset board is no longer customized");
    }

    [Fact]
    public async Task An_anonymous_caller_may_not_customize_a_board()
    {
        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();
        await page.GotoAsync("/home");

        var response = await page.APIRequest.PutAsync($"{_fixture.Host.FleetUrl}/spark/boards/home", new()
        {
            Headers = new Dictionary<string, string> { ["X-XSRF-TOKEN"] = await XsrfTokenAsync(page) },
            DataObject = new { tiles = Array.Empty<object>(), removed = new[] { "recent-cars" }, titles = new { }, layouts = new { } },
        });

        response.Status.Should().Be(401);
    }

    /// <summary>Starts from the app default whatever an earlier run left behind.</summary>
    private async Task ResetThroughApiAsync(IPage page)
    {
        var response = await page.APIRequest.DeleteAsync($"{_fixture.Host.FleetUrl}/spark/boards/home", new()
        {
            Headers = new Dictionary<string, string> { ["X-XSRF-TOKEN"] = await XsrfTokenAsync(page) },
        });
        response.Ok.Should().BeTrue($"resetting the board answered {response.Status}");
    }

    private static async Task<string> XsrfTokenAsync(IPage page)
        => (await page.Context.CookiesAsync()).FirstOrDefault(c => c.Name == "XSRF-TOKEN")?.Value
           ?? throw new InvalidOperationException("No XSRF-TOKEN cookie after loading the SPA.");
}
