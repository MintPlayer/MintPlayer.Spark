using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Prerendering;

namespace MintPlayer.Spark.Tests.Prerendering;

/// <summary>
/// A server-rendered page's language (#604 D1): the URL's <c>?lang</c>, then the <c>spark-lang</c> cookie, each only when
/// it names a configured language. Null leaves it to <c>Accept-Language</c> and the default.
/// </summary>
public class SparkPageLanguageTests
{
    private static readonly string[] Languages = ["en", "nl", "fr"];

    private static string? Resolve(string query = "", string? cookie = null)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        if (cookie is not null)
            context.Request.Headers.Cookie = $"spark-lang={cookie}";
        return SparkPrerenderingService.ResolveLanguage(context, Languages);
    }

    [Fact]
    public void The_url_wins_over_the_cookie() => Resolve("?lang=nl", "fr").Should().Be("nl");

    [Fact]
    public void The_cookie_is_the_fallback() => Resolve(cookie: "fr").Should().Be("fr");

    [Fact]
    public void An_unknown_url_language_falls_back_to_the_cookie() => Resolve("?lang=xx", "fr").Should().Be("fr");

    [Fact]
    public void The_code_is_returned_as_configured() => Resolve("?lang=NL").Should().Be("nl");

    [Fact]
    public void Nothing_known_is_left_to_the_request_culture() => Resolve("?lang=", "de").Should().BeNull();
}
