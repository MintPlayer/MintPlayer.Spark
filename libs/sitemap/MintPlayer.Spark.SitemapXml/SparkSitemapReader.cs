using System.Security.Claims;
using MintPlayer.AspNetCore.SitemapXml.Abstractions.Data;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Endpoints.Queries;
using MintPlayer.Spark.Prerendering;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.SitemapXml;

/// <summary>
/// Reads the sitemap's lists (D17): each indexable type's list query runs through the ordinary read path, as an
/// anonymous visitor whoever asks, so a signed-in owner never sees their private objects in it. Each request reads one
/// page of one list, or each list's count for the index. A singleton: every read gets a scope of its own.
/// </summary>
internal sealed partial class SparkSitemapReader
{
    [Inject] private readonly IServiceScopeFactory scopes;
    [Inject] private readonly IModelLoader models;
    [Inject] private readonly IQueryLoader queries;
    [Inject] private readonly SparkSitemapOptions options;

    /// <summary>One sitemap per <see cref="SparkSitemapXml.PageSize"/> readable rows of each indexable type's list.</summary>
    public Task<SitemapIndex> IndexAsync(HttpContext context)
        => AsAnonymousAsync(context, async () =>
        {
            var origin = Origin(context);
            var sitemaps = new List<Sitemap>();
            foreach (var (type, query) in IndexableLists())
            {
                // The count only: a page of one row carries the total.
                var result = await ReadAsync(query, 0, 1, context.RequestAborted);
                if (result is null)
                    continue;
                var pages = (result.TotalItems + SparkSitemapXml.PageSize - 1) / SparkSitemapXml.PageSize;
                for (var page = 1; page <= pages; page++)
                    sitemaps.Add(new Sitemap { Loc = $"{origin}{options.PagePrefix}/{Uri.EscapeDataString(TypeSegment(type))}/{page}.xml" });
            }
            return new SitemapIndex(sitemaps);
        });

    /// <summary>One page of one type's list, or null for a type that is not indexable or a list the visitor cannot read.</summary>
    public Task<UrlSet?> PageAsync(HttpContext context, string typeSegment, int page)
        => AsAnonymousAsync(context, async () =>
        {
            var match = IndexableLists().FirstOrDefault(l => TypeSegment(l.Type) == typeSegment);
            if (match.Type is null)
                return null;
            var result = await ReadAsync(match.Query, (page - 1) * SparkSitemapXml.PageSize, SparkSitemapXml.PageSize, context.RequestAborted);
            if (result is null)
                return null;
            var origin = Origin(context);
            return new UrlSet(result.Items
                .Where(item => match.Type.Seo!.IndexableWhen is not { } attribute || SparkSitemapXml.IsTrue(item.Values.FirstOrDefault(v => v.Key == attribute)?.Value))
                .Select(item => new Url { Loc = SparkSitemapXml.DetailUrl(origin, typeSegment, item) }));
        });

    private IEnumerable<(EntityTypeDefinition Type, SparkQuery Query)> IndexableLists()
        => models.GetEntityTypes()
            .Where(t => t.Seo?.Indexable == true)
            .Select(t => (Type: t, Query: SparkSitemapXml.ListQuery(t, queries.GetQueries())))
            .Where(l => l.Query is not null)
            .Select(l => (l.Type, l.Query!));

    /// <summary>The type's segment of a page URL, as the app's links write it: the alias, else the id.</summary>
    private static string TypeSegment(EntityTypeDefinition type) => SparkEntityLinks.TypeSegment(type);

    private static string Origin(HttpContext context)
        => $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";

    /// <summary>In a scope of its own (rights and row filters are per request), as a visitor who is not signed in.</summary>
    private static async Task<T> AsAnonymousAsync<T>(HttpContext context, Func<Task<T>> read)
    {
        var caller = context.User;
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        try
        {
            return await read();
        }
        finally
        {
            context.User = caller;
        }
    }

    private async Task<QueryResult?> ReadAsync(SparkQuery query, int skip, int take, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<IQueryReadPath>().ExecuteAsync(new ExecuteQueryRequest
        {
            QueryId = query.Id.ToString(),
            Skip = skip,
            Take = take,
        }, cancellationToken);
        return outcome.Result;
    }
}
