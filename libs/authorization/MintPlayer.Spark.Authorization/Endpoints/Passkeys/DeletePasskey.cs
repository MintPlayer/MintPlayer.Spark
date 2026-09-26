using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Passkeys;

/// <summary>
/// Removes one of the signed-in user's enrolled passkeys, unless it is the last thing that can sign
/// the account in.
/// </summary>
[MemberOf<SparkAuthGroup>]
internal sealed partial class DeletePasskey<TUser> : IDeleteEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/passkeys/{id}";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder
            .RequireAuthorization()
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly IOptions<SparkAuthenticationOptions> options;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var user = await userManager.GetUserAsync(httpContext.User);
        if (user is null)
            return Results.Unauthorized();

        var id = httpContext.Request.RouteValues["id"]?.ToString();
        if (id is null || !PasskeyEndpoints.TryDecodeCredentialId(id, out var credentialId))
            return PasskeyEndpoints.BadCeremonyInput();

        if (await userManager.GetPasskeyAsync(user, credentialId) is null)
            return Results.NotFound();

        // Removing the last thing that can sign this account in locks the user out permanently,
        // and no amount of support undoes it. Same refusal shape the external-login unlink route
        // already returns, so the client's existing handling applies unchanged.
        var logins = await userManager.GetLoginsAsync(user);
        var passkeyList = await userManager.GetPasskeysAsync(user);
        if (SparkCredentialInventory.WouldRemoveLastPasskey(
                passkeyList.Count,
                logins.Count,
                await userManager.HasPasswordAsync(user),
                options.Value.LocalCredentials,
                options.Value.Passkeys))
        {
            return Results.BadRequest(new { success = false, error = "last_credential" });
        }

        var result = await userManager.RemovePasskeyAsync(user, credentialId);
        return result.Succeeded
            ? Results.Ok(new { success = true })
            : PasskeyEndpoints.BadCeremonyInput();
    }
}
