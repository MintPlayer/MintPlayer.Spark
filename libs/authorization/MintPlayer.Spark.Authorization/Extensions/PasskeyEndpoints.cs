using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Endpoints.Passkeys;
using MintPlayer.Spark.Authorization.Identity;
using System.Text.Json;

namespace MintPlayer.Spark.Authorization.Extensions;

/// <summary>
/// The passkey (WebAuthn) HTTP surface: discoverable-credential sign-in for an anonymous caller.
/// </summary>
/// <remarks>
/// <para>
/// Hand-mapped rather than source-generated, because the whole group is gated on
/// <see cref="SparkPasskeys"/> and the generated mapper is unconditional. Microsoft's
/// <c>MapIdentityApi</c> contributes nothing here — measured, it maps no passkey route at all — so
/// every endpoint below is Spark's own.
/// </para>
/// <para>
/// A signed-in user's passkeys are not HTTP: listing, adding, renaming and removing them are the
/// generic passkeys page's <c>my-passkeys</c> query and custom actions, over
/// <see cref="ISparkPasskeyAccount"/> (generic passkeys page PRD D8). The helpers below are shared
/// with it.
/// </para>
/// <para>
/// ⚠️ The ceremony runs through <see cref="SignInManager{TUser}"/>, never
/// <c>IPasskeyHandler</c> directly. The handler hands its caller an <c>AttestationState</c> that is
/// plaintext JSON containing the challenge and the target user id; round-tripping that through the
/// browser would let a caller choose their own challenge (assertion replay) and rewrite the user a
/// credential enrolls against (account takeover). <c>SignInManager</c> keeps the state in a
/// DataProtection-protected cookie instead, so none of it is reachable from the client and this
/// package writes no cryptographic code.
/// </para>
/// </remarks>
internal static class PasskeyEndpoints
{
    /// <summary>
    /// Uniform failure body. Every sign-in rejection — unknown credential, bad signature, failed
    /// user verification, replay — answers with exactly this, so the endpoint cannot be used to
    /// learn which of those happened, or whether a given credential or account exists.
    /// </summary>
    internal static IResult SignInFailed() =>
        Results.Json(new { error = "passkey_failed" }, statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// Everything the framework throws when a ceremony cannot be evaluated.
    /// <para>
    /// ⚠️ <c>InvalidOperationException</c> belongs here and is easy to miss: posting a credential
    /// with no prior options call throws it with the message "No passkey assertion is underway",
    /// <em>not</em> a <c>PasskeyException</c>. Left out, the commonest hostile request — a bare POST
    /// to the anonymous sign-in route — answers 500 with a stack trace rather than a refusal.
    /// </para>
    /// </summary>
    internal static bool IsCeremonyInputFailure(Exception ex)
        => ex is PasskeyException or InvalidOperationException or JsonException or FormatException or ArgumentException;

    internal static void MapPasskeyApi<TUser>(
        IEndpointRouteBuilder endpoints,
        RouteGroupBuilder authGroup)
        where TUser : SparkUser, new()
    {
        var passkeys = endpoints.ServiceProvider
            .GetService<IOptions<SparkAuthenticationOptions>>()?.Value.Passkeys
            ?? SparkPasskeys.Disabled;

        if (passkeys == SparkPasskeys.Disabled)
            return;

        // Generator endpoint classes. They are open generics, so this assembly's generated
        // MapSparkAuthEndpoints() deliberately skips them (MPEP025, Info) and they are mapped here,
        // where TUser is concrete. Mapped on `endpoints`, not `authGroup`: the /spark/auth prefix
        // comes from [MemberOf<SparkAuthGroup>], and mapping onto the group as well would compose
        // it twice.
        endpoints.MapEndpoint<PasskeyRequestOptions<TUser>>();
        endpoints.MapEndpoint<PasskeySignIn<TUser>>();
    }


    #region Helpers

    /// <summary>
    /// The credential id as the passkeys page's rows carry it: base64url, matching what the browser
    /// produces.
    /// </summary>
    internal static string EncodeCredentialId(byte[] credentialId)
        => Convert.ToBase64String(credentialId).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static bool TryDecodeCredentialId(string value, out byte[] credentialId)
    {
        credentialId = [];
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);

        try
        {
            credentialId = Convert.FromBase64String(padded);
            return credentialId.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// A user-chosen label, bounded and stripped of control characters. It is rendered in a list of
    /// the user's own credentials, so the only real risks are unbounded storage and a name that
    /// disrupts the display.
    /// </summary>
    internal static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var cleaned = new string([.. name.Trim().Where(c => !char.IsControl(c))]);
        return cleaned.Length == 0 ? null : cleaned[..Math.Min(cleaned.Length, 64)];
    }

    #endregion
}

/// <summary>The assertion JSON the browser produced for a sign-in.</summary>
internal sealed class PasskeySignInRequest
{
    public string? CredentialJson { get; set; }
}
