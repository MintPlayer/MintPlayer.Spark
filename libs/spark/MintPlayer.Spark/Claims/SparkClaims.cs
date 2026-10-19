using MintPlayer.SourceGenerators.Attributes;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.CompareExchange;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Claims;

/// <summary>
/// Claims over RavenDB compare-exchange (commerce PRD X12): reserve, convert and release a cluster-wide,
/// single-owner key, with lazy expiry. One primitive for what Spark used to hand-write per feature (the email and
/// client-id reservations, the messaging and dashboard-sampler leases) and what commerce needs next (seats,
/// per-buyer limits, single-use admission tokens, one queue entry per identity).
/// </summary>
/// <remarks>
/// <para>
/// Every write is guarded by the compare-exchange index it read (0 for "must not exist"), so two contenders never
/// both win. A reservation is idempotent by owner: reserving a claim the caller already holds answers
/// <see cref="SparkClaimStatus.AlreadyYours"/> and writes nothing. An expired (or abandoned) claim is taken over by
/// the next reservation; nothing has to delete it first.
/// </para>
/// <para>
/// Several keys and documents that must change together go through <see cref="BeginUnitOfWork"/>: one cluster-wide
/// session that commits all or nothing.
/// </para>
/// </remarks>
public interface ISparkClaims
{
    /// <summary>The live claim at <paramref name="key"/>, or null when absent or expired.</summary>
    Task<SparkClaim<TValue>?> GetAsync<TValue>(SparkClaimKind<TValue> kind, SparkClaimKey key, CancellationToken cancellationToken = default)
        where TValue : class;

    /// <summary>
    /// Claims <paramref name="key"/> for <paramref name="owner"/>.
    /// </summary>
    /// <param name="create">
    /// The value to store, given the expired or abandoned claim it takes over (null when the key is free). Return null
    /// to write nothing (<see cref="SparkClaimStatus.Declined"/>), for example when the previous value says the work
    /// was done already.
    /// </param>
    /// <param name="nowUtc">The instant expiry is judged at; default the registered <see cref="TimeProvider"/>.</param>
    Task<SparkClaimResult<TValue>> ReserveAsync<TValue>(
        SparkClaimKind<TValue> kind, SparkClaimKey key, string owner, Func<SparkClaim<TValue>?, TValue?> create,
        DateTime? nowUtc = null, CancellationToken cancellationToken = default)
        where TValue : class;

    /// <summary>
    /// Changes <paramref name="owner"/>'s claim in place: a renewal, a conversion (a hold made permanent), new data.
    /// Refused (<see cref="SparkClaimStatus.NotYours"/>) when the claim is absent or another owner's. Return null from
    /// <paramref name="change"/> to write nothing.
    /// </summary>
    Task<SparkClaimResult<TValue>> UpdateAsync<TValue>(
        SparkClaimKind<TValue> kind, SparkClaimKey key, string owner, Func<TValue, TValue?> change, CancellationToken cancellationToken = default)
        where TValue : class;

    /// <summary>Deletes <paramref name="owner"/>'s claim. Another owner's claim is never released.</summary>
    Task<SparkClaimResult<TValue>> ReleaseAsync<TValue>(
        SparkClaimKind<TValue> kind, SparkClaimKey key, string owner, CancellationToken cancellationToken = default)
        where TValue : class;

    /// <summary>
    /// Opens a cluster-wide unit of work: claims and documents staged in it commit together or not at all.
    /// </summary>
    /// <param name="atomicDocumentWrites">
    /// RavenDB's atomic guards on the documents the unit of work writes. Turn off when those documents are also
    /// written by ordinary sessions, which leave no guard to check.
    /// </param>
    ISparkClaimsUnitOfWork BeginUnitOfWork(bool atomicDocumentWrites = true);
}

/// <summary>
/// Claims and documents that commit together, in one RavenDB cluster-wide session (<see cref="ISparkClaims.BeginUnitOfWork"/>).
/// Each operation is judged on what the unit of work read and is staged; nothing is written before
/// <see cref="CommitAsync"/>, which fails as a whole when any key or guarded document it read changed meanwhile.
/// </summary>
/// <remarks>
/// RavenDB refuses optimistic concurrency and patches in a cluster-wide session: <see cref="Session"/> may load,
/// store and delete documents, nothing else. The staged statuses are what the commit will write;
/// <see cref="SparkClaimStatus.Conflict"/> never appears before the commit.
/// </remarks>
public interface ISparkClaimsUnitOfWork : IDisposable
{
    /// <summary>The cluster-wide session the documents go through.</summary>
    IAsyncDocumentSession Session { get; }

    /// <inheritdoc cref="ISparkClaims.ReserveAsync{TValue}"/>
    Task<SparkClaimResult<TValue>> ReserveAsync<TValue>(
        SparkClaimKind<TValue> kind, SparkClaimKey key, string owner, Func<SparkClaim<TValue>?, TValue?> create,
        DateTime? nowUtc = null, CancellationToken cancellationToken = default)
        where TValue : class;

    /// <inheritdoc cref="ISparkClaims.UpdateAsync{TValue}"/>
    Task<SparkClaimResult<TValue>> UpdateAsync<TValue>(
        SparkClaimKind<TValue> kind, SparkClaimKey key, string owner, Func<TValue, TValue?> change, CancellationToken cancellationToken = default)
        where TValue : class;

    /// <inheritdoc cref="ISparkClaims.ReleaseAsync{TValue}"/>
    Task<SparkClaimResult<TValue>> ReleaseAsync<TValue>(
        SparkClaimKind<TValue> kind, SparkClaimKey key, string owner, CancellationToken cancellationToken = default)
        where TValue : class;

    /// <summary>Writes everything staged. False when a key or document read here changed since: nothing was written.</summary>
    Task<bool> CommitAsync(CancellationToken cancellationToken = default);
}

/// <summary>The hold lifecycle (<see cref="SparkClaimHold"/>): reserve until a deadline, convert to permanent, release.</summary>
public static class SparkClaimHoldExtensions
{
    /// <summary>
    /// Holds <paramref name="key"/> for <paramref name="owner"/> until <paramref name="expiresAtUtc"/> (null: permanent).
    /// Idempotent by owner: the caller's own hold answers <see cref="SparkClaimStatus.AlreadyYours"/> and keeps its deadline.
    /// </summary>
    public static Task<SparkClaimResult<SparkClaimHold>> ReserveHoldAsync(
        this ISparkClaims claims, SparkClaimKind<SparkClaimHold> kind, SparkClaimKey key, string owner, DateTime? expiresAtUtc,
        string? data = null, CancellationToken cancellationToken = default)
        => claims.ReserveAsync(kind, key, owner, _ => new SparkClaimHold(owner, expiresAtUtc, data), cancellationToken: cancellationToken);

    /// <summary>Makes <paramref name="owner"/>'s hold permanent (a seat paid for, a token spent): it no longer expires.</summary>
    public static Task<SparkClaimResult<SparkClaimHold>> ConvertAsync(
        this ISparkClaims claims, SparkClaimKind<SparkClaimHold> kind, SparkClaimKey key, string owner, CancellationToken cancellationToken = default)
        => claims.UpdateAsync(kind, key, owner, hold => hold with { ExpiresAtUtc = null }, cancellationToken);

    /// <inheritdoc cref="ReserveHoldAsync(ISparkClaims, SparkClaimKind{SparkClaimHold}, SparkClaimKey, string, DateTime?, string?, CancellationToken)"/>
    public static Task<SparkClaimResult<SparkClaimHold>> ReserveHoldAsync(
        this ISparkClaimsUnitOfWork unitOfWork, SparkClaimKind<SparkClaimHold> kind, SparkClaimKey key, string owner, DateTime? expiresAtUtc,
        string? data = null, CancellationToken cancellationToken = default)
        => unitOfWork.ReserveAsync(kind, key, owner, _ => new SparkClaimHold(owner, expiresAtUtc, data), cancellationToken: cancellationToken);

    /// <inheritdoc cref="ConvertAsync(ISparkClaims, SparkClaimKind{SparkClaimHold}, SparkClaimKey, string, CancellationToken)"/>
    public static Task<SparkClaimResult<SparkClaimHold>> ConvertAsync(
        this ISparkClaimsUnitOfWork unitOfWork, SparkClaimKind<SparkClaimHold> kind, SparkClaimKey key, string owner, CancellationToken cancellationToken = default)
        => unitOfWork.UpdateAsync(kind, key, owner, hold => hold with { ExpiresAtUtc = null }, cancellationToken);
}

/// <summary>
/// The compare-exchange implementation of <see cref="ISparkClaims"/>. Registered as a singleton; constructible
/// directly (<c>new SparkClaims(store)</c>) by code that runs outside the container.
/// </summary>
[Register(typeof(ISparkClaims), ServiceLifetime.Singleton)]
public sealed class SparkClaims : ISparkClaims
{
    // A create that lost to a concurrent create, or a takeover that lost to another, re-reads once: the
    // winner may be the caller's own owner (an idempotent retry), or may have released again.
    private const int Attempts = 2;

    private readonly IDocumentStore documentStore;
    private readonly TimeProvider timeProvider;
    private readonly string? database;

    /// <param name="documentStore">The store the claims live in.</param>
    /// <param name="timeProvider">The clock expiry is judged by; default <see cref="TimeProvider.System"/>.</param>
    /// <param name="database">The database; default the store's.</param>
    public SparkClaims(IDocumentStore documentStore, TimeProvider? timeProvider = null, string? database = null)
    {
        this.documentStore = documentStore;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.database = database;
    }

    private OperationExecutor Operations => database is null ? documentStore.Operations : documentStore.Operations.ForDatabase(database);

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public async Task<SparkClaim<TValue>?> GetAsync<TValue>(SparkClaimKind<TValue> kind, SparkClaimKey key, CancellationToken cancellationToken = default)
        where TValue : class
    {
        var claim = await ReadAsync(kind, key, cancellationToken);
        return claim is not null && kind.IsLiveAt(claim, UtcNow) ? claim : null;
    }

    /// <inheritdoc />
    public async Task<SparkClaimResult<TValue>> ReserveAsync<TValue>(
        SparkClaimKind<TValue> kind, SparkClaimKey key, string owner, Func<SparkClaim<TValue>?, TValue?> create,
        DateTime? nowUtc = null, CancellationToken cancellationToken = default)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(owner);
        var now = nowUtc ?? UtcNow;
        SparkClaimResult<TValue> outcome = new(SparkClaimStatus.Conflict, null);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var current = await ReadAsync(kind, key, cancellationToken);
            var judged = await JudgeAsync(kind, current, owner, now, cancellationToken);
            if (judged is { } refusal)
                return refusal;

            var value = create(current);
            if (value is null)
                return new(SparkClaimStatus.Declined, current);

            // Index 0 succeeds only while the key does not exist; a takeover compares the index it read.
            var put = await Operations.SendAsync(
                new PutCompareExchangeValueOperation<TValue>(key.Value, value, current?.Index ?? 0), token: cancellationToken);
            if (put.Successful)
                return new(SparkClaimStatus.Reserved, new SparkClaim<TValue>(key, value, put.Index));

            // The failed put answers with what is stored: the caller's own owner won an earlier attempt.
            if (put.Value is { } stored && kind.IsOwnedBy(stored, owner))
                return new(SparkClaimStatus.AlreadyYours, new SparkClaim<TValue>(key, stored, put.Index));

            outcome = new(SparkClaimStatus.Conflict, put.Value is { } other ? new SparkClaim<TValue>(key, other, put.Index) : null);
        }

        return outcome;
    }

    /// <inheritdoc />
    public async Task<SparkClaimResult<TValue>> UpdateAsync<TValue>(
        SparkClaimKind<TValue> kind, SparkClaimKey key, string owner, Func<TValue, TValue?> change, CancellationToken cancellationToken = default)
        where TValue : class
    {
        var current = await ReadAsync(kind, key, cancellationToken);
        if (current is null || !kind.IsOwnedBy(current.Value, owner))
            return new(SparkClaimStatus.NotYours, current);

        var next = change(current.Value);
        if (next is null)
            return new(SparkClaimStatus.Declined, current);

        var put = await Operations.SendAsync(
            new PutCompareExchangeValueOperation<TValue>(key.Value, next, current.Index), token: cancellationToken);
        return put.Successful
            ? new(SparkClaimStatus.Updated, new SparkClaim<TValue>(key, next, put.Index))
            : new(SparkClaimStatus.Conflict, put.Value is { } stored ? new SparkClaim<TValue>(key, stored, put.Index) : null);
    }

    /// <inheritdoc />
    public async Task<SparkClaimResult<TValue>> ReleaseAsync<TValue>(
        SparkClaimKind<TValue> kind, SparkClaimKey key, string owner, CancellationToken cancellationToken = default)
        where TValue : class
    {
        var current = await ReadAsync(kind, key, cancellationToken);
        if (current is null || !kind.IsOwnedBy(current.Value, owner))
            return new(SparkClaimStatus.NotYours, current);

        var deleted = await Operations.SendAsync(
            new DeleteCompareExchangeValueOperation<TValue>(key.Value, current.Index), token: cancellationToken);
        return deleted.Successful
            ? new(SparkClaimStatus.Released, current)
            : new(SparkClaimStatus.Conflict, current);
    }

    /// <inheritdoc />
    public ISparkClaimsUnitOfWork BeginUnitOfWork(bool atomicDocumentWrites = true)
        => new SparkClaimsUnitOfWork(documentStore.OpenAsyncSession(new Raven.Client.Documents.Session.SessionOptions
        {
            Database = database,
            TransactionMode = TransactionMode.ClusterWide,
            DisableAtomicDocumentWritesInClusterWideTransaction = !atomicDocumentWrites,
        }), timeProvider);

    private async Task<SparkClaim<TValue>?> ReadAsync<TValue>(SparkClaimKind<TValue> kind, SparkClaimKey key, CancellationToken cancellationToken)
        where TValue : class
    {
        EnsureKind(kind, key);
        var stored = await Operations.SendAsync(new GetCompareExchangeValueOperation<TValue>(key.Value), token: cancellationToken);
        return stored?.Value is { } value ? new SparkClaim<TValue>(key, value, stored.Index) : null;
    }

    /// <summary>
    /// The refusal a reservation meets before it writes, or null when it may write (the key is free, or its claim
    /// expired or was abandoned). Shared with the unit of work so both judge alike.
    /// </summary>
    internal static async Task<SparkClaimResult<TValue>?> JudgeAsync<TValue>(
        SparkClaimKind<TValue> kind, SparkClaim<TValue>? current, string owner, DateTime nowUtc, CancellationToken cancellationToken)
        where TValue : class
    {
        if (current is null)
            return null;
        if (kind.IsOwnedBy(current.Value, owner))
            return new SparkClaimResult<TValue>(SparkClaimStatus.AlreadyYours, current);
        if (!kind.IsLiveAt(current, nowUtc))
            return null;
        if (kind.IsAbandonedAsync is { } abandoned && await abandoned(current, cancellationToken))
            return null;
        return new SparkClaimResult<TValue>(SparkClaimStatus.Taken, current);
    }

    internal static void EnsureKind<TValue>(SparkClaimKind<TValue> kind, SparkClaimKey key) where TValue : class
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (key.Value is null || !key.Value.StartsWith(kind.Prefix, StringComparison.Ordinal))
            throw new ArgumentException($"Claim key '{key.Value}' is not under its kind's prefix '{kind.Prefix}'.", nameof(key));
    }
}
