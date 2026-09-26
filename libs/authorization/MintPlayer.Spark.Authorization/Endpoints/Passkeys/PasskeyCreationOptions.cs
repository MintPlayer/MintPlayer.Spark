using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Passkeys;

/// <summary>
/// Starts an enrollment ceremony for the signed-in user.
/// </summary>
/// <remarks>
/// POST, not GET: this mutates server state by setting the ceremony cookie, and a GET would be
/// cacheable and navigable cross-site.
/// </remarks>
[MemberOf<SparkAuthGroup>]
internal sealed partial class PasskeyCreationOptions<TUser> : IPostEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/passkeys/creation-options";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder
            .RequireAuthorization()
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly SignInManager<TUser> signInManager;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var user = await userManager.GetUserAsync(httpContext.User);
        if (user is null)
            return Results.Unauthorized();

        var entity = new PasskeyUserEntity
        {
            Id = await userManager.GetUserIdAsync(user),
            Name = await userManager.GetUserNameAsync(user) ?? string.Empty,
            // Falls back to the account name rather than the email: this string is shown by the
            // authenticator's own UI and stored on the device, so it should be recognisable
            // without being more identifying than the account already is.
            DisplayName = await userManager.GetUserNameAsync(user) ?? string.Empty,
        };

        var optionsJson = await signInManager.MakePasskeyCreationOptionsAsync(entity);
        return Results.Text(optionsJson, "application/json");
    }
}
