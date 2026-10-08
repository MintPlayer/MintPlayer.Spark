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
internal sealed partial class PasskeySignIn<TUser> : IPostEndpoint<PasskeySignInRequest>
    where TUser : SparkUser, new()
{
    public static string Path => "/passkeys/sign-in";

    static bool IEndpointBase.IsEnabled(IServiceProvider services) => SparkAuthFeatures.Passkeys(services);

    [Inject] private readonly SignInManager<TUser> signInManager;

    /// <summary>
    /// ⚠️ A body that cannot be bound — none, malformed, a JSON <c>null</c>, the wrong content type — is
    /// the uniform failure too, never the binder's 400 or 415: that would tell "junk" apart from
    /// "unknown credential". Measured identical before the endpoint became typed
    /// (<c>PasskeyEndpointTests.Unbindable_sign_in_bodies_answer_the_uniform_failure_as_before</c>).
    /// </summary>
    protected override ValueTask<IResult> OnBindFailedAsync(HttpContext context, EndpointBindingException? failure)
        => new(PasskeyEndpoints.SignInFailed());

    public override async Task<IResult> HandleAsync(PasskeySignInRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CredentialJson))
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
