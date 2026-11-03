using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Serialization;
using MintPlayer.AspNetCore.SitemapXml.Abstractions.Data;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Endpoints.Queries;
using MintPlayer.Spark.Prerendering;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.SitemapXml;

/// <summary>
/// The detail page of every object an anonymous visitor may read, of every type whose model says <c>seo.indexable</c>
/// (D17). No per-type code: each type's list query, found as the <c>po/:type</c> page finds it, runs through the
/// ordinary read path, so row filters and rights apply as they do to the page itself. An object of a type with
/// <c>seo.indexableWhen</c> is listed only when that attribute is true in its query row, the same rule that makes its
/// page <c>noindex</c>.
/// </summary>
internal static class SparkSitemapXml
{
    /// <summary>Rows per sitemap page: the read path's largest page, so a sitemap page is one query.</summary>
    internal const int PageSize = QueryReadPath.MaxTake;

    /// <summary>One sitemap per <see cref="PageSize"/> readable rows of each indexable type's list.</summary>
    internal static Task<SitemapIndex> IndexAsync(HttpContext context, IServiceScopeFactory scopes, IModelLoader models, IQueryLoader queries, Func<string, int, string> pageUrl, CancellationToken cancellationToken)
        => AsAnonymousAsync(context, async () =>
        {
            var sitemaps = new List<Sitemap>();
            foreach (var (type, query) in IndexableLists(models, queries))
            {
                // The count only: a page of one row carries the total.
                var result = await ReadAsync(scopes, query, 0, 1, cancellationToken);
                if (result is null)
                    continue;
                var pages = (result.TotalItems + PageSize - 1) / PageSize;
                for (var page = 1; page <= pages; page++)
                    sitemaps.Add(new Sitemap { Loc = pageUrl(TypeSegment(type), page) });
            }
            return new SitemapIndex(sitemaps);
        });

    /// <summary>One page of one type's list, or null for a type that is not indexable or a list the visitor cannot read.</summary>
    internal static Task<UrlSet?> PageAsync(HttpContext context, IServiceScopeFactory scopes, IModelLoader models, IQueryLoader queries, string origin, string typeSegment, int page, CancellationToken cancellationToken)
        => AsAnonymousAsync(context, async () =>
        {
            var match = IndexableLists(models, queries).FirstOrDefault(l => TypeSegment(l.Type) == typeSegment);
            if (match.Type is null)
                return null;
            var result = await ReadAsync(scopes, match.Query, (page - 1) * PageSize, PageSize, cancellationToken);
            if (result is null)
                return null;
            var segment = SparkRoutesPrerenderContributor.EncodeUriComponent(typeSegment);
            return new UrlSet(result.Items
                .Where(item => match.Type.Seo!.IndexableWhen is not { } attribute || IsTrue(item.Values.FirstOrDefault(v => v.Key == attribute)?.Value))
                .Select(item => new Url { Loc = DetailUrl(origin, segment, item) }));
        });

    private static IEnumerable<(EntityTypeDefinition Type, SparkQuery Query)> IndexableLists(IModelLoader models, IQueryLoader queries)
        => models.GetEntityTypes()
            .Where(t => t.Seo?.Indexable == true)
            .Select(t => (Type: t, Query: ListQuery(t, queries.GetQueries())))
            .Where(l => l.Query is not null)
            .Select(l => (l.Type, l.Query!));

    /// <summary>The type's segment of a page URL, as the app's links write it: the alias, else the id.</summary>
    private static string TypeSegment(EntityTypeDefinition type) => type.Alias ?? type.Id.ToString();

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

    private static async Task<QueryResult?> ReadAsync(IServiceScopeFactory scopes, SparkQuery query, int skip, int take, CancellationToken cancellationToken)
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

    /// <summary>
    /// The type's list query, as <c>po/:type</c> finds it (<c>SparkRoutesPrerenderContributor.SupplyTypeListAsync</c>,
    /// mirroring spark-query-list.component.ts): the query of that entity type, else the one whose source names the
    /// type or its plural.
    /// </summary>
    internal static SparkQuery? ListQuery(EntityTypeDefinition type, IEnumerable<SparkQuery> queries)
        => queries.FirstOrDefault(q =>
        {
            if (q.EntityType == type.Name) return true;
            var source = q.Source ?? "";
            var sourceName = source.Contains('.') ? source[(source.IndexOf('.') + 1)..] : source;
            return sourceName == type.Name || sourceName == type.Name + "s";
        });

    /// <summary>The page's canonical link (spark-po-detail <c>writeSeo</c>): <c>/po/:type/:id/:slug</c>, without the slug when it is empty or "edit".</summary>
    internal static string DetailUrl(string origin, string typeSegment, QueryResultItem item)
    {
        var slug = SparkRoutesPrerenderContributor.SparkSlug(item.Breadcrumb);
        var path = $"/po/{typeSegment}/{SparkRoutesPrerenderContributor.EncodeUriComponent(item.Id)}";
        return slug is "" or "edit" ? origin + path : $"{origin}{path}/{slug}";
    }

    /// <summary>A row value that is true: a <see cref="bool"/>, or a JSON <c>true</c>. Anything else, absence included, is not.</summary>
    internal static bool IsTrue(object? value) => value switch
    {
        bool b => b,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        _ => false,
    };

    /// <summary>
    /// The document, with the stylesheet's processing instruction, as the package's MVC formatter writes it. Spark knows
    /// no modification date or change frequency per object, and the package's <c>lastmod</c> and <c>changefreq</c> are
    /// non-nullable, so they are left out rather than written as <c>0001-01-01</c>.
    /// </summary>
    internal static string Write<T>(T document, string? stylesheetUrl)
    {
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(new Utf8StringWriter(builder), new XmlWriterSettings { Indent = false }))
        {
            if (!string.IsNullOrEmpty(stylesheetUrl))
                writer.WriteProcessingInstruction("xml-stylesheet", $"type=\"text/xsl\" href=\"{stylesheetUrl}\"");
            var namespaces = new XmlSerializerNamespaces();
            namespaces.Add(string.Empty, string.Empty);
            Serializers.GetOrAdd(typeof(T), CreateSerializer).Serialize(writer, document, namespaces);
        }
        return builder.ToString();
    }

    // An XmlSerializer built with overrides compiles a new assembly every time, so each is built once.
    private static readonly ConcurrentDictionary<Type, XmlSerializer> Serializers = new();

    private static XmlSerializer CreateSerializer(Type type)
    {
        var ignore = new XmlAttributes { XmlIgnore = true };
        var overrides = new XmlAttributeOverrides();
        overrides.Add(typeof(Url), nameof(Url.LastMod), ignore);
        overrides.Add(typeof(Url), nameof(Url.ChangeFreq), ignore);
        overrides.Add(typeof(Sitemap), nameof(Sitemap.LastMod), ignore);
        return new XmlSerializer(type, overrides);
    }

    private sealed class Utf8StringWriter(StringBuilder builder) : StringWriter(builder)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
