using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints;

/// <summary>
/// <c>GET /spark/auth/me</c>: the signed-in user as the store has it now.
/// </summary>
/// <remarks>
/// Read through <see cref="UserManager{TUser}"/>, not from the cookie's claims. The claims are a copy
/// taken at sign-in, so a user name or email changed on the profile page kept answering with the old
/// value until the user signed in again. A principal whose user no longer exists answers like an
/// anonymous caller.
/// <para>
/// Generic over <typeparamref name="TUser"/>, so the endpoint generator skips it (MPEP025) and
/// <c>MapSparkIdentityApi</c> maps it with the other <c>&lt;TUser&gt;</c> endpoints.
/// </para>
/// </remarks>
[MemberOf<SparkAuthGroup>]
internal sealed partial class GetCurrentUser<TUser> : IGetEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/me";

    [Inject] private readonly UserManager<TUser> userManager;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var user = httpContext.User.Identity?.IsAuthenticated == true
            ? await userManager.GetUserAsync(httpContext.User)
            : null;

        if (user is null)
            return Results.Ok(new { isAuthenticated = false });

        return Results.Ok(new
        {
            isAuthenticated = true,
            userName = await userManager.GetUserNameAsync(user),
            email = await userManager.GetEmailAsync(user),
            roles = await userManager.GetRolesAsync(user),
        });
    }
}
