using Microsoft.AspNetCore.Identity;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Passkeys;

/// <summary>
/// Completes a discoverable-credential sign-in ceremony.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ Every rejection on this route answers <c>SignInFailed()</c>, including malformed input and a
/// missing ceremony. A 400 here and a 401 there would let a caller separate "your payload was junk"
/// from "that credential is unknown", which is the distinction D10 spent the username parameter to
/// remove.
/// </para>
/// <para>
/// No <c>Configure</c>: anonymous by design, same as the request-options route.
/// </para>
/// </remarks>
[MemberOf<SparkAuthGroup>]
internal sealed partial class PasskeySignIn<TUser> : IPostEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/passkeys/sign-in";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAuthFeatures.Passkeys(services);

    [Inject] private readonly SignInManager<TUser> signInManager;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        // ⚠️ The body read is inside the guard, not before it. Minimal-API binding used to turn a
        // missing or malformed body into a framework 400; reading it by hand throws instead, and an
        // uncaught JsonException here answers 500 with a stack trace — which is precisely the
        // failure IsCeremonyInputFailure was written to prevent, on precisely the route its remarks
        // name: a bare POST to the anonymous sign-in endpoint.
        PasskeySignInRequest? request;
        try
        {
            request = await httpContext.Request.ReadFromJsonAsync<PasskeySignInRequest>();
        }
        catch (Exception ex) when (PasskeyEndpoints.IsCeremonyInputFailure(ex))
        {
            return PasskeyEndpoints.SignInFailed();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.CredentialJson))
            return PasskeyEndpoints.SignInFailed();

        SignInResult result;
        try
        {
            result = await signInManager.PasskeySignInAsync(request.CredentialJson);
        }
        catch (Exception ex) when (PasskeyEndpoints.IsCeremonyInputFailure(ex))
        {
            return PasskeyEndpoints.SignInFailed();
        }

        // Every failure mode collapses to one answer. Lockout is the single exception worth
        // distinguishing, because a user who cannot tell a locked account from a broken
        // authenticator will keep retrying into the lockout.
        if (result.IsLockedOut)
            return Results.Json(new { error = "locked_out" }, statusCode: StatusCodes.Status401Unauthorized);

        return result.Succeeded
            ? Results.Ok(new { success = true })
            : PasskeyEndpoints.SignInFailed();
    }
}
