using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Authorization.Configuration;
using MintPlayer.Spark.Authorization.Extensions;
using MintPlayer.Spark.Authorization.Identity;

namespace MintPlayer.Spark.Authorization.Endpoints.Account;

/// <summary>
/// <c>DELETE /spark/auth/manage/account</c>: deletes the signed-in account after re-authentication (D8).
/// Every mode.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Reads its body by hand, deliberately</b> (an exception to typed endpoints, D3). The body is
/// <em>optional</em>: without a password the sign-in must be recent instead. A typed
/// <c>IDeleteEndpoint&lt;TRequest, TResponse&gt;</c> treats a body as required — it answers a bodiless
/// request 400/415 before the handler runs — and adds <c>IAcceptsMetadata</c> the route never had.
/// </para>
/// </remarks>
[MemberOf<SparkAuthManageGroup>]
internal sealed partial class DeleteAccount<TUser> : IDeleteEndpoint
    where TUser : SparkUser, new()
{
    public static string Path => "/account";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly UserManager<TUser> userManager;
    [Inject] private readonly SignInManager<TUser> signInManager;
    [Inject] private readonly IOptions<SparkAuthenticationOptions> options;
    [Inject] private readonly IEnumerable<ISparkAccountDeletionHandler<TUser>> deletionHandlers;
    [Inject] private readonly IEnumerable<ISparkAccountDeletedHandler<TUser>> deletedHandlers;
    [Inject] private readonly ILogger<DeleteAccount<TUser>> logger;
    // Optional parameters last: [Inject] makes a nullable field an optional constructor parameter.
    [Inject] private readonly TimeProvider? timeProvider;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (await userManager.GetUserAsync(httpContext.User) is not { } user)
            return Results.NotFound();

        SparkDeleteAccountRequest? request = null;
        if (httpContext.Request.ContentLength is > 0 || httpContext.Request.Headers.TransferEncoding.Count > 0)
        {
            try
            {
                request = await httpContext.Request.ReadFromJsonAsync<SparkDeleteAccountRequest>(httpContext.RequestAborted);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
            }
        }

        if (!await IsReauthenticatedAsync(user, request?.Password))
            return Results.Json(new { error = "reauthentication_required" }, statusCode: StatusCodes.Status403Forbidden);

        // Handlers first, the account last: a failing handler leaves the account intact and the
        // request retryable (D8).
        foreach (var handler in deletionHandlers)
        {
            try
            {
                await handler.OnDeletingAccountAsync(user, httpContext.RequestAborted);
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

        // The deletion is committed: only now may anything act on it (a goodbye mail). A failure here
        // cannot undo it, so it is logged and the rest still run. CancellationToken.None: the client
        // hanging up after the delete must not skip the after-deletion work.
        foreach (var handler in deletedHandlers)
        {
            try
            {
                await handler.OnAccountDeletedAsync(user, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "After-deletion handler {Handler} failed for deleted user {UserId}.", handler.GetType().FullName, user.Id);
            }
        }

        if (httpContext.User.Identity?.AuthenticationType == IdentityConstants.ApplicationScheme)
            await httpContext.SignOutAsync(IdentityConstants.ApplicationScheme);

        return Results.NoContent();
    }

    /// <summary>
    /// Re-authentication for account deletion: the current password (checked with lockout), or — for
    /// an account without one, or a caller that did not supply it — a sign-in no older than
    /// <see cref="SparkAuthenticationOptions.ReauthenticationMaxAge"/>.
    /// </summary>
    private async Task<bool> IsReauthenticatedAsync(TUser user, string? password)
    {
        if (!string.IsNullOrEmpty(password))
        {
            var check = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
            return check.Succeeded;
        }

        if (signInManager is not SparkSignInManager<TUser> spark || await spark.GetAuthenticatedAtAsync() is not { } at)
            return false;

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return now - at <= options.Value.ReauthenticationMaxAge;
    }
}
