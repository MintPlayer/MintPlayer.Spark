using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Challenges.Risk;

/// <summary>
/// One logged decision (research §6.10 item 7): what was seen, what was decided, and which deceptive measure (if any) was
/// applied, so a decision can be reviewed and appealed (GDPR Art. 22(3): a human can be asked to look again) and shown
/// on an access request (Art. 15). Keeps no network address: counts by address live in memory only.
/// </summary>
public sealed class SparkRiskDecisionRecord : IExpiring
{
    public string? Id { get; set; }

    public DateTime AtUtc { get; set; }

    /// <summary>The point (<c>queue-join</c>, <c>hold</c>, <c>admission-consume</c>).</summary>
    public string Point { get; set; } = "";

    /// <summary>What the request acted on (a room id).</summary>
    public string? Subject { get; set; }

    /// <summary><c>user:{id}</c>, <c>visitor:{id}</c>; an address-only caller is stored as <c>ip</c> (no address).</summary>
    public string IdentityKey { get; set; } = "";

    /// <summary>A <see cref="SparkRiskAction"/> name.</summary>
    public string Action { get; set; } = "";

    public double Score { get; set; }

    public List<SparkRiskEvidenceRecord> Evidence { get; set; } = [];

    /// <summary>The deceptive measure applied, if any (<see cref="Deceptive.SparkDeceptiveMeasureNames"/>).</summary>
    public string? Deceptive { get; set; }

    /// <summary>Kept until <see cref="SparkRiskOptions.DecisionRetention"/> after the decision.</summary>
    public DateTime? ExpiresAtUtc { get; set; }
}

/// <summary>A logged piece of evidence.</summary>
public sealed class SparkRiskEvidenceRecord
{
    public string Signal { get; set; } = "";
    public double Weight { get; set; }
    public bool Deterministic { get; set; }
    public bool CountryBased { get; set; }
    public string? Detail { get; set; }
}

/// <summary>The decision log: written by the pipeline and the deceptive measures; read for appeals and access requests.</summary>
public interface ISparkRiskDecisionLog
{
    /// <summary>Writes one decision; answers its id.</summary>
    Task<string> WriteAsync(SparkRiskContext context, SparkRiskDecision decision, string? deceptive, CancellationToken cancellationToken = default);

    /// <summary>The identity's decisions, newest first (an appeal, a GDPR access request).</summary>
    Task<IReadOnlyList<SparkRiskDecisionRecord>> ForIdentityAsync(string identityKey, int take = 100, CancellationToken cancellationToken = default);

    /// <summary>Deletes the identity's decisions (GDPR erasure). Idempotent.</summary>
    Task EraseAsync(string identityKey, CancellationToken cancellationToken = default);
}

internal sealed class SparkRiskDecisionLog(IDocumentStore store, IOptionsMonitor<SparkChallengesOptions> options, TimeProvider time) : ISparkRiskDecisionLog
{
    public const string IdPrefix = "SparkRiskDecisions/";

    public async Task<string> WriteAsync(SparkRiskContext context, SparkRiskDecision decision, string? deceptive, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var record = new SparkRiskDecisionRecord
        {
            Id = IdPrefix + Guid.NewGuid().ToString("N"),
            AtUtc = now,
            Point = context.Point,
            Subject = context.Subject,
            IdentityKey = StoredIdentity(context.IdentityKey),
            Action = decision.Action.ToString(),
            Score = decision.Score,
            Evidence = decision.Evidence.Select(e => new SparkRiskEvidenceRecord
            {
                Signal = e.Signal,
                Weight = e.Weight,
                Deterministic = e.Deterministic,
                CountryBased = e.CountryBased,
                Detail = e.Detail,
            }).ToList(),
            Deceptive = deceptive,
            ExpiresAtUtc = now + options.CurrentValue.Risk.DecisionRetention,
        };
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(record, record.Id, cancellationToken);
        await session.SaveChangesAsync(cancellationToken);
        return record.Id!;
    }

    public async Task<IReadOnlyList<SparkRiskDecisionRecord>> ForIdentityAsync(string identityKey, int take = 100, CancellationToken cancellationToken = default)
    {
        var key = StoredIdentity(identityKey);
        using var session = store.OpenAsyncSession();
        return await session.Query<SparkRiskDecisionRecord>()
            .Where(r => r.IdentityKey == key)
            .OrderByDescending(r => r.AtUtc)
            .Take(Math.Clamp(take, 1, 1024))
            .ToListAsync(cancellationToken);
    }

    public async Task EraseAsync(string identityKey, CancellationToken cancellationToken = default)
    {
        var key = StoredIdentity(identityKey);
        while (true)
        {
            using var session = store.OpenAsyncSession();
            var batch = await session.Query<SparkRiskDecisionRecord>()
                .Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(15)))
                .Where(r => r.IdentityKey == key)
                .Take(256)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
                return;
            foreach (var record in batch)
                session.Delete(record);
            await session.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>An address-only identity is stored without its address (data minimisation).</summary>
    internal static string StoredIdentity(string identityKey)
        => identityKey.StartsWith("ip:", StringComparison.Ordinal) ? "ip" : identityKey;
}
