using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions.Visitor;
using MintPlayer.Spark.Challenges.Risk;
using MintPlayer.Spark.Claims;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.CompareExchange;

namespace MintPlayer.Spark.WaitingRoom.Admissions;

/// <summary>The sealed content of an admission token.</summary>
internal sealed record WaitingRoomTokenPayload(string Jti, string Room, string Phase, string Entry, string Sub, long Exp, bool Poisoned);

/// <summary>Seals and opens admission tokens (Data Protection; the expiry is in the payload, judged by the registered <see cref="TimeProvider"/>).</summary>
internal sealed class WaitingRoomTokens(IDataProtectionProvider dataProtection)
{
    internal const string ProtectorPurpose = "MintPlayer.Spark.WaitingRoom.Admission.v1";

    private IDataProtector Protector => dataProtection.CreateProtector(ProtectorPurpose);

    public (string Token, string Jti) Issue(string roomId, string phase, string entryId, string subject, DateTimeOffset expiresAt, bool poisoned)
    {
        var jti = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var payload = new WaitingRoomTokenPayload(jti, roomId, phase, entryId, subject, expiresAt.ToUnixTimeMilliseconds(), poisoned);
        return (Protector.Protect(JsonSerializer.Serialize(payload)), jti);
    }

    public WaitingRoomTokenPayload? Open(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096)
            return null;
        try
        {
            return JsonSerializer.Deserialize<WaitingRoomTokenPayload>(Protector.Unprotect(token.Trim()));
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }
}

internal sealed class WaitingRoomAdmissions(
    WaitingRoomTokens tokens,
    ISparkClaims claims,
    IDocumentStore store,
    IHttpContextAccessor accessor,
    IOptions<SparkWaitingRoomOptions> options,
    WaitingRoomGateCache gates,
    IServiceProvider services,
    TimeProvider time,
    ILogger<WaitingRoomAdmissions> logger) : IWaitingRoomAdmissions
{
    /// <summary>Spent markers: <c>waitingroom/tokens/{jti}</c>, owned by the consumer, until the token expires (plus a minute).</summary>
    internal static readonly SparkClaimKind<SparkClaimHold> Spent = SparkClaimKinds.Holds("waitingroom/tokens/");

    public Task<bool> IsGatedAsync(string phase, CancellationToken cancellationToken = default)
        => gates.IsGatedAsync(phase, options.Value.GateCacheDuration, cancellationToken);

    public async Task<WaitingRoomAdmissionCheck> ValidateAsync(string? token, string phase, string? identityKey = null, CancellationToken cancellationToken = default)
    {
        var (check, _) = await CheckAsync(token, phase, identityKey, cancellationToken);
        if (check.Status != WaitingRoomAdmissionStatus.Valid && check.Status != WaitingRoomAdmissionStatus.Poisoned)
            return check;
        // A poisoned token validates (the deceptive measure): it is refused only when it is spent.
        var spent = await claims.GetAsync(Spent, Spent.Key(check.Jti!), cancellationToken);
        return spent is null ? check with { Status = WaitingRoomAdmissionStatus.Valid } : check with { Status = WaitingRoomAdmissionStatus.Spent };
    }

    public async Task<WaitingRoomAdmissionCheck> ConsumeAsync(string? token, string phase, string consumer, string? identityKey = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        var (check, subject) = await CheckAsync(token, phase, identityKey, cancellationToken);
        if (check.Status == WaitingRoomAdmissionStatus.Forged)
            await RecordAsync(subject, "admission-token-forged", "an admission token that this application did not seal", cancellationToken);
        if (check.Status == WaitingRoomAdmissionStatus.Poisoned)
            return check;
        if (!check.IsValid)
            return check;

        var expires = check.ExpiresAt!.Value.UtcDateTime.AddMinutes(1);
        var claim = await claims.ReserveHoldAsync(Spent, Spent.Key(check.Jti!), consumer, expires, data: check.EntryId, cancellationToken: cancellationToken);
        if (claim.Holds)
            return check;
        await RecordAsync(subject, "admission-token-replayed", "an admission token already spent by another consumer", cancellationToken);
        return check with { Status = WaitingRoomAdmissionStatus.Spent };
    }

    /// <summary>The token's own checks; answers the identity judged against (for the evidence record).</summary>
    private async Task<(WaitingRoomAdmissionCheck Check, string Subject)> CheckAsync(string? token, string phase, string? identityKey, CancellationToken cancellationToken)
    {
        var visitor = accessor.HttpContext?.Features.Get<ISparkVisitor>();
        var subject = identityKey ?? visitor?.IdentityKey ?? "";
        if (string.IsNullOrWhiteSpace(token))
            return (new(WaitingRoomAdmissionStatus.Missing), subject);
        if (tokens.Open(token) is not { } payload)
            return (new(WaitingRoomAdmissionStatus.Forged), subject);

        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(payload.Exp);
        var check = new WaitingRoomAdmissionCheck(WaitingRoomAdmissionStatus.Valid, payload.Room, payload.Entry, payload.Jti, expiresAt);
        if (time.GetUtcNow() >= expiresAt)
            return (check with { Status = WaitingRoomAdmissionStatus.Expired }, subject);
        if (!string.Equals(payload.Phase, phase, StringComparison.Ordinal))
            return (check with { Status = WaitingRoomAdmissionStatus.WrongPhase }, subject);
        if (!await IsSameIdentityAsync(payload.Sub, subject, identityKey is null ? visitor?.UserId : UserOf(identityKey), cancellationToken))
            return (check with { Status = WaitingRoomAdmissionStatus.WrongIdentity }, subject);
        return (payload.Poisoned ? check with { Status = WaitingRoomAdmissionStatus.Poisoned } : check, subject);
    }

    /// <summary>The token's subject, or the visitor it was issued to who has since signed in as <paramref name="userId"/> (X1 merge record).</summary>
    private async Task<bool> IsSameIdentityAsync(string tokenSubject, string presenter, string? userId, CancellationToken cancellationToken)
    {
        if (string.Equals(tokenSubject, presenter, StringComparison.Ordinal))
            return true;
        if (userId is null || !tokenSubject.StartsWith("visitor:", StringComparison.Ordinal))
            return false;
        var merged = await store.Operations.SendAsync(
            new GetCompareExchangeValueOperation<string>("visitor-merge/" + tokenSubject["visitor:".Length..]), token: cancellationToken);
        return string.Equals(merged?.Value, userId, StringComparison.Ordinal);
    }

    private static string? UserOf(string identityKey) => identityKey.StartsWith("user:", StringComparison.Ordinal) ? identityKey["user:".Length..] : null;

    private async Task RecordAsync(string subject, string signal, string detail, CancellationToken cancellationToken)
    {
        if (accessor.HttpContext is not { } http || services.GetService<ISparkRiskPipeline>() is not { } pipeline || subject.Length == 0)
            return;
        try
        {
            var context = pipeline.ContextFor(http, "admission-consume") with { IdentityKey = subject };
            await pipeline.RecordAsync(context, new SparkRiskEvidence(signal, 1, Deterministic: true, Detail: detail, DeceptiveTrigger: true), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Recording {Signal} failed.", signal);
        }
    }
}

/// <summary>Which phases a room gates, cached briefly (asked on every hold).</summary>
internal sealed class WaitingRoomGateCache(IDocumentStore store, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (bool Gated, DateTimeOffset Until)> cache = new(StringComparer.Ordinal);

    public async Task<bool> IsGatedAsync(string phase, TimeSpan duration, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (cache.TryGetValue(phase, out var hit) && hit.Until > now)
            return hit.Gated;
        using var session = store.OpenAsyncSession();
        // Not stale: a room created a moment ago must gate its phase at once, or holds would pass without a token.
        var rooms = await session.Query<WaitingRoomDefinition>()
            .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
            .Where(r => r.Gate == phase)
            .ToListAsync(cancellationToken);
        var gated = rooms.Any(r => r.EndsAt is not { } ends || ends > now);
        cache[phase] = (gated, now + duration);
        return gated;
    }

    public void Invalidate() => cache.Clear();
}
