using System.Threading.RateLimiting;
using Iconic.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Iconic.Endpoints;

/// <summary>
/// The editor's API under <c>/api/iconic</c> (docs/iconic_PRD.md §6 Stage 0). Every state-changing call needs the
/// antiforgery header; saves are rate-limited per user (D21) on top of Spark's per-IP limiter for the prefix.
/// </summary>
public static class IconicEndpoints
{
    public const string SavePolicy = "iconic-save";
    public const string ReportPolicy = "iconic-report";

    public static IServiceCollection AddIconicRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        var savesPerMinute = configuration.GetValue("Iconic:Quotas:SavesPerMinute", 50);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(SavePolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.User.Identity?.IsAuthenticated == true
                    ? $"user:{context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value}"
                    : $"ip:{context.Connection.RemoteIpAddress}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = savesPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            // A report mails a person: a handful per address per hour is plenty.
            options.AddPolicy(ReportPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
        });
        return services;
    }

    public static IEndpointRouteBuilder MapIconicApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/iconic");
        var csrf = new RequireAntiforgeryTokenAttribute(true);

        var projects = api.MapGroup("/projects");
        projects.MapPost("", (CreateProjectRequest body, ProjectService s, CancellationToken ct) => s.CreateAsync(body, ct))
            .WithMetadata(csrf).RequireRateLimiting(SavePolicy);
        projects.MapGet("/{key}", (string key, ProjectService s, CancellationToken ct) => s.GetAsync(key, ct));
        projects.MapPut("/{key}/meta", (string key, UpdateProjectRequest body, ProjectService s, CancellationToken ct) => s.UpdateAsync(key, body, ct))
            .WithMetadata(csrf).RequireRateLimiting(SavePolicy);
        projects.MapPut("/{key}/scene", (string key, SaveSceneRequest body, ProjectService s, CancellationToken ct) => s.SaveSceneAsync(key, body, ct))
            .WithMetadata(csrf).RequireRateLimiting(SavePolicy)
            .WithMetadata(new RequestSizeLimitAttribute(8 * 1024 * 1024));
        projects.MapPatch("/{key}/scene", (string key, PatchSceneRequest body, ProjectService s, CancellationToken ct) => s.PatchSceneAsync(key, body, ct))
            .WithMetadata(csrf).RequireRateLimiting(SavePolicy)
            .WithMetadata(new RequestSizeLimitAttribute(8 * 1024 * 1024));

        projects.MapPut("/{key}/files/{**name}", (string key, string name, HttpRequest http, ProjectService s, CancellationToken ct) => s.PutFileAsync(key, name, http, ct))
            .WithMetadata(csrf).RequireRateLimiting(SavePolicy)
            .WithMetadata(new RequestSizeLimitAttribute(11 * 1024 * 1024));
        projects.MapGet("/{key}/files/{**name}", (string key, string name, ProjectService s, CancellationToken ct) => s.GetFileAsync(key, name, ct));
        projects.MapDelete("/{key}/files/{**name}", (string key, string name, ProjectService s, CancellationToken ct) => s.DeleteFileAsync(key, name, ct))
            .WithMetadata(csrf);

        projects.MapPost("/{key}/snapshots", (string key, SnapshotRequest body, ProjectService s, CancellationToken ct) => s.CreateSnapshotAsync(key, body, ct))
            .WithMetadata(csrf).RequireRateLimiting(SavePolicy);
        projects.MapPost("/{key}/snapshots/{snapshotId}/restore", (string key, string snapshotId, ProjectService s, CancellationToken ct) => s.RestoreSnapshotAsync(key, snapshotId, ct))
            .WithMetadata(csrf).RequireRateLimiting(SavePolicy);
        projects.MapDelete("/{key}/snapshots/{snapshotId}", (string key, string snapshotId, ProjectService s, CancellationToken ct) => s.DeleteSnapshotAsync(key, snapshotId, ct))
            .WithMetadata(csrf);

        projects.MapPost("/{key}/share", (string key, ShareRequest body, ProjectService s, CancellationToken ct) => s.ShareAsync(key, body, ct))
            .WithMetadata(csrf);

        var shared = api.MapGroup("/shared");
        shared.MapGet("/{token}", (string token, ProjectService s, CancellationToken ct) => s.GetSharedAsync(token, ct)).AllowAnonymous();
        shared.MapGet("/{token}/files/{**name}", (string token, string name, ProjectService s, CancellationToken ct) => s.GetSharedFileAsync(token, name, ct)).AllowAnonymous();
        shared.MapPost("/{token}/remix", (string token, ProjectService s, CancellationToken ct) => s.RemixAsync(token, ct))
            .WithMetadata(csrf).RequireRateLimiting(SavePolicy);
        shared.MapPost("/{token}/report", (string token, ReportRequest body, ProjectService s, IConfiguration configuration, HttpRequest http, CancellationToken ct)
                => s.ReportAsync(token, body, configuration["Iconic:PublicBaseUrl"] ?? $"{http.Scheme}://{http.Host}", ct))
            .AllowAnonymous().WithMetadata(csrf).RequireRateLimiting(ReportPolicy);

        api.MapPost("/admin/takedown", (TakedownRequest body, ProjectService s, CancellationToken ct) => s.TakedownAsync(body, ct))
            .WithMetadata(csrf);

        return endpoints;
    }
}
