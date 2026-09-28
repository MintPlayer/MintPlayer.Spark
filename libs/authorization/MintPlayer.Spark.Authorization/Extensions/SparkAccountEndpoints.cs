using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Extensions;

/// <summary>
/// Spark's own account endpoints under <c>/spark/auth</c> (#460 D6, D8, D16): the mail-sending half of
/// the local-credential family, replaced so its links point at the SPA and its rules are Spark's, and
/// the account-management pages.
/// </summary>
/// <remarks>
/// <para>
/// <b>Replacing Microsoft's.</b> <c>MapIdentityApi</c>'s <c>register</c>, <c>resendConfirmationEmail</c>,
/// <c>confirmEmail</c>, <c>forgotPassword</c>, <c>resetPassword</c> and <c>POST manage/info</c> are
/// filtered out (<see cref="LocalCredentialEndpointFilter"/>) and mapped here with the same request and
/// response contracts. What changes: mailed links come from <see cref="ISparkAuthLinkBuilder"/>;
/// <c>forgotPassword</c> also sends to an unconfirmed address and a completed reset confirms the email
/// (D6 — the reset link reached the mailbox, which is what confirming proves); a confirmed email change
/// only moves a user name that was the old email. Microsoft's <c>login</c>, <c>refresh</c>,
/// <c>manage/2fa</c> and <c>GET manage/info</c> stay.
/// </para>
/// <para>
/// <b>Classification</b> (<see cref="SparkLocalCredentials"/>) — kept in this one method, and pinned by
/// <c>AccountRouteClassificationTests</c> for every route:
/// </para>
/// <list type="bullet">
/// <item><description><c>Full</c> only: <c>register</c>, <c>resendConfirmationEmail</c> (self-service sign-up and its mail trigger).</description></item>
/// <item><description><c>Full</c> + <c>SignInOnly</c>: <c>forgotPassword</c>, <c>resetPassword</c>, <c>POST manage/info</c>, <c>manage/password</c> — the password surface, and the email change (under <c>Disabled</c> the email is the one the provider attested).</description></item>
/// <item><description>Every mode: <c>confirmEmail</c> (GET, legacy links) and <c>confirm-email</c> (POST) — an external sign-up from a provider without a verified-email signal is confirmed by mail (D7) in any mode; <c>manage/profile</c>, <c>manage/2fa/authenticator-uri</c>, <c>manage/personal-data</c>, <c>DELETE manage/account</c>.</description></item>
/// </list>
/// <para>
/// <b>Antiforgery</b>: every mutating route carries <see cref="RequireAntiforgeryTokenAttribute"/>
/// explicitly, including the anonymous ones — an exemption would have to be stated, and none is.
/// </para>
/// </remarks>
internal static class SparkAccountEndpoints
{
    private static readonly EmailAddressAttribute EmailAddress = new();

    internal static void Map<TUser>(IEndpointRouteBuilder endpoints, SparkLocalCredentials mode)
        where TUser : SparkUser, new()
    {
        var group = endpoints.MapGroup("/spark/auth");
        var manage = group.MapGroup("/manage").RequireAuthorization();

        if (mode == SparkLocalCredentials.Full)
        {
            Mutating(group.MapPost("/register", RegisterAsync<TUser>));
            Mutating(group.MapPost("/resendConfirmationEmail", ResendConfirmationEmailAsync<TUser>));
        }

        if (mode != SparkLocalCredentials.Disabled)
        {
            Mutating(group.MapPost("/forgotPassword", ForgotPasswordAsync<TUser>));
            Mutating(group.MapPost("/resetPassword", ResetPasswordAsync<TUser>));
            Mutating(manage.MapPost("/info", PostInfoAsync<TUser>));
            Mutating(manage.MapPost("/password", SetPasswordAsync<TUser>));
        }

        // A mailbox link: a plain top-level GET, no session, the single-use token is the credential
        // (same reasoning as confirm-external-link). Kept for links already sent by older versions.
        group.MapGet("/confirmEmail", ConfirmEmailGetAsync<TUser>);
        Mutating(group.MapPost("/confirm-email", ConfirmEmailPostAsync<TUser>));

        manage.MapGet("/profile", GetProfileAsync<TUser>);
        Mutating(manage.MapPost("/profile", PostProfileAsync<TUser>));
        manage.MapGet("/2fa/authenticator-uri", GetAuthenticatorUriAsync<TUser>);
        manage.MapGet("/personal-data", GetPersonalDataAsync<TUser>);
        Mutating(manage.MapDelete("/account", DeleteAccountAsync<TUser>));
    }

    private static void Mutating(RouteHandlerBuilder builder)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    #region Local-credential family (replacing MapIdentityApi's)

    private static async Task<Results<Ok, ValidationProblem>> RegisterAsync<TUser>(
        [FromBody] RegisterRequest registration, HttpContext context, [FromServices] IServiceProvider services)
        where TUser : SparkUser, new()
    {
        var userManager = services.GetRequiredService<UserManager<TUser>>();
        var store = services.GetRequiredService<IUserStore<TUser>>();
        var email = registration.Email;

        if (string.IsNullOrEmpty(email) || !EmailAddress.IsValid(email))
            return Problem(IdentityResult.Failed(userManager.ErrorDescriber.InvalidEmail(email)));

        // Through the store, not UserManager.SetUserNameAsync: the manager would validate (and try to
        // save) a user whose email is not set yet, which the '@' user-name rule refuses.
        var user = new TUser();
        await store.SetUserNameAsync(user, email, CancellationToken.None);
        await ((IUserEmailStore<TUser>)store).SetEmailAsync(user, email, CancellationToken.None);

        var result = await userManager.CreateAsync(user, registration.Password);
        if (!result.Succeeded)
            return Problem(result);

        await services.GetRequiredService<SparkAccountMail<TUser>>().SendConfirmationAsync(context, user, email);
        return TypedResults.Ok();
    }

    private static async Task<Ok> ResendConfirmationEmailAsync<TUser>(
        [FromBody] ResendConfirmationEmailRequest request, HttpContext context, [FromServices] IServiceProvider services)
        where TUser : SparkUser, new()
    {
        var userManager = services.GetRequiredService<UserManager<TUser>>();
        if (await userManager.FindByEmailAsync(request.Email) is { EmailConfirmed: false } user)
            await services.GetRequiredService<SparkAccountMail<TUser>>().SendConfirmationAsync(context, user, request.Email);

        // Same answer whether or not the address belongs to anyone.
        return TypedResults.Ok();
    }

    private static async Task<Ok> ForgotPasswordAsync<TUser>(
        [FromBody] ForgotPasswordRequest request, HttpContext context, [FromServices] IServiceProvider services)
        where TUser : SparkUser, new()
    {
        var userManager = services.GetRequiredService<UserManager<TUser>>();

        // D6: unconfirmed addresses too. Microsoft sends only to confirmed ones, which strands every
        // account created before confirmation was enforced; completing the reset confirms the email.
        if (await userManager.FindByEmailAsync(request.Email) is { } user)
            await services.GetRequiredService<SparkAccountMail<TUser>>().SendPasswordResetAsync(context, user, request.Email);

        return TypedResults.Ok();
    }

    private static async Task<Results<Ok, ValidationProblem>> ResetPasswordAsync<TUser>(
        [FromBody] ResetPasswordRequest request, [FromServices] IServiceProvider services)
        where TUser : SparkUser, new()
    {
        var userManager = services.GetRequiredService<UserManager<TUser>>();
        var user = await userManager.FindByEmailAsync(request.Email);

        // One answer for "no such account" and "bad token", as Microsoft's does.
        if (user is null || DecodeCode(request.ResetCode) is not { } code)
            return Problem(IdentityResult.Failed(userManager.ErrorDescriber.InvalidToken()));

        var result = await userManager.ResetPasswordAsync(user, code, request.NewPassword);
        if (!result.Succeeded)
            return Problem(result);

        // D6: the token was delivered to this address, so the address is proven.
        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            var confirmed = await userManager.UpdateAsync(user);
            if (!confirmed.Succeeded)
                return Problem(confirmed);
        }

        return TypedResults.Ok();
    }

    private static async Task<Results<Ok<InfoResponse>, ValidationProblem, NotFound>> PostInfoAsync<TUser>(
        ClaimsPrincipal claimsPrincipal, [FromBody] InfoRequest request, HttpContext context, [FromServices] IServiceProvider services)
        where TUser : SparkUser, new()
    {
        var userManager = services.GetRequiredService<UserManager<TUser>>();
        if (await userManager.GetUserAsync(claimsPrincipal) is not { } user)
            return TypedResults.NotFound();

        if (!string.IsNullOrEmpty(request.NewEmail) && !EmailAddress.IsValid(request.NewEmail))
            return Problem(IdentityResult.Failed(userManager.ErrorDescriber.InvalidEmail(request.NewEmail)));

        if (!string.IsNullOrEmpty(request.NewPassword))
        {
            if (string.IsNullOrEmpty(request.OldPassword))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["OldPasswordRequired"] = ["The old password is required to set a new password. If the old password is forgotten, use /resetPassword."],
                });
            }

            var changed = await userManager.ChangePasswordAsync(user, request.OldPassword, request.NewPassword);
            if (!changed.Succeeded)
                return Problem(changed);
        }

        if (!string.IsNullOrEmpty(request.NewEmail)
            && !string.Equals(await userManager.GetEmailAsync(user), request.NewEmail, StringComparison.OrdinalIgnoreCase))
        {
            // Mailed to the NEW address; nothing changes until its link is followed.
            await services.GetRequiredService<SparkAccountMail<TUser>>()
                .SendConfirmationAsync(context, user, request.NewEmail, changedEmail: request.NewEmail);
        }

        return TypedResults.Ok(new InfoResponse
        {
            Email = await userManager.GetEmailAsync(user) ?? throw new NotSupportedException("Users must have an email."),
            IsEmailConfirmed = await userManager.IsEmailConfirmedAsync(user),
        });
    }

    private static async Task<IResult> ConfirmEmailGetAsync<TUser>(
        [FromQuery] string userId, [FromQuery] string code, [FromQuery] string? changedEmail, [FromServices] IServiceProvider services)
        where TUser : SparkUser, new()
    {
        var result = await ConfirmAsync<TUser>(services, userId, code, changedEmail);
        return result.Succeeded
            ? Results.Text("Thank you for confirming your email.")
            : Results.Unauthorized();
    }

    private static async Task<Results<Ok, ValidationProblem>> ConfirmEmailPostAsync<TUser>(
        [FromBody] SparkConfirmEmailRequest request, [FromServices] IServiceProvider services)
        where TUser : SparkUser, new()
    {
        var result = await ConfirmAsync<TUser>(services, request.UserId, request.Code, request.ChangedEmail);
        return result.Succeeded ? TypedResults.Ok() : Problem(result);
    }

    private static async Task<IdentityResult> ConfirmAsync<TUser>(IServiceProvider services, string? userId, string? rawCode, string? changedEmail)
        where TUser : SparkUser, new()
    {
        var userManager = services.GetRequiredService<UserManager<TUser>>();
        var invalid = IdentityResult.Failed(userManager.ErrorDescriber.InvalidToken());

        if (string.IsNullOrEmpty(userId) || DecodeCode(rawCode) is not { } code)
            return invalid;

        if (await userManager.FindByIdAsync(userId) is not { } user)
            return invalid;

        // A change goes through SparkUserManager.ChangeEmailAsync, which moves an email-shaped user
        // name along in the same save. (Microsoft's handler set the user name to the new email
        // unconditionally, overwriting a chosen handle.)
        return string.IsNullOrEmpty(changedEmail)
            ? await userManager.ConfirmEmailAsync(user, code)
            : await userManager.ChangeEmailAsync(user, changedEmail, code);
    }

    private static async Task<Results<Ok, ValidationProblem, NotFound>> SetPasswordAsync<TUser>(
        ClaimsPrincipal claimsPrincipal, [FromBody] SparkSetPasswordRequest request, [FromServices] IServiceProvider services)
        where TUser : SparkUser, new()
    {
        var userManager = services.GetRequiredService<UserManager<TUser>>();
        if (await userManager.GetUserAsync(claimsPrincipal) is not { } user)
            return TypedResults.NotFound();

        if (string.IsNullOrEmpty(request.NewPassword))
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["NewPasswordRequired"] = ["A new password is required."] });

        IdentityResult result;
        if (await userManager.HasPasswordAsync(user))
        {
            if (string.IsNullOrEmpty(request.CurrentPassword))
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["CurrentPasswordRequired"] = ["The current password is required to change it."] });

            result = await userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        }
        else
        {
            // A social-only account adding its first password.
            result = await userManager.AddPasswordAsync(user, request.NewPassword);
        }

        if (!result.Succeeded)
            return Problem(result);

        await RefreshCookieAsync(services, claimsPrincipal, user);
        return TypedResults.Ok();
    }

    #endregion

    #region Profile, authenticator, personal data, deletion

    private static async Task<IResult> GetProfileAsync<TUser>(ClaimsPrincipal claimsPrincipal, HttpContext context, [FromServices] IServiceProvider services)
        where TUser : SparkUser, new()
    {
        var userManager = services.GetRequiredService<UserManager<TUser>>();
        if (await userManager.GetUserAsync(claimsPrincipal) is not { } user)
            return Results.NotFound();

        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var contributor in services.GetServices<ISparkProfileContributor<TUser>>())
        {
            foreach (var (name, value) in await contributor.GetAsync(user, context.RequestAborted))
                fields[name] = value;
        }

        return Results.Ok(new SparkProfileResponse(user.UserName, user.Email, fields));
    }

    private static async Task<IResult> PostProfileAsync<TUser>(
        ClaimsPrincipal claimsPrincipal, [FromBody] SparkProfileRequest request, HttpContext context, [FromServices] IServiceProvider services)
        where TUser : SparkUser, new()
    {
        var userManager = services.GetRequiredService<UserManager<TUser>>();
        if (await userManager.GetUserAsync(claimsPrincipal) is not { } user)
            return Results.NotFound();

        var contributors = services.GetServices<ISparkProfileContributor<TUser>>().ToArray();
        var posted = request.Fields ?? new Dictionary<string, JsonElement>();
        var errors = new SparkProfileErrors();

        // Every posted field must belong to a contributor: an unknown one is a client bug or a probe,
        // and silently dropping it would report a save that did not happen.
        foreach (var name in posted.Keys)
        {
            if (!contributors.Any(c => c.Fields.Contains(name, StringComparer.OrdinalIgnoreCase)))
                errors.Add(name, "Unknown profile field.");
        }

        var perContributor = contributors
            .Select(c => (Contributor: c, Values: (IReadOnlyDictionary<string, JsonElement>)posted
                .Where(p => c.Fields.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase)))
            .Where(x => x.Values.Count > 0)
            .ToArray();

        foreach (var (contributor, values) in perContributor)
            await contributor.ValidateAsync(user, values, errors, context.RequestAborted);

        if (errors.HasErrors)
            return TypedResults.ValidationProblem(errors.ToDictionary());

        if (request.UserName is { } userName && !string.Equals(userName, user.UserName, StringComparison.Ordinal))
        {
            // Set on the entity; the single UpdateAsync below validates (uniqueness, characters, the '@'
            // rule), normalizes and saves it together with the contributed fields.
            await services.GetRequiredService<IUserStore<TUser>>().SetUserNameAsync(user, userName.Trim(), context.RequestAborted);
        }

        foreach (var (contributor, values) in perContributor)
            await contributor.ApplyAsync(user, values, context.RequestAborted);

        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
            return Problem(result);

        return await GetProfileAsync<TUser>(claimsPrincipal, context, services);
    }

    private static async Task<IResult> GetAuthenticatorUriAsync<TUser>(
        ClaimsPrincipal claimsPrincipal, HttpContext context, [FromServices] IServiceProvider services)
        where TUser : SparkUser, new()
    {
        var userManager = services.GetRequiredService<UserManager<TUser>>();
        if (await userManager.GetUserAsync(claimsPrincipal) is not { } user)
            return Results.NotFound();

        // Read-only: the key is created by POST /manage/2fa (Microsoft's), never by a GET.
        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
            return Results.Json(new { error = "no_authenticator_key" }, statusCode: StatusCodes.Status409Conflict);

        var options = services.GetService<IOptions<SparkAuthenticationOptions>>()?.Value;
        var issuer = options?.AuthenticatorIssuer
            ?? services.GetService<IHostEnvironment>()?.ApplicationName
            ?? "Spark";
        var account = user.Email ?? user.UserName ?? user.Id ?? "account";

        var uri = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}"
            + $"?secret={Uri.EscapeDataString(key)}&issuer={Uri.EscapeDataString(issuer)}&digits=6";

        // The body is the shared secret: never cached anywhere.
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new SparkAuthenticatorUriResponse(
            key, uri, services.GetRequiredService<SparkQrCodeRenderer>().RenderSvg(uri)));
    }

    private static async Task<IResult> GetPersonalDataAsync<TUser>(
        ClaimsPrincipal claimsPrincipal, HttpContext context, [FromServices] IServiceProvider services)
        where TUser : SparkUser, new()
    {
        var userManager = services.GetRequiredService<UserManager<TUser>>();
        if (await userManager.GetUserAsync(claimsPrincipal) is not { } user)
            return Results.NotFound();

        var contributions = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var contributor in services.GetServices<ISparkPersonalDataContributor<TUser>>())
        {
            if (await contributor.GetPersonalDataAsync(user, context.RequestAborted) is { } data)
                contributions[contributor.Name] = data;
        }

        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.ContentDisposition = "attachment; filename=\"personal-data.json\"";

        // What the account holds about the person — never credentials, hashes, keys or tokens.
        return Results.Ok(new
        {
            account = new
            {
                id = user.Id,
                userName = user.UserName,
                email = user.Email,
                emailConfirmed = user.EmailConfirmed,
                phoneNumber = user.PhoneNumber,
                phoneNumberConfirmed = user.PhoneNumberConfirmed,
                twoFactorEnabled = user.TwoFactorEnabled,
                createdAtUtc = user.CreatedAtUtc,
                registrationMethod = user.RegistrationMethod,
                roles = user.Roles,
                claims = user.Claims.Select(c => new { type = c.ClaimType, value = c.ClaimValue }),
                externalLogins = user.Logins.Select(l => new { provider = l.LoginProvider, displayName = l.ProviderDisplayName }),
                passkeys = user.Passkeys.Select(p => new { name = p.Name, createdAt = p.CreatedAt }),
            },
            data = contributions,
        });
    }

    private static async Task<IResult> DeleteAccountAsync<TUser>(
        ClaimsPrincipal claimsPrincipal, HttpContext context, [FromServices] IServiceProvider services)
        where TUser : SparkUser, new()
    {
        var userManager = services.GetRequiredService<UserManager<TUser>>();
        var signInManager = services.GetRequiredService<SignInManager<TUser>>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(SparkAccountEndpoints).FullName!);

        if (await userManager.GetUserAsync(claimsPrincipal) is not { } user)
            return Results.NotFound();

        SparkDeleteAccountRequest? request = null;
        if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
        {
            try
            {
                request = await context.Request.ReadFromJsonAsync<SparkDeleteAccountRequest>(context.RequestAborted);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
            }
        }

        if (!await IsReauthenticatedAsync(services, signInManager, user, request?.Password))
            return Results.Json(new { error = "reauthentication_required" }, statusCode: StatusCodes.Status403Forbidden);

        // Handlers first, the account last: a failing handler leaves the account intact and the
        // request retryable (D8).
        foreach (var handler in services.GetServices<ISparkAccountDeletionHandler<TUser>>())
        {
            try
            {
                await handler.OnDeletingAccountAsync(user, context.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Account deletion of user {UserId} stopped: {Handler} failed. The account is intact.", user.Id, handler.GetType().FullName);
                return Results.Json(new { error = "deletion_failed" }, statusCode: StatusCodes.Status500InternalServerError);
            }
        }

        var deleted = await userManager.DeleteAsync(user);
        if (!deleted.Succeeded)
        {
            logger.LogError("Account deletion of user {UserId} failed in the store: {Errors}", user.Id, string.Join("; ", deleted.Errors.Select(e => e.Code)));
            return Results.Json(new { error = "deletion_failed" }, statusCode: StatusCodes.Status500InternalServerError);
        }

        if (claimsPrincipal.Identity?.AuthenticationType == IdentityConstants.ApplicationScheme)
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);

        return Results.NoContent();
    }

    /// <summary>
    /// Re-authentication for account deletion: the current password (checked with lockout), or — for
    /// an account without one, or a caller that did not supply it — a sign-in no older than
    /// <see cref="SparkAuthenticationOptions.ReauthenticationMaxAge"/>.
    /// </summary>
    private static async Task<bool> IsReauthenticatedAsync<TUser>(
        IServiceProvider services, SignInManager<TUser> signInManager, TUser user, string? password)
        where TUser : SparkUser
    {
        if (!string.IsNullOrEmpty(password))
        {
            var check = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
            return check.Succeeded;
        }

        if (signInManager is not SparkSignInManager<TUser> spark || await spark.GetAuthenticatedAtAsync() is not { } at)
            return false;

        var maxAge = services.GetService<IOptions<SparkAuthenticationOptions>>()?.Value.ReauthenticationMaxAge ?? TimeSpan.FromMinutes(5);
        var now = (services.GetService<TimeProvider>() ?? TimeProvider.System).GetUtcNow();
        return now - at <= maxAge;
    }

    #endregion

    #region Helpers

    private static async Task RefreshCookieAsync<TUser>(IServiceProvider services, ClaimsPrincipal principal, TUser user)
        where TUser : SparkUser
    {
        // A password change rotates the security stamp; without a refresh the cookie that made this
        // call would be refused at the next stamp validation. A bearer caller refreshes via /refresh.
        if (principal.Identity?.AuthenticationType == IdentityConstants.ApplicationScheme)
            await services.GetRequiredService<SignInManager<TUser>>().RefreshSignInAsync(user);
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

    private static ValidationProblem Problem(IdentityResult result)
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

    #endregion
}

/// <summary>Body of <c>POST /spark/auth/confirm-email</c>: the query of the mailed link.</summary>
public sealed class SparkConfirmEmailRequest
{
    public string? UserId { get; set; }
    public string? Code { get; set; }
    /// <summary>Set when the link confirms an email <em>change</em>.</summary>
    public string? ChangedEmail { get; set; }
}

/// <summary>Body of <c>POST /spark/auth/manage/password</c>. <see cref="CurrentPassword"/> is required when the account already has one.</summary>
public sealed class SparkSetPasswordRequest
{
    public string? CurrentPassword { get; set; }
    public string? NewPassword { get; set; }
}

/// <summary>Body of <c>POST /spark/auth/manage/profile</c>.</summary>
public sealed class SparkProfileRequest
{
    /// <summary>A new user name, or <see langword="null"/> to keep it.</summary>
    public string? UserName { get; set; }
    /// <summary>Application fields, each owned by an <see cref="ISparkProfileContributor{TUser}"/>.</summary>
    public Dictionary<string, JsonElement>? Fields { get; set; }
}

/// <summary>Answer of <c>GET/POST /spark/auth/manage/profile</c>.</summary>
public sealed record SparkProfileResponse(string? UserName, string? Email, IReadOnlyDictionary<string, object?> Fields);

/// <summary>Answer of <c>GET /spark/auth/manage/2fa/authenticator-uri</c>.</summary>
public sealed record SparkAuthenticatorUriResponse(string SharedKey, string AuthenticatorUri, string QrCodeSvg);

/// <summary>Optional body of <c>DELETE /spark/auth/manage/account</c>.</summary>
public sealed class SparkDeleteAccountRequest
{
    /// <summary>The current password. Without it, the sign-in must be recent.</summary>
    public string? Password { get; set; }
}
