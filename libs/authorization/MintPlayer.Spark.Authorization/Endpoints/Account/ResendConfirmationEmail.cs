using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Account;

/// <summary>
/// <c>POST /spark/auth/resendConfirmationEmail</c>, <see cref="SparkLocalCredentials.Full"/> only (the
/// sign-up's mail trigger). Replaces Microsoft's; see <see cref="SparkAccount"/>.
/// </summary>
[MemberOf<SparkAuthGroup>]
internal sealed partial class ResendConfirmationEmail<TUser> : IPostEndpoint<ResendConfirmationEmailRequest>
    where TUser : SparkUser, new()
{
    public static string Path => "/resendConfirmationEmail";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => LocalCredentialMode.IsFull(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly SparkAccountMail<TUser> accountMail;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => SparkAccount.BindFailed(context, failure);

    public override async Task<IResult> HandleAsync(ResendConfirmationEmailRequest request, CancellationToken cancellationToken)
    {
        if (await userManager.FindByEmailAsync(request.Email) is { EmailConfirmed: false } user)
            await accountMail.SendConfirmationAsync(httpContextAccessor.HttpContext!, user, request.Email);

        // Same answer whether or not the address belongs to anyone.
        return TypedResults.Ok();
    }
}
