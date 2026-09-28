using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Moderation;

namespace QnA.Testing;

/// <summary>
/// End-to-end test seams: run Moderation's two Cron jobs now instead of waiting for their schedule.
/// A privilege earned by a vote counts only from the crediting run that credits it (every 5 minutes),
/// and the fraud detector runs nightly, so a test that waited would be slow and still racy.
/// </summary>
/// <remarks>
/// <para>
/// Off unless <c>QnA:TestSeams:Enabled</c> is true, which only the E2E host's generated settings set.
/// Refused at startup in Production. Even when on, only a moderator (a holder of
/// <c>Audit/Moderation</c>) gets an answer other than 404, and every call needs the antiforgery token.
/// </para>
/// <para>
/// The seams call <see cref="ISparkModerationJobs"/> — the jobs' own code — so the rules are exactly the
/// scheduled ones: crediting still credits only entries whose delay has passed.
/// </para>
/// </remarks>
public static class QnATestSeams
{
    public const string EnabledKey = "QnA:TestSeams:Enabled";

    /// <summary>Whether the seams are on. Throws when they are configured on in Production.</summary>
    public static bool IsEnabled(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>(EnabledKey))
            return false;
        if (environment.IsProduction())
            throw new InvalidOperationException($"'{EnabledKey}' is set in Production. The QnA test seams run Moderation's jobs on request; they exist for the E2E host only.");
        return true;
    }

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/qna-test/moderation")
            .WithMetadata(new RequireAntiforgeryTokenAttribute(true));

        group.MapPost("/credit", async (HttpContext http, IPermissionService permissions, ISparkModerationJobs jobs) =>
            await IsModeratorAsync(permissions)
                ? Results.Json(new { credited = await jobs.RunCreditingAsync(http.RequestAborted) })
                : Results.NotFound());

        group.MapPost("/detect", async (HttpContext http, IPermissionService permissions, ISparkModerationJobs jobs) =>
            await IsModeratorAsync(permissions)
                ? Results.Json(await jobs.RunFraudDetectorAsync(http.RequestAborted))
                : Results.NotFound());

        group.MapPost("/recompute", async (HttpContext http, RecomputeRequest request, IPermissionService permissions, ISparkModerationJobs jobs) =>
        {
            if (!await IsModeratorAsync(permissions))
                return Results.NotFound();
            await jobs.RecomputeReputationAsync(request.UserIds ?? [], http.RequestAborted);
            return Results.NoContent();
        });
    }

    private static Task<bool> IsModeratorAsync(IPermissionService permissions)
        => permissions.IsAllowedAsync(ModerationRights.Audit, ModerationRights.Target);

    public sealed record RecomputeRequest(List<string>? UserIds);
}
