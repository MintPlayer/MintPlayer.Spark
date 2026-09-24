using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;
using System.Text.Json;

namespace MintPlayer.Spark.Authorization.Extensions;

/// <summary>
/// The passkey (WebAuthn) surface: enrollment and management for a signed-in user, and
/// discoverable-credential sign-in for an anonymous one.
/// </summary>
/// <remarks>
/// <para>
/// Hand-mapped rather than source-generated, because the whole group is gated on
/// <see cref="SparkPasskeys"/> and the generated mapper is unconditional. Microsoft's
/// <c>MapIdentityApi</c> contributes nothing here — measured, it maps no passkey route at all — so
/// every endpoint below is Spark's own.
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
    private static IResult SignInFailed() =>
        Results.Json(new { error = "passkey_failed" }, statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// Malformed input is a 400 with no detail. The ceremony endpoints decode attacker-controlled
    /// base64url and credential JSON, which throws readily; an unhandled throw would be a 500 on an
    /// anonymous route and would put exception text in the response.
    /// </summary>
    private static IResult BadCeremonyInput() =>
        Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// Everything the framework throws when a ceremony cannot be evaluated.
    /// <para>
    /// ⚠️ <c>InvalidOperationException</c> belongs here and is easy to miss: posting a credential
    /// with no prior options call throws it with the message "No passkey assertion is underway",
    /// <em>not</em> a <c>PasskeyException</c>. Left out, the commonest hostile request — a bare POST
    /// to the anonymous sign-in route — answers 500 with a stack trace rather than a refusal.
    /// </para>
    /// </summary>
    private static bool IsCeremonyInputFailure(Exception ex)
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

        MapEnrollment<TUser>(authGroup);
        MapManagement<TUser>(authGroup);
        MapSignIn<TUser>(authGroup);
    }

    #region Enrollment

    private static void MapEnrollment<TUser>(RouteGroupBuilder authGroup)
        where TUser : SparkUser, new()
    {
        // POST, not GET: this mutates server state by setting the ceremony cookie, and a GET would
        // be cacheable and navigable cross-site.
        authGroup.MapPost("/passkeys/creation-options", async (
            UserManager<TUser> userManager,
            SignInManager<TUser> signInManager,
            HttpContext context) =>
        {
            var user = await userManager.GetUserAsync(context.User);
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
        })
            .RequireAuthorization()
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));

        authGroup.MapPost("/passkeys", async (
            UserManager<TUser> userManager,
            SignInManager<TUser> signInManager,
            HttpContext context,
            PasskeyRegistrationRequest request) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.CredentialJson))
                return BadCeremonyInput();

            PasskeyAttestationResult attestation;
            try
            {
                attestation = await signInManager.PerformPasskeyAttestationAsync(request.CredentialJson);
            }
            catch (Exception ex) when (IsCeremonyInputFailure(ex))
            {
                return BadCeremonyInput();
            }

            if (!attestation.Succeeded)
                return BadCeremonyInput();

            var passkey = attestation.Passkey;
            passkey.Name = Sanitize(request.Name);

            try
            {
                var result = await userManager.AddOrUpdatePasskeyAsync(user, passkey);
                if (!result.Succeeded)
                    return BadCeremonyInput();
            }
            catch (InvalidOperationException)
            {
                // The store refuses a credential id already held by another account, and refuses
                // without saying by whom. Answering "already registered" here would undo that.
                return BadCeremonyInput();
            }

            return Results.Ok(ToSummary(passkey));
        })
            .RequireAuthorization()
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    #endregion

    #region Management

    private static void MapManagement<TUser>(RouteGroupBuilder authGroup)
        where TUser : SparkUser, new()
    {
        authGroup.MapGet("/passkeys", async (
            UserManager<TUser> userManager,
            HttpContext context) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null)
                return Results.Unauthorized();

            var passkeys = await userManager.GetPasskeysAsync(user);
            // Metadata only. The public key, attestation object and client data JSON never leave the
            // server: nothing in the UI needs them, and shipping them widens the blast radius of any
            // future leak on this route for no benefit.
            return Results.Ok(passkeys.Select(ToSummary).ToArray());
        })
            .RequireAuthorization();

        authGroup.MapPost("/passkeys/{id}/name", async (
            UserManager<TUser> userManager,
            HttpContext context,
            string id,
            PasskeyRenameRequest request) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null)
                return Results.Unauthorized();

            if (!TryDecodeCredentialId(id, out var credentialId))
                return BadCeremonyInput();

            var passkey = await userManager.GetPasskeyAsync(user, credentialId);
            if (passkey is null)
                return Results.NotFound();

            passkey.Name = Sanitize(request.Name);
            var result = await userManager.AddOrUpdatePasskeyAsync(user, passkey);

            return result.Succeeded ? Results.Ok(ToSummary(passkey)) : BadCeremonyInput();
        })
            .RequireAuthorization()
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));

        authGroup.MapDelete("/passkeys/{id}", async (
            UserManager<TUser> userManager,
            IOptions<SparkAuthenticationOptions> options,
            HttpContext context,
            string id) =>
        {
            var user = await userManager.GetUserAsync(context.User);
            if (user is null)
                return Results.Unauthorized();

            if (!TryDecodeCredentialId(id, out var credentialId))
                return BadCeremonyInput();

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
            return result.Succeeded ? Results.Ok(new { success = true }) : BadCeremonyInput();
        })
            .RequireAuthorization()
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));
    }

    #endregion

    #region Sign-in

    private static void MapSignIn<TUser>(RouteGroupBuilder authGroup)
        where TUser : SparkUser, new()
    {
        // Anonymous, and takes no username — by design.
        //
        // MakePasskeyRequestOptionsAsync accepts a user, and passing one populates allowCredentials,
        // which turns this route into a user-existence oracle: a known account answers with
        // credentials, an unknown one with an empty list. Passing null always means the browser
        // offers whatever discoverable credential it holds for this relying party, the assertion
        // carries the user handle, and the server resolves the account from the credential id
        // afterwards. The response is then identical for every caller.
        authGroup.MapPost("/passkeys/request-options", async (SignInManager<TUser> signInManager) =>
        {
            var optionsJson = await signInManager.MakePasskeyRequestOptionsAsync(null);
            return Results.Text(optionsJson, "application/json");
        });

        authGroup.MapPost("/passkeys/sign-in", async (
            SignInManager<TUser> signInManager,
            PasskeyRegistrationRequest request) =>
        {
            // Every rejection on this route answers SignInFailed(), including malformed input and a
            // missing ceremony. A 400 here and a 401 there would let a caller separate "your payload
            // was junk" from "that credential is unknown", which is the distinction D10 spent the
            // username parameter to remove.
            if (string.IsNullOrWhiteSpace(request.CredentialJson))
                return SignInFailed();

            SignInResult result;
            try
            {
                result = await signInManager.PasskeySignInAsync(request.CredentialJson);
            }
            catch (Exception ex) when (IsCeremonyInputFailure(ex))
            {
                return SignInFailed();
            }

            // Every failure mode collapses to one answer. Lockout is the single exception worth
            // distinguishing, because a user who cannot tell a locked account from a broken
            // authenticator will keep retrying into the lockout.
            if (result.IsLockedOut)
                return Results.Json(new { error = "locked_out" }, statusCode: StatusCodes.Status401Unauthorized);

            return result.Succeeded ? Results.Ok(new { success = true }) : SignInFailed();
        });
    }

    #endregion

    #region Helpers

    /// <summary>
    /// The credential id as it appears in a URL: base64url, matching what the browser produces and
    /// what the listing endpoint hands out.
    /// </summary>
    private static string EncodeCredentialId(byte[] credentialId)
        => Convert.ToBase64String(credentialId).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryDecodeCredentialId(string value, out byte[] credentialId)
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
    private static string? Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var cleaned = new string([.. name.Trim().Where(c => !char.IsControl(c))]);
        return cleaned.Length == 0 ? null : cleaned[..Math.Min(cleaned.Length, 64)];
    }

    private static object ToSummary(UserPasskeyInfo passkey) => new
    {
        id = EncodeCredentialId(passkey.CredentialId),
        name = passkey.Name,
        createdAt = passkey.CreatedAt,
        isBackedUp = passkey.IsBackedUp,
        isBackupEligible = passkey.IsBackupEligible,
        transports = passkey.Transports,
    };

    #endregion
}

/// <summary>The credential JSON the browser produced, plus an optional label at enrollment.</summary>
internal sealed class PasskeyRegistrationRequest
{
    public string? CredentialJson { get; set; }
    public string? Name { get; set; }
}

internal sealed class PasskeyRenameRequest
{
    public string? Name { get; set; }
}
