using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Account;

/// <summary>
/// <c>POST /spark/auth/resetPassword</c>, whenever accounts have passwords. Replaces Microsoft's; a
/// completed reset also confirms the email (D6). See <see cref="SparkAccount"/>.
/// </summary>
[MemberOf<SparkAuthGroup>]
internal sealed partial class ResetPassword<TUser> : IPostEndpoint<ResetPasswordRequest>
    where TUser : SparkUser, new()
{
    public static string Path => "/resetPassword";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => LocalCredentialMode.HasPasswords(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly UserManager<TUser> userManager;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => SparkAccount.BindFailed(context, failure);

    public override async Task<IResult> HandleAsync(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(request.Email);

        // One answer for "no such account" and "bad token", as Microsoft's does.
        if (user is null || SparkAccount.DecodeCode(request.ResetCode) is not { } code)
            return SparkAccount.Problem(IdentityResult.Failed(userManager.ErrorDescriber.InvalidToken()));

        var result = await userManager.ResetPasswordAsync(user, code, request.NewPassword);
        if (!result.Succeeded)
            return SparkAccount.Problem(result);

        // D6: the token was delivered to this address, so the address is proven.
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            var confirmed = await userManager.UpdateAsync(user);
            if (!confirmed.Succeeded)
                return SparkAccount.Problem(confirmed);
        }

        return TypedResults.Ok();
    }
}
