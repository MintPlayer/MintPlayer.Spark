using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Account;

/// <summary>
/// <c>POST /spark/auth/confirm-email</c>: the SPA posting back the query of a mailed link — a plain
/// confirmation, or an email change. Every mode: an external sign-up from a provider without a
/// verified-email signal is confirmed by mail (D7) whatever the local-credential mode.
/// </summary>
[MemberOf<SparkAuthGroup>]
internal sealed partial class ConfirmEmail<TUser> : IPostEndpoint<SparkConfirmEmailRequest>
    where TUser : SparkUser, new()
{
    public static string Path => "/confirm-email";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly IOptions<SparkAuthenticationOptions> options;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => SparkAccount.BindFailed(context, failure);

    public override async Task<IResult> HandleAsync(SparkConfirmEmailRequest request, CancellationToken cancellationToken)
    {
        var result = await SparkAccount.ConfirmAsync(userManager, options.Value, request.UserId, request.Code, request.ChangedEmail);
        return result.Succeeded ? TypedResults.Ok() : SparkAccount.Problem(result);
    }
}
