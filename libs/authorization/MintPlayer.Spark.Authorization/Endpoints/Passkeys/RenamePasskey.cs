using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Passkeys;

/// <summary>
/// Renames one of the signed-in user's enrolled passkeys.
/// </summary>
[MemberOf<SparkAuthGroup>]
internal sealed partial class RenamePasskey<TUser> : IPostEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/passkeys/{id}/name";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder)
    {
        builder
            .RequireAuthorization()
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    [Inject] private readonly UserManager<TUser> userManager;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var user = await userManager.GetUserAsync(httpContext.User);
        if (user is null)
            return Results.Unauthorized();

        var id = httpContext.Request.RouteValues["id"]?.ToString();
        if (id is null || !PasskeyEndpoints.TryDecodeCredentialId(id, out var credentialId))
            return PasskeyEndpoints.BadCeremonyInput();

        var passkey = await userManager.GetPasskeyAsync(user, credentialId);
        if (passkey is null)
            return Results.NotFound();

        // Read inside the guard — an uncaught JsonException from a missing or malformed body would
        // answer 500 rather than the uniform refusal this surface promises. See PasskeySignIn.
        PasskeyRenameRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<PasskeyRenameRequest>();
        }
        catch (Exception ex) when (PasskeyEndpoints.IsCeremonyInputFailure(ex))
        {
            return PasskeyEndpoints.BadCeremonyInput();
        }

        if (request is null)
            return PasskeyEndpoints.BadCeremonyInput();

        passkey.Name = PasskeyEndpoints.Sanitize(request.Name);
        var result = await userManager.AddOrUpdatePasskeyAsync(user, passkey);

        return result.Succeeded
            ? Results.Ok(PasskeyEndpoints.ToSummary(passkey))
            : PasskeyEndpoints.BadCeremonyInput();
    }
}
