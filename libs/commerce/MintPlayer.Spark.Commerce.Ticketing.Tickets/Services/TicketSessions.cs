using MintPlayer.Spark.Commerce.Ticketing.Tickets.Models;
using MintPlayer.Spark.Commerce.Ticketing.Tickets.Secrets;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Commerce.Ticketing.Tickets.Services;

/// <summary>
/// Ticket writes: ordinary sessions with optimistic concurrency, retried on a conflict (two scans of one ticket, a scan
/// beside a transfer). A unit of work must be repeatable: it reloads what it changes.
/// </summary>
internal static class TicketSessions
{
    public const int MaxAttempts = 8;

    public static async Task<T> RetryAsync<T>(IDocumentStore store, Func<IAsyncDocumentSession, Task<T>> work, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var session = store.OpenAsyncSession();
            session.Advanced.UseOptimisticConcurrency = true;
            try
            {
                var result = await work(session);
                await session.SaveChangesAsync(cancellationToken);
                return result;
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
                await Task.Delay(Random.Shared.Next(5, 20 * attempt), cancellationToken);
            }
        }
    }

    public static Task RetryAsync(IDocumentStore store, Func<IAsyncDocumentSession, Task> work, CancellationToken cancellationToken)
        => RetryAsync<bool>(store, async s => { await work(s); return true; }, cancellationToken);

    /// <summary>
    /// Draws a new secret for <paramref name="ticket"/> (a reissue: transfer, resale, seat change), bumps its generation
    /// and makes delivery due again. The previous QR code and ticket link stop working at once (§4.11).
    /// </summary>
    /// <returns>The new secret, for the caller to show once (the vault keeps the encrypted copy).</returns>
    public static async Task<string> NewGenerationAsync(IAsyncDocumentSession session, Ticket ticket, TicketSecretVault vault, int iterations, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var secret = TicketSecrets.NewSecret();
        var verifier = TicketSecrets.Hash(secret, iterations);
        ticket.Generation++;
        ticket.SecretSalt = verifier.Salt;
        ticket.SecretHash = verifier.Hash;
        ticket.SecretIterations = verifier.Iterations;
        ticket.DeliveryDue = ticket.Status != ETicketStatus.Revoked;
        ticket.DeliverAt = ticket.AvailableFrom is { } from && from > now ? from : now;
        ticket.UpdatedAt = now;
        await StoreSecretAsync(session, ticket, secret, vault, cancellationToken);
        return secret;
    }

    /// <summary>Stores (or replaces) the vault copy of the ticket's current secret.</summary>
    public static async Task StoreSecretAsync(IAsyncDocumentSession session, Ticket ticket, string secret, TicketSecretVault vault, CancellationToken cancellationToken)
    {
        var fresh = vault.Protect(ticket.Code, secret, ticket.Generation);
        var stored = await session.LoadAsync<TicketSecret>(fresh.Id, cancellationToken);
        if (stored is null)
        {
            await session.StoreAsync(fresh, fresh.Id, cancellationToken);
            return;
        }
        stored.Protected = fresh.Protected;
        stored.Generation = fresh.Generation;
    }
}
