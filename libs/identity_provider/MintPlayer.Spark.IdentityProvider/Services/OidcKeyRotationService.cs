using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MintPlayer.Spark.IdentityProvider.Services;

/// <summary>
/// Advances the signing-key rotation schedule every six hours (<see cref="OidcKeyRing.RotateAsync"/>,
/// <c>Spark:IdentityProvider:Keys</c>). Every instance runs it; the schedule's steps are idempotent, so
/// two instances agree on what is due.
/// </summary>
internal sealed class OidcKeyRotationService(OidcKeyRing ring, ILogger<OidcKeyRotationService> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ring.RotateAsync(force: false, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Identity provider key rotation failed; the current keys keep signing.");
            }
        }
    }
}
