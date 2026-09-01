using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Webhooks.GitHub.Services;
using Octokit;

namespace CodeCoverage.Services;

public interface IGitHubAppReadinessService
{
    /// <summary>Cached tri-state probe of the GitHub App credentials; see <see cref="GitHubAppReadiness"/>.</summary>
    Task<GitHubAppReadinessResult> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// skipped: no App configured (the normal dev state — never fails readiness).
/// ready: the App JWT round-tripped to GitHub.
/// degraded: GitHub unreachable/5xx — inconclusive, must not fail readiness.
/// failed: the key is decisively unusable (401, or unreadable/malformed PEM).
/// </summary>
public static class GitHubAppReadiness
{
    public const string Skipped = "skipped";
    public const string Ready = "ready";
    public const string Degraded = "degraded";
    public const string Failed = "failed";
}

public sealed record GitHubAppReadinessResult(string Status, string? Detail);

/// <summary>
/// Probes that the configured GitHub App private key actually authenticates,
/// by minting an App JWT and calling <c>GET /app</c>. Only App-authenticated
/// calls exercise the key — webhooks use the secret and uploads use
/// covt_/OIDC tokens — so a wrong key otherwise stays invisible until
/// check-runs fail hours later (#13 U1). Result is cached: the probe is meant
/// for /health/ready polled by deploys, not to spend a GitHub call per hit.
/// </summary>
[Register(typeof(IGitHubAppReadinessService), ServiceLifetime.Singleton)]
public partial class GitHubAppReadinessService : IGitHubAppReadinessService
{
    [Inject] private readonly IServiceScopeFactory scopeFactory;
    [Inject] private readonly IConfiguration configuration;
    [Inject] private readonly IWebHostEnvironment environment;
    [Inject] private readonly ILogger<GitHubAppReadinessService> logger;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim gate = new(1, 1);
    private GitHubAppReadinessResult? cached;
    private DateTime cachedAtUtc;

    public async Task<GitHubAppReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (cached is not null && DateTime.UtcNow - cachedAtUtc < CacheDuration)
            return cached;

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cached is not null && DateTime.UtcNow - cachedAtUtc < CacheDuration)
                return cached;

            var result = await ProbeAsync(cancellationToken);
            // A decisive answer is worth caching; an inconclusive one should
            // be retried on the next poll rather than pinning "degraded".
            if (result.Status != GitHubAppReadiness.Degraded)
            {
                cached = result;
                cachedAtUtc = DateTime.UtcNow;
            }
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<GitHubAppReadinessResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var envPrefix = environment.EnvironmentName;
        var privateKeyPath = configuration[$"GitHub:{envPrefix}:PrivateKeyPath"];
        var appIdConfigured = long.TryParse(configuration[$"GitHub:{envPrefix}:AppId"], out _);
        if (string.IsNullOrEmpty(privateKeyPath) || !appIdConfigured)
            return new(GitHubAppReadiness.Skipped, "no GitHub App configured");

        try
        {
            using var scope = scopeFactory.CreateScope();
            var installationService = scope.ServiceProvider.GetRequiredService<IGitHubInstallationService>();
            var client = await installationService.CreateAppClientAsync();
            var app = await client.GitHubApps.GetCurrent();
            return new(GitHubAppReadiness.Ready, $"authenticated as {app.Slug}");
        }
        catch (Exception ex)
        {
            var result = Classify(ex);
            logger.LogWarning(ex, "GitHub App readiness probe: {Status}", result.Status);
            return result;
        }
    }

    /// <summary>
    /// failed only on decisive evidence the key is unusable; anything that
    /// could be GitHub having a bad minute is degraded, so an outage can't
    /// take readiness down with it.
    /// </summary>
    public static GitHubAppReadinessResult Classify(Exception ex) => ex switch
    {
        AuthorizationException => new(GitHubAppReadiness.Failed,
            "GitHub rejected the App JWT — the private key does not belong to the configured App. " +
            "Verify the PEM fingerprint against the App's General page."),
        // Key file missing/unreadable/a-directory, or not parseable as a key.
        IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException
            => new(GitHubAppReadiness.Failed, $"the App private key is unusable: {ex.Message}"),
        _ => new(GitHubAppReadiness.Degraded, $"probe inconclusive: {ex.Message}"),
    };
}
