using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Settings;
using MintPlayer.Spark.Settings.Models;
using SeoTester.Settings;
using SeoTester.Tests._Infrastructure;

namespace SeoTester.Tests;

/// <summary>
/// <c>/bot</c> (<c>docs/seo_analyzer_PRD.md</c> F19, D17, D19): the page the crawler's user agent names. Anyone can
/// read it without JavaScript; it states the user agent, the current limits (the administrator's settings, not
/// copied numbers) and the robots.txt opt-out, the only one there is.
/// </summary>
[Collection(SeoTesterHostCollection.Name)]
public class BotPageTests(SeoTesterHostFixture host)
{
    [Fact]
    public async Task Anyone_reads_the_bot_page_as_server_rendered_html_with_the_user_agent_and_the_opt_out()
    {
        using var client = host.CreateClient();

        var response = await client.GetAsync("/bot");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        html.Should().Contain("MintPlayerSeoTester/1.0 (+https://seotester.mintplayer.com/bot)");
        html.Should().Contain("User-agent: MintPlayerSeoTester\nDisallow: /");
        html.Should().Contain("At most 100 pages and 10 minutes per check.");
        html.Should().Contain("at least 1 second between two requests");
        html.Should().Contain("at most once every 6 hours");
        html.Should().NotContain("<app-root", "the page is the server's, not the SPA's");
    }

    [Fact]
    public async Task The_page_states_the_limits_the_administrator_set()
    {
        var id = Setting.IdFor(SeoSettings.UnverifiedPagesPerRun.Key);
        await SetAsync(id, "250");
        try
        {
            using var client = host.CreateClient();

            var html = await client.GetStringAsync("/bot");

            html.Should().Contain("At most 250 pages");
        }
        finally
        {
            await SetAsync(id, null);
        }
    }

    [Fact]
    public async Task A_browser_that_prefers_Dutch_reads_it_in_Dutch()
    {
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/bot");
        request.Headers.AcceptLanguage.ParseAdd("nl-BE,nl;q=0.9,en;q=0.5");

        var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        html.Should().Contain("<html lang=\"nl\">");
        html.Should().Contain("Ten hoogste 100 pagina");
        html.Should().Contain("User-agent: MintPlayerSeoTester\nDisallow: /");
    }

    /// <summary>Stores a setting's value as an administrator's save does, and drops this instance's snapshot.</summary>
    private async Task SetAsync(string id, string? value)
    {
        using (var session = host.Database.OpenAsyncSession())
        {
            var setting = await session.LoadAsync<Setting>(id);
            setting.Value = value;
            await session.SaveChangesAsync();
        }
        host.Factory.Services.GetRequiredService<ISparkSettings>().Invalidate();
    }
}
