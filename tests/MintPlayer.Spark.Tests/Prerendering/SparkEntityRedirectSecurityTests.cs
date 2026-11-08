using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Links;
using MintPlayer.Spark.Abstractions.Prerendering;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Extensions;
using MintPlayer.Spark.Prerendering;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Prerendering;

// Fixture names start with "Er": ActionsResolver matches actions classes by simple name across the assembly.

public class ErRepo
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsPrivate { get; set; }
}

/// <summary>An anonymous visitor reads public repositories only: the row-level rule CodeCoverage's RepositoryActions applies.</summary>
public class ErRepoActions(IEntityMapper mapper)
    : DefaultPersistentObjectActions<ErRepo>(mapper), Abstractions.Authorization.ISparkOwnsRowSecurity
{
    public string RowSecurityRationale => "Test fixture: a private repository is hidden from every caller.";

    public override Task<bool> IsAllowedAsync(string action, ErRepo entity) => Task.FromResult(!entity.IsPrivate);

    public override Task<System.Linq.Expressions.Expression<Func<ErRepo, bool>>?> GetRowFilterAsync(string action)
        => Task.FromResult<System.Linq.Expressions.Expression<Func<ErRepo, bool>>?>(r => !r.IsPrivate);
}

public class ErContext : SparkContext
{
    public IRavenQueryable<ErRepo> Repos => Session.Query<ErRepo>();
}

/// <summary>
/// <c>/r/{name}</c>, looked up with NO authorization of its own: a raw session query that finds private repositories
/// too, as CodeCoverage's <c>AccountVanityRedirect</c> does. What protects a private repository is Spark's load.
/// </summary>
public sealed class ErRepoRedirect(IAsyncDocumentSession session) : ISparkEntityRedirect
{
    public string Pattern => "r/{name}";

    public async Task<SparkEntityTarget?> ResolveAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var name = values["name"];
        var repo = await session.Query<ErRepo>().Where(r => r.Name == name).FirstOrDefaultAsync(cancellationToken);
        return repo?.Id is { } id ? new SparkEntityTarget("errepo", id) : null;
    }
}

/// <summary>
/// A redirect hook never reveals a private object to an anonymous visitor (#602 review): through the real Spark
/// pipeline (security.json, the actions class's row rule, <c>/spark/po/load</c>), a private repository's vanity URL
/// answers exactly as a missing one does, with no <c>Location</c> and nothing of the repository in the page, while a
/// public one is a 301 to its page.
/// </summary>
public class SparkEntityRedirectSecurityTests : SparkTestDriver
{
    private static readonly Guid RepoTypeId = Guid.Parse("e7e00000-0000-4000-8000-00000000e001");

    private SparkEndpointFactory<ErContext> factory = null!;
    private SparkPrerenderDispatcher dispatcher = null!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        factory = new SparkEndpointFactory<ErContext>(
            Store,
            [RepoModel()],
            configureServices: services => services.AddScoped<ErRepoActions>(),
            configureSpark: spark => spark.AddPrerendering().AddEntityRedirect<ErRepoRedirect>(),
            security: SparkTestSecurity.Empty.Granting(["QueryRead/ErRepo"]));

        dispatcher = factory.GetService<SparkPrerenderDispatcher>();
        dispatcher.Attach(new ApplicationBuilder(factory.GetService<IServiceProvider>()));

        await SeedAsync(async session =>
        {
            await session.StoreAsync(new ErRepo { Name = "open-source" }, "errepos/1");
            await session.StoreAsync(new ErRepo { Name = "top-secret", IsPrivate = true }, "errepos/2");
        });
    }

    public override async Task DisposeAsync()
    {
        await factory.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task A_public_repository_is_a_301_to_its_page()
    {
        var page = await RenderAsync("/r/open-source");

        page.RedirectTo.Should().NotBeNull();
        page.RedirectTo!.Value.Permanent.Should().BeTrue();
        page.RedirectTo.Value.Location.Should().StartWith("/po/errepo/errepos%2F1");
    }

    [Fact]
    public async Task A_private_repository_answers_as_a_missing_one_with_no_redirect_and_nothing_of_it_in_the_page()
    {
        var secret = await RenderAsync("/r/top-secret");
        var missing = await RenderAsync("/r/no-such-repo");

        secret.RedirectTo.Should().BeNull("the Location header would name the private repository's id and slug");
        secret.PageStatus.Should().BeOneOf(StatusCodes.Status401Unauthorized, StatusCodes.Status404NotFound);
        secret.PageStatus.Should().Be(missing.PageStatus, "a private repository and a missing one are indistinguishable");

        var responses = System.Text.Json.JsonSerializer.Serialize(await secret.ResponsesAsync());
        responses.Should().NotContain("top-secret");
        responses.Should().NotContain("errepos/2");
    }

    /// <summary>The prerender step's contributors for one anonymous page request, as SparkPrerenderingService runs them.</summary>
    private async Task<SparkPrerenderContext> RenderAsync(string path)
    {
        using var scope = factory.GetService<IServiceProvider>().CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost");
        http.Request.Path = path;
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        var context = new SparkPrerenderContext(dispatcher, http, "en", "UTC");
        foreach (var contributor in scope.ServiceProvider.GetServices<ISparkPrerenderContributor>())
            await contributor.ContributeAsync(context);
        return context;
    }

    private static EntityTypeFile RepoModel() => new()
    {
        PersistentObject = new EntityTypeDefinition
        {
            Id = RepoTypeId,
            Name = "ErRepo",
            Alias = "errepo",
            ClrType = typeof(ErRepo).FullName!,
            Breadcrumb = "{Name}",
            Attributes =
            [
                new() { Id = Guid.NewGuid(), Name = "Name", DataType = "string", Order = 1 },
                new() { Id = Guid.NewGuid(), Name = "IsPrivate", DataType = "boolean", Order = 2 },
            ],
        },
    };
}
