using System.Collections.Concurrent;
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
/// The pure parts of the sitemap (D17): the detail page of every object an anonymous visitor may read, of every type
/// whose model says <c>seo.indexable</c>. No per-type code: each type's list query, found as the <c>po/:type</c> page
/// finds it, runs through the ordinary read path (<see cref="SparkSitemapReader"/>), so row filters and rights apply as
/// they do to the page itself. An object of a type with <c>seo.indexableWhen</c> is listed only when that attribute is
/// true in its query row, the same rule that makes its page <c>noindex</c>.
/// </summary>
internal static class SparkSitemapXml
{
    /// <summary>Rows per sitemap page: the read path's largest page, so a sitemap page is one query.</summary>
    internal const int PageSize = QueryReadPath.MaxTake;

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
