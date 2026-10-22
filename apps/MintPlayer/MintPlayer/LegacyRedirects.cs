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

    public static string Target(string segment, string id, string? name = null)
    {
        // The name becomes the page's slug; the page corrects it if the title has changed since.
        var slug = name is null ? "" : "/" + Uri.EscapeDataString(name);
        return $"/po/{segment}/{Collections[segment]}%2F{id}{slug}";
    }
}

/// <summary><c>GET /song/313</c> (and <c>/artist</c>, <c>/person</c>).</summary>
internal sealed partial class LegacyShowEndpoint : IGetEndpoint
{
    public static string Path => $"/{LegacyRedirects.Subject}/{{id:int}}";

    [RouteParam] public string Segment { get; set; } = "";
    [RouteParam] public string Id { get; set; } = "";

    public Task<IResult> HandleAsync(HttpContext httpContext) =>
        Task.FromResult(Results.Redirect(LegacyRedirects.Target(Segment, Id), permanent: true));
}

/// <summary><c>GET /song/313/sky-full-of-stars</c> (and <c>/artist</c>, <c>/person</c>).</summary>
internal sealed partial class LegacyShowWithNameEndpoint : IGetEndpoint
{
    public static string Path => $"/{LegacyRedirects.Subject}/{{id:int}}/{{name}}";

    [RouteParam] public string Segment { get; set; } = "";
    [RouteParam] public string Id { get; set; } = "";
    [RouteParam] public string Name { get; set; } = "";

    public Task<IResult> HandleAsync(HttpContext httpContext) =>
        Task.FromResult(Results.Redirect(LegacyRedirects.Target(Segment, Id, Name), permanent: true));
}

/// <summary><c>GET /amp/song/313</c>: D11, AMP is dropped.</summary>
internal sealed partial class LegacyAmpSongEndpoint : IGetEndpoint
{
    public static string Path => "/amp/song/{id:int}";

    [RouteParam] public string Id { get; set; } = "";

    public Task<IResult> HandleAsync(HttpContext httpContext) =>
        Task.FromResult(Results.Redirect(LegacyRedirects.Target("song", Id), permanent: true));
}
