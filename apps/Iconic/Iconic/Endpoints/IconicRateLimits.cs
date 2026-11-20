using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Iconic.Endpoints;

/// <summary>
/// The editor's own rate limits (D21), on top of Spark's per-IP limiter for the prefix: saves per user, and reports
/// per address, since a report mails a person.
/// </summary>
public static class IconicRateLimits
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
}
