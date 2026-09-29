using System.Security.Claims;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The default <see cref="ISparkCurrentUser"/>: the request principal's
/// <see cref="ClaimTypes.NameIdentifier"/>.
/// </summary>
/// <remarks>
/// Read on every access rather than captured, so a principal that changes during the request — a
/// sign-in endpoint that has just called <c>SignInAsync</c> and replaced <c>HttpContext.User</c> —
/// is reported as it is now. No request at all (a background job) is an anonymous caller.
/// </remarks>
[Register(typeof(ISparkCurrentUser), ServiceLifetime.Scoped)]
internal partial class HttpContextSparkCurrentUser : ISparkCurrentUser
{
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public string? Id => IsAuthenticated
        ? Principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value is { Length: > 0 } id ? id : null
        : null;
}
