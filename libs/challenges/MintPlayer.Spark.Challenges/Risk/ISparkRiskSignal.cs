using Microsoft.AspNetCore.Http;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Challenges.Risk;

/// <summary>
/// One source of risk evidence (commerce PRD §4.12, research §6.7). Discovered (<c>[SparkDiscoverable]</c>) in an
/// application, or registered by a library (scoped). Built in: replayed or forged tokens recorded against an identity,
/// the velocity rule, the honeypot, the automation flag, the shared-address count.
/// </summary>
/// <remarks>
/// <para>
/// Each evaluation is time-boxed (<see cref="SparkRiskOptions.SignalTimeout"/>, 20 ms): a slow signal is skipped, never
/// awaited. A signal that throws is skipped and logged.
/// </para>
/// <para>
/// <b>Deterministic</b> evidence (<see cref="SparkRiskEvidence.Deterministic"/>) is proof, not suspicion: a replayed
/// single-use token, a per-buyer limit exceeded. Only deterministic evidence ever blocks (D29); everything else adds to a
/// score that at worst asks for a challenge or slows the caller down.
/// </para>
/// <para>
/// <b>Country (GA11, Regulation (EU) 2018/302).</b> A signal declares what it is keyed on (<see cref="Basis"/>). Startup
/// refuses a signal keyed on <see cref="SparkRiskBasis.Nationality"/>, <see cref="SparkRiskBasis.Residence"/> or
/// <see cref="SparkRiskBasis.CardIssuingCountry"/>: Art. 3–5 forbid treating a customer differently for those reasons.
/// Evidence from a network-location country (<see cref="SparkRiskEvidence.CountryBased"/>) is never deterministic and can
/// never by itself move a decision past <see cref="SparkRiskAction.Flag"/>.
/// </para>
/// </remarks>
[SparkDiscoverable]
public interface ISparkRiskSignal
{
    /// <summary>The signal's name, stored on the decision log.</summary>
    string Name { get; }

    /// <summary>What the signal is keyed on; startup refuses the forbidden bases (GA11).</summary>
    SparkRiskBasis Basis => SparkRiskBasis.Behaviour;

    /// <summary>The evidence for one request, or null when the signal has nothing to say.</summary>
    ValueTask<SparkRiskEvidence?> EvaluateAsync(SparkRiskContext context, CancellationToken cancellationToken);
}

/// <summary>What a signal is keyed on.</summary>
[Flags]
public enum SparkRiskBasis
{
    None = 0,
    /// <summary>The identity itself (account, visitor): replays, limits.</summary>
    Identity = 1,
    /// <summary>How the caller behaves: velocity, honeypots, failed challenges.</summary>
    Behaviour = 2,
    /// <summary>The network address or prefix.</summary>
    Network = 4,
    /// <summary>The client device or software (automation flags, TLS fingerprints).</summary>
    Device = 8,
    /// <summary>The account's history (age, verified phone, previous orders).</summary>
    Account = 16,
    /// <summary>A payment instrument's fingerprint across identities.</summary>
    PaymentInstrument = 32,
    /// <summary>The country of the network location (geo-IP). Allowed as weak evidence only (capped at Flag).</summary>
    NetworkCountry = 64,
    /// <summary>The buyer's place of residence: forbidden (Reg. 2018/302 Art. 3–4). Startup refuses it.</summary>
    Residence = 128,
    /// <summary>The buyer's nationality: forbidden (Reg. 2018/302 Art. 3–4). Startup refuses it.</summary>
    Nationality = 256,
    /// <summary>The issuing country of the payment card: forbidden (Reg. 2018/302 Art. 5). Startup refuses it.</summary>
    CardIssuingCountry = 512,
}

/// <summary>The request a decision is about.</summary>
/// <param name="Point">Where: a <see cref="SparkChallengePoints"/> value or the host's own (<c>queue-join</c>, <c>hold</c>).</param>
/// <param name="IdentityKey"><c>user:{id}</c>, <c>visitor:{id}</c> or <c>ip:{address}</c> (X1).</param>
/// <param name="AddressKey">The caller's address key (IPv4 address or IPv6 /64, X10); used for counts, never stored.</param>
/// <param name="Subject">What the request acts on (a room id, a sale), for the decision log.</param>
public sealed record SparkRiskContext(string Point, string IdentityKey, string AddressKey, string? Subject = null)
{
    /// <summary>Request facts a signal may read: <c>honeypot</c> (the hidden field's value), <c>webdriver</c> (<c>navigator.webdriver</c> as the client reported it).</summary>
    public IReadOnlyDictionary<string, string?> Facts { get; init; } = new Dictionary<string, string?>();

    /// <summary>The HTTP request, when there is one.</summary>
    public HttpContext? Http { get; init; }
}

/// <summary>One piece of evidence.</summary>
/// <param name="Signal">The signal's name.</param>
/// <param name="Weight">How much it raises the score (0–1), or lowers it when negative (positive evidence: a passkey sign-in, a verified phone).</param>
/// <param name="Deterministic">Proof rather than suspicion: blocks on its own.</param>
/// <param name="Detail">A short explanation for the reviewer; never personal data.</param>
/// <param name="CountryBased">Derived from a network-location country (GA11): never decisive.</param>
/// <param name="Minimum">The least the decision may be, for rules (the velocity rule asks for <see cref="SparkRiskAction.Slow"/>). Block and ShadowQueue are reserved for deterministic evidence.</param>
/// <param name="DeceptiveTrigger">A deterministic trigger for the deceptive measures (a honeypot or decoy hit, a forged or replayed token), even where the evidence itself does not block.</param>
public sealed record SparkRiskEvidence(
    string Signal,
    double Weight,
    bool Deterministic = false,
    string? Detail = null,
    bool CountryBased = false,
    SparkRiskAction? Minimum = null,
    bool DeceptiveTrigger = false);

/// <summary>What the pipeline decides, from mildest to strictest.</summary>
public enum SparkRiskAction
{
    /// <summary>Let the request through.</summary>
    Allow,
    /// <summary>Let it through and log it for review.</summary>
    Flag,
    /// <summary>Ask for a human-verification challenge first (where a provider is configured).</summary>
    Challenge,
    /// <summary>An honest slow-down: 429 with a jittered <c>Retry-After</c>.</summary>
    Slow,
    /// <summary>Looks queued, is never admitted (deterministic evidence only, <c>ShadowQueue = DeterministicOnly</c>).</summary>
    ShadowQueue,
    /// <summary>Refused with an explanation (deterministic evidence only).</summary>
    Block,
}

/// <summary>The pipeline's decision.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Score">The combined probabilistic score (0–1).</param>
/// <param name="Evidence">Every piece of evidence that was considered.</param>
/// <param name="RetryAfter">For <see cref="SparkRiskAction.Slow"/>: when to come back.</param>
/// <param name="DecisionId">The decision-log entry, when one was written (quote it in an appeal).</param>
public sealed record SparkRiskDecision(
    SparkRiskAction Action,
    double Score,
    IReadOnlyList<SparkRiskEvidence> Evidence,
    TimeSpan? RetryAfter = null,
    string? DecisionId = null)
{
    /// <summary>Whether any evidence is deterministic (and not country-based).</summary>
    public bool IsDeterministic => Evidence.Any(e => e.Deterministic && !e.CountryBased);

    /// <summary>Whether a deterministic deceptive trigger is present.</summary>
    public bool HasDeceptiveTrigger => Evidence.Any(e => (e.DeceptiveTrigger || e.Deterministic) && !e.CountryBased);

    public static SparkRiskDecision Allowed { get; } = new(SparkRiskAction.Allow, 0, []);
}
