using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Endpoints;

namespace MintPlayer.Spark.Challenges.Endpoints;

/// <summary>Public: anyone may ask for a challenge (that is the point of one).</summary>
internal class ChallengesGroup : IEndpointGroup
{
    public static string Prefix => "/spark/challenges";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) => group.AllowAnonymous();
}

/// <summary>
/// <c>GET /spark/challenges/offer?point=&amp;action=</c>: the challenge to show at a point (a <c>SparkChallengeOffer</c>).
/// Issues no cookie and writes nothing: an ALTCHA challenge is sealed and self-contained. A point that is not a known
/// point, or <c>admission</c>, is the standard refusal.
/// </summary>
[MemberOf<ChallengesGroup>]
internal sealed partial class OfferChallenge : IGetEndpoint
{
    public static string Path => "/offer";

    [Inject] private readonly ISparkHumanChallenges challenges;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var point = httpContext.Request.Query["point"].ToString();
        if (!SparkChallengePoints.All.Contains(point) || !challenges.IsConfigured)
            return addOn.Refusal(httpContext);
        var action = httpContext.Request.Query["action"].ToString() is { Length: > 0 and <= 200 } a ? a : null;
        var offer = await challenges.OfferAsync(point.ToLowerInvariant(), action, httpContext.RequestAborted);
        httpContext.Response.Headers.CacheControl = "no-store";
        return addOn.Envelope(offer, StatusCodes.Status200OK);
    }
}
