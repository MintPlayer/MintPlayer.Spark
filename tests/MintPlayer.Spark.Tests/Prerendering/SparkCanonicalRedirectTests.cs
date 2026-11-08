using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Links;
using MintPlayer.Spark.Abstractions.Prerendering;
using MintPlayer.Spark.Prerendering;
using MintPlayer.Spark.Services;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Prerendering;

/// <summary>
/// The server answers a non-canonical object page with a 301 before Angular renders (#602): the request path against the
/// loaded object's <c>href</c>, only for a readable object, keeping the query string. And the redirect hook
/// (<see cref="ISparkEntityRedirect"/>) sends a natural-key URL to the object's page, or refuses it uniformly.
/// </summary>
public class SparkCanonicalRedirectTests
{
    private static readonly EntityTypeDefinition Song = new() { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "Song", Alias = "song" };
    private const string Canonical = "/po/song/Songs%2F1/halo";

    private static ISparkEntityLinks Links()
    {
        var models = Substitute.For<IModelLoader>();
        models.GetEntityTypes().Returns([Song]);
        models.GetEntityType(Song.Id).Returns(Song);
        return new SparkEntityLinks(models);
    }

    private static ISparkPrerenderContext Context(string path, string query = "", int loadStatus = 200)
    {
        var context = Substitute.For<ISparkPrerenderContext>();
        context.Path.Returns(new PathString(path));
        context.QueryString.Returns(new QueryString(query));
        context.GetAsync(Arg.Any<string>()).Returns(new SparkPrerenderResponse(200, "[]"));
        context.PostAsync(Arg.Any<string>(), Arg.Any<object?>()).Returns(new SparkPrerenderResponse(200, "{}"));
        context.PostAsync("/spark/po/load", Arg.Any<object?>()).Returns(loadStatus == 200
            ? new SparkPrerenderResponse(200, $$"""{"id":"Songs/1","breadcrumb":"Halo","href":"{{Canonical}}"}""")
            : new SparkPrerenderResponse(loadStatus, """{"error":"Authentication required"}"""));
        return context;
    }

    private static IHttpContextAccessor NoHttp()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        return accessor;
    }

    private static Task Run(ISparkPrerenderContext context)
        => new SparkRoutesPrerenderContributor(Links()).ContributeAsync(context);

    [Theory]
    [InlineData("/po/song/Songs%2F1")]
    [InlineData("/po/song/Songs%2F1/old-slug")]
    [InlineData("/po/11111111-1111-1111-1111-111111111111/Songs%2F1/halo")]
    public async Task A_non_canonical_path_is_a_301_to_the_href_keeping_the_query(string path)
    {
        var context = Context(path, "?lang=nl");

        await Run(context);

        context.Received(1).Redirect($"{Canonical}?lang=nl", true);
    }

    [Fact]
    public async Task The_canonical_path_is_rendered()
    {
        var context = Context(Canonical);

        await Run(context);

        context.DidNotReceiveWithAnyArgs().Redirect(default!, default);
    }

    [Theory]
    [InlineData("/po/song/Songs%2F1/edit")]
    [InlineData("/po/song/new")]
    public async Task The_edit_and_new_pages_are_never_redirected(string path)
    {
        var context = Context(path);

        await Run(context);

        context.DidNotReceiveWithAnyArgs().Redirect(default!, default);
    }

    [Fact]
    public async Task A_denied_or_missing_object_keeps_its_status_and_reveals_no_slug()
    {
        var context = Context("/po/song/Songs%2F1/wrong", loadStatus: 401);

        await Run(context);

        context.Received().SetPageStatus(401);
        context.DidNotReceiveWithAnyArgs().Redirect(default!, default);
    }

    private sealed class RepoRedirect(SparkEntityTarget? target) : ISparkEntityRedirect
    {
        public string Pattern => "{provider}/r/{owner}/{repo}";
        public IReadOnlyDictionary<string, string>? Values { get; private set; }

        public Task<SparkEntityTarget?> ResolveAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
        {
            Values = values;
            return Task.FromResult(target);
        }
    }

    [Fact]
    public async Task The_redirect_hook_sends_a_natural_key_url_to_the_objects_page()
    {
        var redirect = new RepoRedirect(new SparkEntityTarget("song", "Songs/1"));
        var context = Context("/GitHub/R/octo/repo", "?lang=nl");

        await new SparkEntityRedirectPrerenderContributor([redirect], NoHttp()).ContributeAsync(context);

        redirect.Values.Should().BeEquivalentTo(new Dictionary<string, string> { ["provider"] = "GitHub", ["owner"] = "octo", ["repo"] = "repo" });
        context.Received(1).Redirect($"{Canonical}?lang=nl", true);
    }

    [Fact]
    public async Task A_hook_miss_is_refused_without_a_redirect()
    {
        var context = Context("/github/r/octo/missing");

        await new SparkEntityRedirectPrerenderContributor([new RepoRedirect(null)], NoHttp()).ContributeAsync(context);

        context.Received().SetPageStatus(StatusCodes.Status404NotFound);
        context.DidNotReceiveWithAnyArgs().Redirect(default!, default);
    }

    [Fact]
    public async Task A_path_outside_the_pattern_is_left_alone()
    {
        var redirect = new RepoRedirect(new SparkEntityTarget("song", "Songs/1"));
        var context = Context("/github/r/octo");

        await new SparkEntityRedirectPrerenderContributor([redirect], NoHttp()).ContributeAsync(context);

        redirect.Values.Should().BeNull();
        context.DidNotReceiveWithAnyArgs().SetPageStatus(default);
        context.DidNotReceiveWithAnyArgs().Redirect(default!, default);
    }
}
