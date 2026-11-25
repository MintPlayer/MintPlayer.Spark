using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Messaging.Models;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// The session a claimed message is handled in (#536 D40): <see cref="MessageProcessor"/>, the handler's
/// <see cref="MessageCheckpoint"/> and <see cref="MessageProgress"/> all write through it.
/// <para>
/// <b>Why it is not a bare <see cref="ISparkSession"/>.</b> <see cref="ClaimedExecution"/> renews the claim from its
/// own session while the handlers run, so the stored message moves on under this one. RavenDB's session is
/// last-write-wins and never noticed; a relational store checks a loaded row's version on every UPDATE, so the
/// owner's next save failed after any renewal. <see cref="SaveChangesAsync"/> tells the two kinds of conflict apart:
/// when the stored message is still <see cref="EMessageStatus.Processing"/> under this owner, only a renewal can have
/// written it, and the save is retried against the version it now has (the save carries a claim expiry at least as
/// late, <see cref="MessageProcessor.ExtendOwnClaim"/>). Anything else — reclaimed, or claimed elsewhere — is a lost
/// claim, and the conflict is thrown as before.
/// </para>
/// </summary>
internal sealed class MessageSession(ISparkStore store, ISparkSession session, SparkMessage message, string ownerId)
{
    private const int Attempts = 3;
    private readonly List<string> sidecarsToExpire = [];
    private DateTimeOffset? sidecarExpiry;

    public ISparkSession Session { get; } = session;

    public SparkMessage Message { get; } = message;

    /// <summary>
    /// Gives each progress sidecar in <paramref name="ids"/> the expiry <paramref name="expiresAt"/> in the next save, so
    /// it commits with the terminal message: a sidecar must not outlive its message, nor die before it.
    /// </summary>
    public void ExpireSidecars(IEnumerable<string> ids, DateTimeOffset expiresAt)
    {
        sidecarsToExpire.AddRange(ids);
        sidecarExpiry = expiresAt;
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        await ApplySidecarExpiriesAsync(cancellationToken);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Session.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (SparkStoreConcurrencyException) when (attempt < Attempts)
            {
                if (await RenewedOnlyAsync(cancellationToken) is not { } version)
                    throw;
                await Session.StoreAsync(Message, version, Message.Id!, cancellationToken);
            }
        }
    }

    /// <summary>The stored version, when the stored message is still this owner's claim; null when the claim is lost.</summary>
    private async Task<string?> RenewedOnlyAsync(CancellationToken cancellationToken)
    {
        using var probe = store.OpenSession();
        var stored = await probe.LoadAsync<SparkMessage>(Message.Id!, cancellationToken);
        return stored is { Status: EMessageStatus.Processing } && stored.OwnerId == ownerId ? probe.GetVersion(stored) : null;
    }

    /// <summary>
    /// The sidecars are re-read before the expiry is set: <see cref="MessageProgress"/> appends steps by patch, so a
    /// copy this session tracks may lack the latest ones, and writing it back whole would drop them.
    /// </summary>
    private async Task ApplySidecarExpiriesAsync(CancellationToken cancellationToken)
    {
        if (sidecarsToExpire.Count == 0 || sidecarExpiry is not { } expiresAt)
            return;

        var ids = sidecarsToExpire.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        sidecarsToExpire.Clear();
        foreach (var id in ids.Where(Session.IsLoaded))
            if (await Session.LoadAsync<SparkMessageProgress>(id, cancellationToken) is { } tracked)
                Session.Evict(tracked);

        foreach (var sidecar in (await Session.LoadAsync<SparkMessageProgress>(ids, cancellationToken)).Values)
            if (sidecar is not null)
                Session.SetExpiry(sidecar, expiresAt);
    }
}
