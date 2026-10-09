using MintPlayer.Spark.Authorization.Pages;
using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.IdentityProvider.Endpoints;
using MintPlayer.Spark.Testing;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// #462 D10: the <c>/connect</c> pages follow the Spark theme cookie on the server. An explicit
/// <c>light</c>/<c>dark</c> becomes <c>&lt;html data-bs-theme&gt;</c>; anything else renders no
/// attribute and leaves the page's <c>prefers-color-scheme</c> block in charge. The cookie is
/// attacker-controlled, so an unrecognised value must never reach the HTML.
/// </summary>
public class OidcPageThemeTests(OidcSharedHost host)
    : SparkSharedTestDriver(host), IClassFixture<OidcSharedHost>
{
    private const string MediaBlock = "@media (prefers-color-scheme: dark){:root:not([data-bs-theme=light])";

    private async Task<string> GetWithThemeCookieAsync(string url, string? cookieValue)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (cookieValue is not null)
            request.Headers.Add("Cookie", $"{ConnectPageTheme.CookieName}={cookieValue}");

        using var client = host.Factory.CreateClient();
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public static TheoryData<string> Pages => new() { "/connect/login?returnUrl=/", "/connect/two-factor?returnUrl=/" };

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task A_dark_cookie_renders_data_bs_theme_dark(string url)
    {
        var html = await GetWithThemeCookieAsync(url, "dark");
        html.Should().StartWith("<!DOCTYPE html><html lang=\"en\" data-bs-theme=\"dark\">");
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task A_light_cookie_renders_data_bs_theme_light(string url)
    {
        var html = await GetWithThemeCookieAsync(url, "light");
        html.Should().StartWith("<!DOCTYPE html><html lang=\"en\" data-bs-theme=\"light\">");
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task No_cookie_renders_no_attribute_and_the_media_block_follows_the_os(string url)
    {
        var html = await GetWithThemeCookieAsync(url, null);
        html.Should().StartWith("<!DOCTYPE html><html lang=\"en\">");
        html.Should().NotContain(" data-bs-theme=\"");
        html.Should().Contain(MediaBlock);
        html.Should().Contain(":root{color-scheme:light dark;");
    }

    [Fact]
    public async Task Auto_renders_no_attribute()
    {
        var html = await GetWithThemeCookieAsync("/connect/login?returnUrl=/", "auto");
        html.Should().StartWith("<!DOCTYPE html><html lang=\"en\">");
    }

    /// <summary>
    /// A value that fails validation, and a valid-shaped custom variant the pages have no
    /// stylesheet for: neither renders an attribute, and neither appears anywhere in the page.
    /// </summary>
    [Theory]
    [InlineData("x%22onload%3D%22alert(1)")]   // x"onload="alert(1)
    [InlineData("%3Cscript%3E")]               // <script>
    [InlineData("sepia-zz9")]
    [InlineData("DARK")]
    public async Task A_garbage_cookie_renders_no_attribute_and_is_not_echoed(string value)
    {
        var html = await GetWithThemeCookieAsync("/connect/login?returnUrl=/", value);
        html.Should().StartWith("<!DOCTYPE html><html lang=\"en\">");
        html.Should().NotContain(" data-bs-theme=\"");
        html.Should().NotContain(Uri.UnescapeDataString(value));
        html.Should().NotContain(value);
    }

    [Theory]
    [InlineData("light", "light")]
    [InlineData("dark", "dark")]
    [InlineData("auto", null)]
    [InlineData("sepia", null)]
    [InlineData("", null)]
    [InlineData("dark\"", null)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null)] // 33 characters
    public void ExplicitTheme_accepts_only_light_and_dark(string cookie, string? expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{ConnectPageTheme.CookieName}={cookie}";
        ConnectPageTheme.ExplicitTheme(context.Request).Should().Be(expected);
    }

    [Fact]
    public void ExplicitTheme_without_a_cookie_is_null()
        => ConnectPageTheme.ExplicitTheme(new DefaultHttpContext().Request).Should().BeNull();
}
