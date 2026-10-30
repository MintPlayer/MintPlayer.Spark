using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using MintPlayer.Spark.Prerendering;

namespace MintPlayer.Spark.Tests.Prerendering;

/// <summary>
/// The page a server render supplies data for is the URL the browser asked for, not <c>Request.Path</c>: outside
/// Development the SPA fallback rewrites that to <c>/index.csr.html</c> before the render, and every contributor
/// then supplied only the app-wide requests (docs/spark_ssr_PRD.md I28, found by the MintPlayer E2E tests).
/// </summary>
public class SparkPrerenderPageUrlTests
{
    private static DefaultHttpContext Rewritten(string rawTarget, string pathBase = "")
    {
        var context = new DefaultHttpContext();
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = rawTarget;
        context.Request.PathBase = pathBase;
        context.Request.Path = "/index.csr.html";
        return context;
    }

    [Fact]
    public void The_page_url_is_the_request_line_when_the_fallback_rewrote_the_path()
    {
        var (path, query) = SparkPrerenderContext.PageUrl(Rewritten("/po/song/Songs%2F23/a-sky-full-of-stars?tab=lyrics"));

        path.Value.Should().Be("/po/song/Songs%2F23/a-sky-full-of-stars", "%2F stays encoded, as Kestrel leaves it, so the id is one segment");
        query.Value.Should().Be("?tab=lyrics");
    }

    [Fact]
    public void The_rest_of_the_path_is_decoded_as_Kestrel_decodes_it()
    {
        var (path, _) = SparkPrerenderContext.PageUrl(Rewritten("/query/caf%C3%A9%20songs"));

        path.Value.Should().Be("/query/café songs");
    }

    [Fact]
    public void The_path_base_is_removed()
    {
        var (path, query) = SparkPrerenderContext.PageUrl(Rewritten("/app/home", pathBase: "/app"));

        path.Value.Should().Be("/home");
        query.Should().Be(QueryString.Empty);
    }
}
