using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Account;

/// <summary>
/// <c>POST /spark/auth/manage/password</c>: change the password, or set a first one on a social-only
/// account, whenever accounts have passwords.
/// </summary>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class SetPassword<TUser> : IPostEndpoint<SparkSetPasswordRequest>
    where TUser : SparkUser, new()
{
    public static string Path => "/password";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => LocalCredentialMode.HasPasswords(services);

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly SignInManager<TUser> signInManager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => SparkAccount.BindFailed(context, failure);

    public override async Task<IResult> HandleAsync(SparkSetPasswordRequest request, CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext!.User;
        if (await userManager.GetUserAsync(principal) is not { } user)
            return TypedResults.NotFound();

        if (string.IsNullOrEmpty(request.NewPassword))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["NewPasswordRequired"] = ["A new password is required."] });

        IdentityResult result;
        if (await userManager.HasPasswordAsync(user))
        {
            if (string.IsNullOrEmpty(request.CurrentPassword))
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["CurrentPasswordRequired"] = ["The current password is required to change it."] });

            result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        }
        else
        {
            // A social-only account adding its first password.
            result = await userManager.AddPasswordAsync(user, request.NewPassword);
        }

        if (!result.Succeeded)
            return SparkAccount.Problem(result);

        await SparkAccount.RefreshCookieAsync(signInManager, principal, user);
        return TypedResults.Ok();
    }
}
