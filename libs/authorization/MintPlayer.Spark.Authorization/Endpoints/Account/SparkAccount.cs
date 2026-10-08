using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Account;

/// <summary>
/// What Spark's account endpoints (<c>/spark/auth</c>, #460 D6, D8, D16) share: the identity-error
/// shape, code decoding and the bind-failure answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replacing Microsoft's.</b> <c>MapIdentityApi</c>'s <c>register</c>, <c>resendConfirmationEmail</c>,
/// <c>confirmEmail</c>, <c>forgotPassword</c>, <c>resetPassword</c> and <c>POST manage/info</c> are
/// filtered out (<c>LocalCredentialEndpointFilter</c>) and replaced by the classes in this namespace,
/// with the same request and response contracts, except that <c>register</c> also requires a
/// <c>userName</c> (<see cref="SparkRegisterRequest"/>). What changes: mailed links come from
/// <see cref="ISparkAuthLinkBuilder"/>; <c>forgotPassword</c> also sends to an unconfirmed address and a
/// completed reset confirms the email (D6 — the reset link reached the mailbox, which is what confirming
/// proves); a confirmed email change never touches the user name (G-Q22). Microsoft's <c>login</c>,
/// <c>refresh</c>, <c>manage/2fa</c> and <c>GET manage/info</c> stay.
/// </para>
/// <para>
/// <b>Classification</b> (<see cref="SparkLocalCredentials"/>) — each endpoint states its own through
/// <c>IsEnabled</c> and <see cref="LocalCredentialMode"/>; <c>AccountRouteClassificationTests</c> pins
/// every route:
/// </para>
/// <list type="bullet">
/// <item><description><c>Full</c> only: <c>register</c>, <c>resendConfirmationEmail</c> (self-service sign-up and its mail trigger).</description></item>
/// <item><description><c>Full</c> + <c>SignInOnly</c>: <c>forgotPassword</c>, <c>resetPassword</c>, <c>POST manage/info</c>, <c>manage/password</c> — the password surface, and the email change (under <c>Disabled</c> the email is the one the provider attested).</description></item>
/// <item><description>Every mode: <c>confirmEmail</c> (GET, legacy links) and <c>confirm-email</c> (POST) — an external sign-up from a provider without a verified-email signal is confirmed by mail (D7) in any mode; <c>manage/profile</c>, <c>manage/2fa/authenticator-uri</c>, <c>manage/personal-data</c>, <c>DELETE manage/account</c>.</description></item>
/// </list>
/// <para>
/// <b>Antiforgery</b>: every mutating route carries <see cref="Microsoft.AspNetCore.Antiforgery.RequireAntiforgeryTokenAttribute"/>
/// explicitly, including the anonymous ones — an exemption would have to be stated, and none is.
/// </para>
/// </remarks>
internal static class SparkAccount
{
    private static readonly EmailAddressAttribute EmailAddress = new();

    internal static bool IsValidEmail(string? email)
        => !string.IsNullOrEmpty(email) && EmailAddress.IsValid(email);

    internal static bool EmailChangeEnabled(SparkAuthenticationOptions options)
        => options.EmailChange == SparkEmailChange.Enabled;

    /// <summary>
    /// The answer for a request that could not be bound: the status alone, without a problem body — what
    /// the minimal-API <c>[FromBody]</c>/<c>[FromQuery]</c> binding these routes replaced answered outside
    /// Development (measured before the move: missing, empty, <c>null</c> and malformed bodies and a
    /// missing required query value all answered a bare 400; a body of another content type a bare 415).
    /// </summary>
    /// <remarks>
    /// A request without any content type is a <em>missing</em> body (400), as minimal APIs answered it,
    /// not an unreadable content type: the typed binder reports it as 415.
    /// </remarks>
    internal static ValueTask<IResult> BindFailed(HttpContext context, EndpointBindingException? failure)
    {
        var status = failure?.StatusCode ?? StatusCodes.Status400BadRequest;
        if (status == StatusCodes.Status415UnsupportedMediaType && string.IsNullOrEmpty(context.Request.ContentType))
            status = StatusCodes.Status400BadRequest;

        return new(Results.StatusCode(status));
    }

    /// <summary>
    /// A posted <see cref="SparkProfileRequest.PreferredCulture"/>: null or empty clears it, a predefined
    /// culture name is stored in its canonical casing (<c>nl-be</c> → <c>nl-BE</c>), anything else is
    /// refused. <see langword="false"/> with <paramref name="culture"/> unset means invalid.
    /// </summary>
    internal static bool TryNormalizeCulture(string? posted, out string? culture)
    {
        culture = null;
        var name = posted?.Trim();
        if (string.IsNullOrEmpty(name))
            return true;

        // 85 = LOCALE_NAME_MAX_LENGTH; the invariant culture has the empty name, handled above.
        if (name.Length > 85)
            return false;
        try
        {
            culture = CultureInfo.GetCultureInfo(name, predefinedOnly: true).Name;
            return culture.Length > 0;
        }
        catch (CultureNotFoundException)
        {
            culture = null;
            return false;
        }
    }

    /// <summary>
    /// A password change rotates the security stamp; without a refresh the cookie that made the call
    /// would be refused at the next stamp validation. A bearer caller refreshes via <c>/refresh</c>.
    /// </summary>
    internal static async Task RefreshCookieAsync<TUser>(SignInManager<TUser> signInManager, ClaimsPrincipal principal, TUser user)
        where TUser : SparkUser
    {
        if (principal.Identity?.AuthenticationType == IdentityConstants.ApplicationScheme)
            await signInManager.RefreshSignInAsync(user);
    }

    internal static string? DecodeCode(string? code)
    {
        if (string.IsNullOrEmpty(code))
            return null;

        try
        {
            return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Confirms a mailed link, from either <see cref="ConfirmEmailLink{TUser}"/> or <see cref="ConfirmEmail{TUser}"/>:
    /// the account's email, or — when <paramref name="changedEmail"/> is set — a change to that address.
    /// </summary>
    internal static async Task<IdentityResult> ConfirmAsync<TUser>(
        UserManager<TUser> userManager, SparkAuthenticationOptions options, string? userId, string? rawCode, string? changedEmail)
        where TUser : SparkUser
    {
        var invalid = IdentityResult.Failed(userManager.ErrorDescriber.InvalidToken());

        if (string.IsNullOrEmpty(userId) || DecodeCode(rawCode) is not { } code)
            return invalid;

        if (await userManager.FindByIdAsync(userId) is not { } user)
            return invalid;

        // A change link minted while email change was enabled must not outlive switching it off.
        if (!string.IsNullOrEmpty(changedEmail) && !EmailChangeEnabled(options))
            return invalid;

        // A change moves the email only. (Microsoft's handler also set the user name to the new
        // email, publishing the address as the handle; G-Q22.)
        return string.IsNullOrEmpty(changedEmail)
            ? await userManager.ConfirmEmailAsync(user, code)
            : await userManager.ChangeEmailAsync(user, changedEmail, code);
    }

    internal static ValidationProblem Problem(IdentityResult result)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var error in result.Errors)
        {
            errors[error.Code] = errors.TryGetValue(error.Code, out var existing)
                ? [.. existing, error.Description]
                : [error.Description];
        }

        return TypedResults.ValidationProblem(errors);
    }
}
