using MintPlayer.AspNetCore.Endpoints;

namespace MintPlayer;

/// <summary>
/// 301s from the legacy site's public URLs to the Spark pages (docs/mintplayer_website_PRD.md G6, D11). The ids
/// kept their legacy numbers, so <c>/song/313/sky-full-of-stars</c> is <c>/po/song/Songs%2F313/sky-full-of-stars</c>.
/// Only the show pages move:
/// the legacy <c>/edit</c> and <c>/sync</c> sub-paths are not mapped.
/// </summary>
internal static class LegacyRedirects
{
    public const string Subject = "{segment:regex(^(song|artist|person)$)}";

    private static readonly Dictionary<string, string> Collections = new() { ["song"] = "Songs", ["artist"] = "Artists", ["person"] = "People" };

    public static IResult To(HttpContext httpContext, string? name = null)
    {
        var segment = (string)httpContext.Request.RouteValues["segment"]!;
        var id = (string)httpContext.Request.RouteValues["id"]!;
        // The name becomes the page's slug; the page corrects it if the title has changed since.
        var slug = name is null ? "" : "/" + Uri.EscapeDataString(name);
        return Results.Redirect($"/po/{segment}/{Collections[segment]}%2F{id}{slug}", permanent: true);
    }
}

/// <summary><c>GET /song/313</c> (and <c>/artist</c>, <c>/person</c>).</summary>
internal sealed partial class LegacyShowEndpoint : IGetEndpoint
{
    public static string Path => $"/{LegacyRedirects.Subject}/{{id:int}}";

    public Task<IResult> HandleAsync(HttpContext httpContext) => Task.FromResult(LegacyRedirects.To(httpContext));
}

/// <summary><c>GET /song/313/sky-full-of-stars</c> (and <c>/artist</c>, <c>/person</c>).</summary>
internal sealed partial class LegacyShowWithNameEndpoint : IGetEndpoint
{
    public static string Path => $"/{LegacyRedirects.Subject}/{{id:int}}/{{name}}";

    public Task<IResult> HandleAsync(HttpContext httpContext) =>
        Task.FromResult(LegacyRedirects.To(httpContext, (string)httpContext.Request.RouteValues["name"]!));
}

/// <summary><c>GET /amp/song/313</c>: D11, AMP is dropped.</summary>
internal sealed partial class LegacyAmpSongEndpoint : IGetEndpoint
{
    public static string Path => "/amp/song/{id:int}";

    public Task<IResult> HandleAsync(HttpContext httpContext) =>
        Task.FromResult(Results.Redirect($"/po/song/Songs%2F{httpContext.Request.RouteValues["id"]}", permanent: true));
}
