using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.IdentityProvider.Configuration;
using MintPlayer.Spark.IdentityProvider.Indexes;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.IdentityProvider;

/// <summary>
/// The periodic sweep that keeps <c>OidcTokens</c> from growing without bound.
/// </summary>
public class OidcTokenCleanupServiceTests : SparkTestDriver
{
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, exception));
    }

    private static OidcToken Token(string id, string status, TimeSpan expiresIn) => new()
    {
        Id = id,
        ApplicationId = "OidcApplications/1",
        Subject = "users/1",
        Type = "refresh_token",
        Status = status,
        CreatedAt = DateTime.UtcNow.AddDays(-30),
        ExpiresAt = DateTime.UtcNow + expiresIn,
    };

    private static async Task RunOnceAsync(OidcTokenCleanupService service, Func<Task> until)
    {
        await service.StartAsync(CancellationToken.None);
        try
        {
            await until();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    private async Task<bool> ExistsAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.Advanced.ExistsAsync(id);
    }

    /// <summary>
    /// Every token past its expiry is dead weight, whatever its status. The sweep used to select
    /// only <c>valid</c> and <c>redeemed</c>, so revoked tokens — every replay teardown, every
    /// withdrawn grant, every explicit revocation — and codes marked <c>expired</c> were kept
    /// forever. Keeping them buys nothing: the sweep already deletes expired <c>redeemed</c> tokens,
    /// which are the ones replay detection actually keys on, and a presented token whose document
    /// is gone is refused exactly as an expired one is.
    /// </summary>
    [Fact]
    public async Task Every_expired_token_is_deleted_whatever_its_status_and_live_ones_are_kept()
    {
        await new OidcTokens_ByExpiration().ExecuteAsync(Store);
        await SeedAsync(async session =>
        {
            await session.StoreAsync(Token("OidcTokens/expired-valid", "valid", TimeSpan.FromDays(-1)));
            await session.StoreAsync(Token("OidcTokens/expired-redeemed", "redeemed", TimeSpan.FromDays(-1)));
            await session.StoreAsync(Token("OidcTokens/expired-revoked", "revoked", TimeSpan.FromDays(-1)));
            await session.StoreAsync(Token("OidcTokens/expired-expired", "expired", TimeSpan.FromDays(-1)));
            await session.StoreAsync(Token("OidcTokens/live-valid", "valid", TimeSpan.FromDays(1)));
            await session.StoreAsync(Token("OidcTokens/live-revoked", "revoked", TimeSpan.FromDays(1)));
        });

        var provider = new ServiceCollection().AddSingleton(Store).BuildServiceProvider();
        var service = new OidcTokenCleanupService(
            provider,
            new SparkIdentityProviderOptions { TokenCleanupInterval = TimeSpan.FromHours(1) },
            new RecordingLogger<OidcTokenCleanupService>());

        // The sweep deletes in one SaveChanges, so once one expired token is gone all are.
        await RunOnceAsync(service, () => AsyncWait.ForAsync(
            async () => await ExistsAsync("OidcTokens/expired-valid") ? "present" : "gone",
            state => state == "gone",
            "the first sweep to delete the expired valid token"));

        (await ExistsAsync("OidcTokens/expired-redeemed")).Should().BeFalse();
        (await ExistsAsync("OidcTokens/expired-revoked")).Should().BeFalse("a revoked token past its expiry protects nothing");
        (await ExistsAsync("OidcTokens/expired-expired")).Should().BeFalse();
        (await ExistsAsync("OidcTokens/live-valid")).Should().BeTrue();
        (await ExistsAsync("OidcTokens/live-revoked")).Should().BeTrue(
            "an unexpired revoked token is still what refuses a replay of it — it must stay");
    }

    [Fact]
    public async Task A_failing_sweep_is_logged_and_the_loop_keeps_running()
    {
        // No IDocumentStore registered: every sweep throws inside the loop.
        var provider = new ServiceCollection().BuildServiceProvider();
        var logger = new RecordingLogger<OidcTokenCleanupService>();
        var service = new OidcTokenCleanupService(
            provider,
            new SparkIdentityProviderOptions { TokenCleanupInterval = TimeSpan.FromMilliseconds(20) },
            logger);

        await RunOnceAsync(service, () => AsyncWait.UntilAsync(
            () => logger.Entries.Count(e => e.Level == LogLevel.Error) >= 2,
            "two failed sweeps to be logged"));

        logger.Entries.Any(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException).Should().BeTrue();
    }
}
