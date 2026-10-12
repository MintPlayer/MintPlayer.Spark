using System.Security.Claims;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authentication;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The default <see cref="ISparkCurrentUser"/>: the request principal's
/// <see cref="ClaimTypes.NameIdentifier"/>, else — for a bearer token this application issued itself —
/// its <c>sub</c>.
/// </summary>
/// <remarks>
/// <para>
/// Read on every access rather than captured, so a principal that changes during the request — a
/// sign-in endpoint that has just called <c>SignInAsync</c> and replaced <c>HttpContext.User</c>, or
/// <c>/mcp</c> swapping the cookie principal for the bearer one — is reported as it is now. No request
/// at all (a background job) is an anonymous caller.
/// </para>
/// <para>
/// <b>The <c>sub</c> fallback (MCP PRD D4, §4.6).</b> A resource-server principal keeps its claim types
/// verbatim (<c>MapInboundClaims = false</c>), so it carries <c>sub</c> and no <c>NameIdentifier</c>, and
/// this used to report <see langword="null"/> for every bearer caller — row security keyed on the user
/// id then failed closed for every agent. <c>sub</c> is read only when the scheme that authenticated the
/// request (<see cref="ISparkAuthenticatedSchemeFeature"/>) declares
/// <see cref="Abstractions.Builder.SparkCredentialScheme.SubjectIsLocalUserId"/>: a token from another
/// issuer names a user of that issuer, not of this application.
/// </para>
/// </remarks>
[Register(typeof(ISparkCurrentUser), ServiceLifetime.Scoped)]
internal partial class HttpContextSparkCurrentUser : ISparkCurrentUser
{
    /// <summary>The JWT subject claim, as a resource server without inbound claim mapping keeps it.</summary>
    internal const string SubjectClaim = "sub";

    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public string? Id
    {
        get
        {
            if (!IsAuthenticated)
                return null;

            if (Principal!.FindFirst(ClaimTypes.NameIdentifier)?.Value is { Length: > 0 } id)
                return id;

            var scheme = httpContextAccessor.HttpContext!.Features.Get<ISparkAuthenticatedSchemeFeature>()?.Scheme;
            return scheme is { SubjectIsLocalUserId: true }
                && Principal.FindFirst(SubjectClaim)?.Value is { Length: > 0 } subject
                    ? subject
                    : null;
        }
    }
}
