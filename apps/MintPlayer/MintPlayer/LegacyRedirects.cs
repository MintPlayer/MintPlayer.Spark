namespace MintPlayer;

/// <summary>
/// 301s from the legacy site's public URLs to the Spark pages (docs/mintplayer_website_PRD.md G6, D11). The ids
/// kept their legacy numbers, so <c>/song/313/sky-full-of-stars</c> is <c>/po/song/Songs%2F313/sky-full-of-stars</c>.
/// Only the show pages move:
/// the legacy <c>/edit</c> and <c>/sync</c> sub-paths are not mapped.
/// </summary>
internal static class LegacyRedirects
{
    private static readonly (string Segment, string Collection)[] Subjects = [("song", "Songs"), ("artist", "Artists"), ("person", "People")];

    public static IEndpointRouteBuilder MapLegacyRedirects(this IEndpointRouteBuilder endpoints)
    {
        foreach (var (segment, collection) in Subjects)
        {
            // The name becomes the page's slug; the page corrects it if the title has changed since.
            endpoints.MapGet($"/{segment}/{{id:int}}", (int id) => Results.Redirect($"/po/{segment}/{collection}%2F{id}", permanent: true));
            endpoints.MapGet($"/{segment}/{{id:int}}/{{name}}", (int id, string name) =>
                Results.Redirect($"/po/{segment}/{collection}%2F{id}/{Uri.EscapeDataString(name)}", permanent: true));
        }

        // D11: AMP is dropped.
        endpoints.MapGet("/amp/song/{id:int}", (int id) => Results.Redirect($"/po/song/Songs%2F{id}", permanent: true));
        return endpoints;
    }
}
