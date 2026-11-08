namespace MintPlayer.Spark.Abstractions.Links;

/// <summary>
/// The one place an object's page URL is built (#602): the default <c>/po/{type}/{id}/{slug}</c>, an entity type's
/// <see cref="EntityTypeDefinition.Route"/> template, and the slug of its breadcrumb. The payloads' <c>href</c>, the
/// sitemap, search and the server's 301s all come from here, so they can never disagree.
/// </summary>
public interface ISparkEntityLinks
{
    /// <summary>The type's segment of a page URL: the alias, else the id.</summary>
    string TypeSegment(EntityTypeDefinition type);

    /// <summary>
    /// The URL slug of a text: lower case, accents dropped, every run of other characters one dash.
    /// "Beyoncé - Halo!" → "beyonce-halo". Empty for a text with no letters or digits.
    /// </summary>
    string Slug(string? text);

    /// <summary>
    /// The object's page path, encoded and with a leading slash, e.g. <c>/po/song/Songs%2F23/a-sky-full-of-stars</c>.
    /// No slug when the type turns slugs off (<see cref="EntityTypeDefinition.Slug"/>) or the slug is empty or "edit".
    /// </summary>
    string DetailPath(EntityTypeDefinition type, string id, string? breadcrumb);

    /// <summary>
    /// <see cref="DetailPath(EntityTypeDefinition, string, string?)"/> for a loaded object, or <see langword="null"/> when it has no page of its own: no id, or a
    /// type that is unknown or only ever embedded.
    /// </summary>
    string? DetailPath(PersistentObject obj);

    /// <summary>
    /// The object a page path names: a type's <see cref="EntityTypeDefinition.Route"/> template first, then the default
    /// <c>po/{type}/{id}[/{slug}]</c>. <c>po/{type}/new</c> and <c>po/{type}/{id}/edit</c> are not detail pages and never
    /// match. The id is decoded once; the type is the alias or id as it appears in the path.
    /// </summary>
    SparkEntityPathMatch? Match(string path);
}

/// <summary>What <see cref="ISparkEntityLinks.Match"/> found: the type segment and id of a detail page path, and its slug.</summary>
/// <param name="Type">The entity type as the path names it (alias or id), or the template's type.</param>
/// <param name="Id">The decoded object id.</param>
/// <param name="Slug">The path's slug segment, or <see langword="null"/> when it has none.</param>
public sealed record SparkEntityPathMatch(string Type, string Id, string? Slug);
