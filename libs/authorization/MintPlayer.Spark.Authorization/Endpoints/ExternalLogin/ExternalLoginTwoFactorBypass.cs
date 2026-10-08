using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.ExternalLogin;

/// <summary>The account's choice about the two-factor step after an external sign-in (#490 D11).</summary>
public sealed record ExternalLoginTwoFactorBypassResponse(bool Bypass, bool TwoFactorEnabled);

/// <summary>The body of <c>POST /spark/auth/manage/external-login-two-factor</c>.</summary>
public sealed record ExternalLoginTwoFactorBypassBody(bool Bypass, string? Code);

/// <summary>
/// <c>GET /spark/auth/manage/external-login-two-factor</c>: whether the signed-in user skips the application's
/// two-factor step after an external sign-in. Mapped only while <c>AllowUserBypass</c> is on (#490 D11).
/// </summary>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class ExternalLoginTwoFactorBypassState<TUser> : IGetEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/external-login-two-factor";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => ExternalLoginTwoFactor.Resolve(services) is { Enabled: true, AllowUserBypass: true };

    [Inject] private readonly UserManager<TUser> userManager;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (await userManager.GetUserAsync(httpContext.User) is not { } user)
            return TypedResults.Unauthorized();
        return TypedResults.Ok(new ExternalLoginTwoFactorBypassResponse(user.BypassTwoFactorForExternalLogin, user.TwoFactorEnabled));
    }
}

/// <summary>
/// <c>POST /spark/auth/manage/external-login-two-factor</c>: switches the skip on or off. Switching it on needs a
/// valid authenticator code, as in legacy MintPlayer (<c>Bypass2faForExternalLogin</c>): a stolen session alone must
/// not be able to weaken the account. Switching it off needs nothing.
/// </summary>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class SetExternalLoginTwoFactorBypass<TUser> : IPostEndpoint<ExternalLoginTwoFactorBypassBody>
    where TUser : SparkUser, new()
{
    public static string Path => "/external-login-two-factor";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => ExternalLoginTwoFactor.Resolve(services) is { Enabled: true, AllowUserBypass: true };

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(ExternalLoginTwoFactorBypassBody request, CancellationToken cancellationToken)
    {
        if (await userManager.GetUserAsync(httpContextAccessor.HttpContext!.User) is not { } user)
            return TypedResults.Unauthorized();

        if (request.Bypass)
        {
            if (!user.TwoFactorEnabled)
                return TypedResults.Problem("Two-factor authentication is not enabled on this account.", statusCode: StatusCodes.Status400BadRequest);
            var code = request.Code?.Replace(" ", "").Replace("-", "");
            if (string.IsNullOrEmpty(code)
                || !await userManager.VerifyTwoFactorTokenAsync(user, userManager.Options.Tokens.AuthenticatorTokenProvider, code))
                return TypedResults.Problem("The verification code is not valid.", statusCode: StatusCodes.Status400BadRequest);
        }

        user.BypassTwoFactorForExternalLogin = request.Bypass;
        var result = await userManager.UpdateAsync(user);
        return result.Succeeded
            ? TypedResults.Ok(new ExternalLoginTwoFactorBypassResponse(user.BypassTwoFactorForExternalLogin, user.TwoFactorEnabled))
            : TypedResults.Problem("The setting could not be saved.", statusCode: StatusCodes.Status500InternalServerError);
    }
}
