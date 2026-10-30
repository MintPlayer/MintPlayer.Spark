using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Prerendering;
using MintPlayer.Spark.Boards.Services;

namespace MintPlayer.Spark.Boards.Endpoints;

/// <summary>Not <c>RequireAuthorization()</c>: an anonymous visitor reads the application's default board.</summary>
internal class BoardsGroup : IEndpointGroup
{
    public static string Prefix => "/spark/boards";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) { }
}

internal static class BoardResults
{
    public static IResult From(BoardSaveResult result) => result.Outcome switch
    {
        BoardSaveOutcome.Saved => TypedResults.Ok(result.Board),
        BoardSaveOutcome.NotFound => TypedResults.NotFound(),
        BoardSaveOutcome.Unauthorized => TypedResults.Unauthorized(),
        BoardSaveOutcome.Forbidden => TypedResults.Json(new { error = "You may not customize this board." }, statusCode: StatusCodes.Status403Forbidden),
        BoardSaveOutcome.Conflict => TypedResults.Json(new { error = result.Error }, statusCode: StatusCodes.Status409Conflict),
        _ => TypedResults.BadRequest(new { error = result.Error }),
    };
}

/// <summary><c>GET /spark/boards/{board}</c>: the caller's board, every tile they cannot see dropped. 404 for an undeclared board.</summary>
[MemberOf<BoardsGroup>]
internal sealed partial class GetBoard : IGetEndpoint
{
    public static string Path => "/{board}";

    // A read: server-side rendering may supply its response (SparkPrerenderableAttribute).
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(SparkPrerenderableAttribute.Instance);

    [RouteParam] public string Board { get; set; } = "";

    [Inject] private readonly SparkBoardService boards;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => await boards.GetAsync(Board, httpContext.RequestAborted) is { } board ? TypedResults.Ok(board) : TypedResults.NotFound();
}

/// <summary>The body of a save: the change vector it was read at (null for a first save) and the caller's edits.</summary>
public sealed class SaveBoardBody
{
    public string? ChangeVector { get; set; }
    public List<SparkBoardTile> Tiles { get; set; } = [];
    public List<string> Removed { get; set; } = [];
    public Dictionary<string, string> Titles { get; set; } = [];
    public Dictionary<int, List<SparkTileLayoutEntry>> Layouts { get; set; } = [];
}

/// <summary><c>PUT /spark/boards/{board}</c>: stores the caller's layout. 401/403/400, and 409 when it changed since it was read (D13).</summary>
[MemberOf<BoardsGroup>]
internal sealed partial class SaveBoard : IPutEndpoint<SaveBoardBody>
{
    public static string Path => "/{board}";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [RouteParam] public string Board { get; set; } = "";

    [Inject] private readonly SparkBoardService boards;

    public override async Task<IResult> HandleAsync(SaveBoardBody body, CancellationToken cancellationToken)
    {
        var request = new BoardMerge.SaveRequest
        {
            Tiles = body?.Tiles?.Where(t => t is not null).ToList() ?? [],
            Removed = body?.Removed?.Where(r => r is not null).ToList() ?? [],
            Titles = body?.Titles ?? [],
            Layouts = body?.Layouts ?? [],
        };
        return BoardResults.From(await boards.SaveAsync(Board, request, body?.ChangeVector, cancellationToken));
    }
}

/// <summary><c>DELETE /spark/boards/{board}</c>: forgets the caller's layout; answers the default board.</summary>
[MemberOf<BoardsGroup>]
internal sealed partial class ResetBoard : IDeleteEndpoint
{
    public static string Path => "/{board}";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [RouteParam] public string Board { get; set; } = "";

    [Inject] private readonly SparkBoardService boards;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
        => BoardResults.From(await boards.ResetAsync(Board, httpContext.RequestAborted));
}

/// <summary><c>GET /spark/boards/{board}/catalog</c>: what the caller may add. 401 anonymous, 403 without the right to customize.</summary>
[MemberOf<BoardsGroup>]
internal sealed partial class GetBoardCatalog : IGetEndpoint
{
    public static string Path => "/{board}/catalog";

    [RouteParam] public string Board { get; set; } = "";

    [Inject] private readonly SparkBoardService boards;
    [Inject] private readonly SparkBoardRegistry registry;
    [Inject] private readonly SparkBoardCatalogBuilder catalog;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (registry.Find(Board) is null)
            return TypedResults.NotFound();
        if (boards.UserId() is null)
            return TypedResults.Unauthorized();
        if (!await boards.CanCustomizeAsync(httpContext.RequestAborted))
            return BoardResults.From(new BoardSaveResult(BoardSaveOutcome.Forbidden));
        return TypedResults.Ok(await catalog.BuildAsync(httpContext.RequestAborted));
    }
}
