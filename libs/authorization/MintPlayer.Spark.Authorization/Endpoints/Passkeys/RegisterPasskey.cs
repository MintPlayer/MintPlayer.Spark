using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Passkeys;

/// <summary>
/// Completes an enrollment ceremony and stores the credential against the signed-in user.
/// </summary>
[MemberOf<SparkAuthGroup>]
internal sealed partial class RegisterPasskey<TUser> : IPostEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/passkeys";

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

        var request = await httpContext.Request.ReadFromJsonAsync<PasskeyRegistrationRequest>();
        if (request is null || string.IsNullOrWhiteSpace(request.CredentialJson))
            return PasskeyEndpoints.BadCeremonyInput();

        PasskeyAttestationResult attestation;
        try
        {
            attestation = await signInManager.PerformPasskeyAttestationAsync(request.CredentialJson);
        }
        catch (Exception ex) when (PasskeyEndpoints.IsCeremonyInputFailure(ex))
        {
            return PasskeyEndpoints.BadCeremonyInput();
        }

        if (!attestation.Succeeded)
            return PasskeyEndpoints.BadCeremonyInput();

        var passkey = attestation.Passkey;
        passkey.Name = PasskeyEndpoints.Sanitize(request.Name);

        try
        {
            var result = await userManager.AddOrUpdatePasskeyAsync(user, passkey);
            if (!result.Succeeded)
                return PasskeyEndpoints.BadCeremonyInput();
        }
        catch (InvalidOperationException)
        {
            // The store refuses a credential id already held by another account, and refuses
            // without saying by whom. Answering "already registered" here would undo that.
            return PasskeyEndpoints.BadCeremonyInput();
        }

        return Results.Ok(PasskeyEndpoints.ToSummary(passkey));
    }
}
