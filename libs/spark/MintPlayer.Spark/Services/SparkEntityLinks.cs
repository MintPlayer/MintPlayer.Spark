using System.Collections.Concurrent;
using System.Text;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Links;
using MintPlayer.Spark.Prerendering;

namespace MintPlayer.Spark.Services;

/// <summary>
/// How a type's list and an object's page are found (#602). <see cref="ISparkEntityLinks"/> is the only place an
/// object's URL is built: the payloads' <c>href</c>, the sitemap, search and the prerender's 301s all ask it, so they
/// can never disagree with each other or with the page.
/// </summary>
[Register(typeof(ISparkEntityLinks), ServiceLifetime.Singleton)]
internal sealed partial class SparkEntityLinks : ISparkEntityLinks
{
    [Inject] private readonly IModelLoader modelLoader;

    private const string IdToken = "{id}", TypeToken = "{type}", SlugToken = "{slug}";

    // A template is parsed once per distinct string; the model is reloaded in development, so the key is the text.
    private static readonly ConcurrentDictionary<string, string[]> Templates = new(StringComparer.Ordinal);

    /// <summary>
    /// The type's list query, as <c>po/:type</c> finds it (<c>SparkRoutesPrerenderContributor.SupplyTypeListAsync</c>,
    /// mirroring spark-query-list.component.ts): the query of that entity type, else the one whose source names the
    /// type or its plural.
    /// </summary>
    internal static SparkQuery? ListQuery(EntityTypeDefinition type, IEnumerable<SparkQuery> queries)
        => ListQuery(queries, type.Name, q => q.EntityType, q => q.Source);

    /// <summary>
    /// <see cref="ListQuery(EntityTypeDefinition, IEnumerable{SparkQuery})"/> over any query shape. A <c>Database.</c>
    /// source wins over a <c>Custom.</c> one, in file order otherwise: a custom query may be a sub-query that needs a
    /// parent (mintplayer.com, 2026-11-05: Song's <c>Custom.Artist_Songs</c> came first and every song hit threw).
    /// A custom query is still the list when it is the only match.
    /// </summary>
    internal static T? ListQuery<T>(IEnumerable<T> queries, string typeName, Func<T, string?> entityType, Func<T, string?> source)
        where T : class
        => queries
            .Where(q =>
            {
                if (entityType(q) == typeName) return true;
                var text = source(q) ?? "";
                var sourceName = text.Contains('.') ? text[(text.IndexOf('.') + 1)..] : text;
                return sourceName == typeName || sourceName == typeName + "s";
            })
            .OrderBy(q => (source(q) ?? "").StartsWith("Custom.", StringComparison.Ordinal) ? 1 : 0)
            .FirstOrDefault();

    /// <summary>The type's segment of a page URL, as the app's links write it: the alias, else the id.</summary>
    internal static string TypeSegmentOf(EntityTypeDefinition type) => type.Alias ?? type.Id.ToString();

    public string TypeSegment(EntityTypeDefinition type) => TypeSegmentOf(type);

    public string Slug(string? text) => SlugOf(text);

    /// <summary>
    /// Lower case, accents dropped, every run of other characters one dash. "Beyoncé - Halo!" → "beyonce-halo".
    /// The only implementation: the client never computes a slug (it links to <c>href</c>).
    /// </summary>
    internal static string SlugOf(string? text)
    {
        var slug = new StringBuilder();
        foreach (var c in (text ?? "").Normalize(NormalizationForm.FormD))
        {
            if (c is >= '̀' and <= 'ͯ')
                continue;
            var lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
                slug.Append(lower);
            else if (slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
        }
        return slug.ToString().TrimEnd('-');
    }

    public string DetailPath(EntityTypeDefinition type, string id, string? breadcrumb)
    {
        // "edit" would be the edit page's URL, so it is never a slug.
        var slug = type.Slug == false ? "" : SlugOf(breadcrumb);
        if (slug == "edit")
            slug = "";

        var template = Template(type);
        if (template is null)
        {
            var path = $"/po/{Encode(TypeSegmentOf(type))}/{Encode(id)}";
            return slug == "" ? path : $"{path}/{slug}";
        }

        var segments = new List<string>(template.Length);
        foreach (var part in template)
        {
            switch (part)
            {
                case IdToken: segments.Add(Encode(id)); break;
                case TypeToken: segments.Add(Encode(TypeSegmentOf(type))); break;
                case SlugToken: if (slug != "") segments.Add(slug); break;
                default: segments.Add(part); break;
            }
        }
        return "/" + string.Join('/', segments);
    }

    public string? DetailPath(PersistentObject obj)
    {
        if (string.IsNullOrEmpty(obj.Id))
            return null;
        var type = modelLoader.GetEntityType(obj.ObjectTypeId);
        return type is null ? null : DetailPath(type, obj.Id, obj.Breadcrumb);
    }

    public SparkEntityPathMatch? Match(string path)
    {
        // Each segment decoded once: a route parameter can hold an encoded '/'.
        var segments = path.Split('?', 2)[0].Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();

        // A type's own template wins over the default: it is the more specific declaration.
        foreach (var type in modelLoader.GetEntityTypes())
        {
            if (Template(type) is { } template && MatchTemplate(type, template, segments) is { } match)
                return match;
        }

        return segments switch
        {
            ["po", _, "new"] => null,
            ["po", _, _, "edit"] => null,
            ["po", var type, var id] => new SparkEntityPathMatch(type, id, null),
            ["po", var type, var id, var slug] => new SparkEntityPathMatch(type, id, slug),
            _ => null,
        };
    }

    private static SparkEntityPathMatch? MatchTemplate(EntityTypeDefinition type, string[] template, string[] segments)
    {
        var slugOptional = template[^1] == SlugToken;
        if (segments.Length != template.Length && !(slugOptional && segments.Length == template.Length - 1))
            return null;

        string? id = null, slug = null;
        for (var i = 0; i < segments.Length; i++)
        {
            var part = template[i];
            var segment = segments[i];
            switch (part)
            {
                case IdToken: id = segment; break;
                case SlugToken: slug = segment; break;
                case TypeToken:
                    if (!string.Equals(segment, TypeSegmentOf(type), StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(segment, type.Id.ToString(), StringComparison.OrdinalIgnoreCase))
                        return null;
                    break;
                default:
                    if (!string.Equals(segment, part, StringComparison.OrdinalIgnoreCase))
                        return null;
                    break;
            }
        }
        // "edit" is never a slug; a template's edit page, if any, is the app's own route.
        return id is null || slug == "edit" ? null : new SparkEntityPathMatch(TypeSegmentOf(type), id, slug);
    }

    /// <summary>The type's route template as segments, or null for the default URL. An invalid template throws, naming the type.</summary>
    internal static string[]? Template(EntityTypeDefinition type)
        => string.IsNullOrWhiteSpace(type.Route) ? null : Templates.GetOrAdd(type.Route, text => ParseTemplate(type.Name, text));

    internal static string[] ParseTemplate(string typeName, string text)
    {
        var segments = text.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        string Fail(string why) => throw new InvalidOperationException(
            $"The route template '{text}' of entity type '{typeName}' is invalid: {why}. " +
            "Use whole-segment placeholders {type}, {id} and {slug}, e.g. \"songs/{id}/{slug}\".");

        // A part-segment placeholder first: "songs/x{id}" lacks a whole {id} because of it, and saying so is the fix.
        foreach (var segment in segments)
        {
            if (segment is IdToken or TypeToken or SlugToken)
                continue;
            if (segment.Contains('{') || segment.Contains('}'))
                Fail($"the segment '{segment}' is not a whole placeholder");
        }
        if (segments.Count(s => s == IdToken) != 1)
            Fail("it must contain {id} exactly once");
        if (segments.Count(s => s == SlugToken) > 1 || (segments.Contains(SlugToken) && segments[^1] != SlugToken))
            Fail("{slug} may only be the last segment");
        if (segments[0] == "po" && segments.Length > 1 && segments[1] == TypeToken)
            Fail("it repeats the default URL; leave route empty instead");
        return segments;
    }

    private static string Encode(string value) => SparkRoutesPrerenderContributor.EncodeUriComponent(value);
}
