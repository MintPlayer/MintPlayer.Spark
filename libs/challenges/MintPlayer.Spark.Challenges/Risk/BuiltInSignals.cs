using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace MintPlayer.Spark.Challenges.Risk;

/// <summary>
/// The in-memory state of the built-in signals: deterministic evidence recorded against identities (a replayed token),
/// attempts per identity and point (the velocity rule), and distinct identities per network address. Per process: on a
/// multi-instance host each instance counts what it sees (the decision log holds every recorded case durably).
/// </summary>
internal sealed class SparkRiskMemory(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, List<(SparkRiskEvidence Evidence, DateTime Until)>> recorded = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Queue<DateTime>> attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Dictionary<string, DateTime>> addresses = new(StringComparer.Ordinal);
    private long touches;

    public DateTime Now => time.GetUtcNow().UtcDateTime;

    public void Record(string identityKey, SparkRiskEvidence evidence, TimeSpan lifetime)
    {
        var list = recorded.GetOrAdd(identityKey, _ => []);
        lock (list)
            list.Add((evidence, Now + lifetime));
    }

    public IReadOnlyList<SparkRiskEvidence> Recorded(string identityKey)
    {
        if (!recorded.TryGetValue(identityKey, out var list))
            return [];
        var now = Now;
        lock (list)
        {
            list.RemoveAll(r => r.Until <= now);
            return list.Select(r => r.Evidence).ToList();
        }
    }

    /// <summary>Counts one attempt and answers the attempts in the last minute, this one included.</summary>
    public int CountAttempt(string identityKey, string point)
    {
        var queue = attempts.GetOrAdd(identityKey + "|" + point, _ => new Queue<DateTime>());
        var now = Now;
        Prune();
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() <= now.AddMinutes(-1))
                queue.Dequeue();
            queue.Enqueue(now);
            return queue.Count;
        }
    }

    /// <summary>Records that <paramref name="identityKey"/> used <paramref name="addressKey"/> and answers the distinct identities seen there within <paramref name="window"/>.</summary>
    public int CountIdentitiesAt(string addressKey, string identityKey, TimeSpan window)
    {
        var seen = addresses.GetOrAdd(addressKey, _ => new Dictionary<string, DateTime>(StringComparer.Ordinal));
        var now = Now;
        lock (seen)
        {
            seen[identityKey] = now;
            if (seen.Count > 64)
                foreach (var stale in seen.Where(p => p.Value <= now - window).Select(p => p.Key).ToList())
                    seen.Remove(stale);
            return seen.Count(p => p.Value > now - window);
        }
    }

    /// <summary>Bounds memory: every few thousand calls, drop empty queues.</summary>
    private void Prune()
    {
        if (Interlocked.Increment(ref touches) % 4096 != 0)
            return;
        var cutoff = Now.AddMinutes(-1);
        foreach (var (key, queue) in attempts)
            lock (queue)
                if (queue.Count == 0 || queue.Last() <= cutoff)
                    attempts.TryRemove(key, out _);
    }
}

/// <summary>Deterministic evidence recorded against the identity (a replayed or forged token, a decoy hit). Blocks.</summary>
internal sealed class RecordedEvidenceSignal(SparkRiskMemory memory) : ISparkRiskSignal
{
    public string Name => "recorded";

    public SparkRiskBasis Basis => SparkRiskBasis.Identity;

    public ValueTask<SparkRiskEvidence?> EvaluateAsync(SparkRiskContext context, CancellationToken cancellationToken)
    {
        var recorded = memory.Recorded(context.IdentityKey);
        if (recorded.Count == 0)
            return ValueTask.FromResult<SparkRiskEvidence?>(null);
        var first = recorded[0];
        return ValueTask.FromResult<SparkRiskEvidence?>(first with { Signal = "recorded:" + first.Signal });
    }
}

/// <summary>The velocity rule: attempts per identity per point per minute beyond human plausibility slow the caller down (429).</summary>
internal sealed class VelocitySignal(SparkRiskMemory memory, IOptionsMonitor<SparkChallengesOptions> options) : ISparkRiskSignal
{
    public string Name => "velocity";

    public ValueTask<SparkRiskEvidence?> EvaluateAsync(SparkRiskContext context, CancellationToken cancellationToken)
    {
        var count = memory.CountAttempt(context.IdentityKey, context.Point);
        var limit = options.CurrentValue.Risk.VelocityPerMinute;
        return ValueTask.FromResult(count > limit
            ? new SparkRiskEvidence(Name, 0.2, Detail: $"{count} attempts in a minute (limit {limit})", Minimum: SparkRiskAction.Slow)
            : null);
    }
}

/// <summary>The honeypot: a hidden field a person never fills. Weak evidence (0.3, never decisive alone), but a deterministic trigger for the deceptive measures.</summary>
internal sealed class HoneypotSignal : ISparkRiskSignal
{
    public const string Fact = "honeypot";

    public string Name => "honeypot";

    public ValueTask<SparkRiskEvidence?> EvaluateAsync(SparkRiskContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(context.Facts.TryGetValue(Fact, out var value) && !string.IsNullOrEmpty(value)
            ? new SparkRiskEvidence(Name, 0.3, Detail: "hidden field filled", DeceptiveTrigger: true)
            : null);
}

/// <summary>The client's automation flag (<c>navigator.webdriver</c>): 0.3 when present; its absence proves nothing.</summary>
internal sealed class AutomationFlagSignal : ISparkRiskSignal
{
    public const string Fact = "webdriver";

    public string Name => "automation-flag";

    public SparkRiskBasis Basis => SparkRiskBasis.Device;

    public ValueTask<SparkRiskEvidence?> EvaluateAsync(SparkRiskContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(context.Facts.TryGetValue(Fact, out var value) && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            ? new SparkRiskEvidence(Name, 0.3, Detail: "navigator.webdriver reported")
            : null);
}

/// <summary>
/// Many identities from one network address (IPv4) or IPv6 /64 within minutes: at most 0.1, so a shared address alone
/// (carrier-grade NAT, an office, a venue's Wi-Fi, iCloud Private Relay) never refuses, slows or challenges anyone (S12:
/// 0 genuine users refused behind one IPv4 or one /64).
/// </summary>
internal sealed class SharedAddressSignal(SparkRiskMemory memory, IOptionsMonitor<SparkChallengesOptions> options) : ISparkRiskSignal
{
    public string Name => "shared-address";

    public SparkRiskBasis Basis => SparkRiskBasis.Network;

    public ValueTask<SparkRiskEvidence?> EvaluateAsync(SparkRiskContext context, CancellationToken cancellationToken)
    {
        var risk = options.CurrentValue.Risk;
        if (context.IdentityKey.StartsWith("ip:", StringComparison.Ordinal))
            return ValueTask.FromResult<SparkRiskEvidence?>(null);
        var count = memory.CountIdentitiesAt(context.AddressKey, context.IdentityKey, risk.SharedAddressWindow);
        return ValueTask.FromResult(count > risk.SharedAddressIdentities
            ? new SparkRiskEvidence(Name, 0.1, Detail: $"{count} identities on one address")
            : null);
    }
}
