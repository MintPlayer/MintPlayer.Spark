using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Endpoints.Queries;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.SitemapXml;
using NSubstitute;

namespace MintPlayer.Spark.Tests.Prerendering;

/// <summary>
/// The sitemap's reader (docs/spark_ssr_PRD.md D17): one sitemap per page of each indexable type's list, read as an
/// anonymous visitor whoever asks (the caller is restored after), a list the visitor cannot read left out, and a row
/// whose <c>seo.indexableWhen</c> attribute is not true left out of its page.
/// </summary>
public class SparkSitemapReaderTests
{
    private readonly IModelLoader models = Substitute.For<IModelLoader>();
    private readonly IQueryLoader queries = Substitute.For<IQueryLoader>();
    private readonly IQueryReadPath readPath = Substitute.For<IQueryReadPath>();
    private readonly List<(ClaimsPrincipal? User, ExecuteQueryRequest Request)> reads = [];
    private HttpContext? current;

    private static readonly EntityTypeDefinition Song = new()
    {
        Id = Guid.NewGuid(), Name = "Song", ClrType = "Demo.Song", Alias = "song", Seo = new EntityTypeSeo { Indexable = true, IndexableWhen = "Public" },
    };
    private static readonly EntityTypeDefinition Tag = new() { Id = Guid.NewGuid(), Name = "Tag", ClrType = "Demo.Tag", Seo = new EntityTypeSeo { Indexable = true } };
    private static readonly EntityTypeDefinition Secret = new() { Id = Guid.NewGuid(), Name = "Secret", ClrType = "Demo.Secret" };
    private static readonly SparkQuery Songs = new() { Id = Guid.NewGuid(), Name = "Songs", Source = "Database.Songs" };
    private static readonly SparkQuery Tags = new() { Id = Guid.NewGuid(), Name = "Tags", Source = "Database.Tags" };

    public SparkSitemapReaderTests()
    {
        models.GetEntityTypes().Returns([Song, Tag, Secret]);
        queries.GetQueries().Returns([Songs, Tags]);
    }

    private SparkSitemapReader Reader()
    {
        var services = new ServiceCollection().AddSingleton(readPath).BuildServiceProvider();
        return new SparkSitemapReader(services.GetRequiredService<IServiceScopeFactory>(), models, queries, new SparkSitemapOptions());
    }

    private void Answer(SparkQuery query, Func<ExecuteQueryRequest, QueryResult?> answer)
        => readPath.ExecuteAsync(Arg.Is<ExecuteQueryRequest>(r => r.QueryId == query.Id.ToString()), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<ExecuteQueryRequest>();
                reads.Add((current?.User, request));
                return answer(request) is { } result ? QueryReadOutcome.Ok(result) : QueryReadOutcome.NotFound("no");
            });

    private static QueryResult Result(int total, params QueryResultItem[] items)
        => new() { Columns = [], Items = items, TotalItems = total, Skip = 0, Take = items.Length };

    private static QueryResultItem Row(string id, string breadcrumb, object? isPublic = null)
        // The href the query path stamps (#602): "Songs/1" of breadcrumb "One" is /po/song/Songs%2F1/one.
        => new() { Id = id, Breadcrumb = breadcrumb, Href = $"/po/{id.Split('/')[0].TrimEnd('s').ToLowerInvariant()}/{Uri.EscapeDataString(id)}/{breadcrumb.ToLowerInvariant()}", Values = isPublic is null ? [] : [new QueryResultItemValue { Key = "Public", Value = isPublic }] };

    private static string[] Locs(string xml)
        => System.Text.RegularExpressions.Regex.Matches(xml, "<loc>(.*?)</loc>").Select(m => m.Groups[1].Value).ToArray();

    private HttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("mintplayer.com");
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner")], "test"));
        current = context;
        return context;
    }

    [Fact]
    public async Task The_index_lists_one_sitemap_per_page_of_each_readable_indexable_list_by_its_alias_or_id()
    {
        Answer(Songs, _ => Result(SparkSitemapXml.PageSize + 1));
        Answer(Tags, _ => Result(3));
        var context = Context();
        var caller = context.User;

        var index = await Reader().IndexAsync(context);

        Locs(SparkSitemapXml.Write(index, null)).Should().Equal(
            "https://mintplayer.com/sitemap/song/1.xml",
            "https://mintplayer.com/sitemap/song/2.xml",
            $"https://mintplayer.com/sitemap/{Tag.Id}/1.xml");
        reads.Should().AllSatisfy(r => r.User!.Identity!.IsAuthenticated.Should().BeFalse());
        reads.Should().AllSatisfy(r => r.Request.Take.Should().Be(1));
        context.User.Should().BeSameAs(caller);
    }

    [Fact]
    public async Task A_list_the_visitor_cannot_read_is_left_out_of_the_index()
    {
        Answer(Songs, _ => null);
        Answer(Tags, _ => Result(0));

        var index = await Reader().IndexAsync(Context());

        Locs(SparkSitemapXml.Write(index, null)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_page_lists_the_rows_whose_indexable_attribute_is_true_at_their_canonical_links()
    {
        Answer(Songs, _ => Result(3, Row("Songs/1", "One", true), Row("Songs/2", "Two", false), Row("Songs/3", "Three")));

        var page = await Reader().PageAsync(Context(), "song", 2);

        Locs(SparkSitemapXml.Write(page!, null)).Should().Equal("https://mintplayer.com/po/song/Songs%2F1/one");
        reads.Should().ContainSingle().Which.Request.Skip.Should().Be(SparkSitemapXml.PageSize);
    }

    [Fact]
    public async Task A_type_without_an_indexable_attribute_lists_every_row()
    {
        Answer(Tags, _ => Result(2, Row("Tags/1", "a"), Row("Tags/2", "b")));

        var page = await Reader().PageAsync(Context(), Tag.Id.ToString(), 1);

        Locs(SparkSitemapXml.Write(page!, null)).Should().HaveCount(2);
    }

    [Fact]
    public async Task An_unknown_or_unindexable_type_or_an_unreadable_list_has_no_page()
    {
        Answer(Songs, _ => null);
        var reader = Reader();

        (await reader.PageAsync(Context(), "nope", 1)).Should().BeNull();
        (await reader.PageAsync(Context(), Secret.Id.ToString(), 1)).Should().BeNull();
        (await reader.PageAsync(Context(), "song", 1)).Should().BeNull();
    }
}
