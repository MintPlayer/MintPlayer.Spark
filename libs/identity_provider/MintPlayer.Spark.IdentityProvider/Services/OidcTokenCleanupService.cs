using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;

namespace MintPlayer.Spark.IdentityProvider.Services;

internal class OidcTokenCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly SparkIdentityProviderOptions _options;
    private readonly ILogger<OidcTokenCleanupService> _logger;

    public OidcTokenCleanupService(
        IServiceProvider serviceProvider,
        SparkIdentityProviderOptions options,
        ILogger<OidcTokenCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupExpiredTokensAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during OIDC token cleanup");
            }

            await Task.Delay(_options.TokenCleanupInterval, stoppingToken);
        }
    }

    private async Task CleanupExpiredTokensAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.OpenAsyncSession();

        var now = DateTime.UtcNow;

        var expiredTokens = await session
            .Query<OidcToken, OidcTokens_ByExpiration>()
            .Where(t => t.ExpiresAt < now && (t.Status == "valid" || t.Status == "redeemed"))
            .Take(1000)
            .ToListAsync(cancellationToken);

        if (expiredTokens.Count == 0) return;

        foreach (var token in expiredTokens)
        {
            session.Delete(token);
        }

        await session.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Cleaned up {Count} expired OIDC tokens", expiredTokens.Count);
    }
}
