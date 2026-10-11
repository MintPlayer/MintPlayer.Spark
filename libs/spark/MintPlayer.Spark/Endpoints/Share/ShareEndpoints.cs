using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Sharing;

namespace MintPlayer.Spark.Endpoints.Share;

[MemberOf<SparkGroup>]
internal class ShareGroup : IEndpointGroup
{
    public static string Prefix => "/share";

    // Anonymous by design: the token is the credential (X6).
    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services)
        => group.AllowAnonymous();
}

public sealed record SharedObjectRequest(string? Type, string? Id, string? Token);

public sealed record SharedQueryRequest(string? Type, string? Id, string? Token, string? Query, int? Skip, int? Take, SortColumn[]? SortColumns);

/// <summary>
/// <c>POST /spark/share/load</c>: the object behind a link, as the link shows it (its declared attributes and the
/// first page of each declared child query). Every refusal is the same 404 (#453): unknown type, id or token, a
/// revoked or replaced link, sharing switched off, an expired object.
/// </summary>
[MemberOf<ShareGroup>]
internal sealed partial class LoadSharedObject : IPostEndpoint<SharedObjectRequest>
{
    public static string Path => "/load";

    // A read that changes nothing, by an anonymous holder of a link: no ambient credential to forge.
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));

    [Inject] private readonly SparkShareLinks links;
    [Inject] private readonly SparkShareRateLimiter limiter;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(SharedObjectRequest request, CancellationToken cancellationToken)
    {
        var context = httpContextAccessor.HttpContext!;
        if (!limiter.TryAcquire(context))
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        var shared = await links.ReadAsync(request?.Type ?? "", request?.Id ?? "", request?.Token ?? "", context, cancellationToken);
        return shared is null ? Results.Json(new { error = "Not found" }, statusCode: StatusCodes.Status404NotFound) : Results.Json(shared);
    }
}

/// <summary><c>POST /spark/share/query</c>: another page or sort of one declared child query; the same 404 for every refusal.</summary>
[MemberOf<ShareGroup>]
internal sealed partial class QuerySharedObject : IPostEndpoint<SharedQueryRequest>
{
    public static string Path => "/query";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));

    [Inject] private readonly SparkShareLinks links;
    [Inject] private readonly SparkShareRateLimiter limiter;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(SharedQueryRequest request, CancellationToken cancellationToken)
    {
        var context = httpContextAccessor.HttpContext!;
        if (!limiter.TryAcquire(context))
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        var page = await links.QueryAsync(request?.Type ?? "", request?.Id ?? "", request?.Token ?? "", request?.Query ?? "",
            request?.Skip ?? 0, request?.Take ?? 25, request?.SortColumns, context, cancellationToken);
        return page is null ? Results.Json(new { error = "Not found" }, statusCode: StatusCodes.Status404NotFound) : Results.Json(page);
    }
}

/// <summary>Share reads per client address (<c>Spark:Share:RequestsPerMinute</c>), on top of any application rate limiter.</summary>
internal sealed class SparkShareRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> limiter;

    public SparkShareRateLimiter(IOptions<SparkShareOptions> options)
    {
        var permits = Math.Max(1, options.Value.RequestsPerMinute);
        limiter = PartitionedRateLimiter.Create<string, string>(key => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
    }

    public bool TryAcquire(HttpContext context)
    {
        using var lease = limiter.AttemptAcquire(context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        return lease.IsAcquired;
    }

    public void Dispose() => limiter.Dispose();
}
