using MintPlayer.Spark.Abstractions.Builder;

namespace MintPlayer.Spark.Challenges;

/// <summary>
/// <c>Spark:Challenges</c> (commerce PRD §4.12, D29). Read through <c>IOptionsMonitor</c>, so <see cref="Provider"/>,
/// <see cref="Points"/> and the thresholds change at runtime when the configuration source reloads (an operator turns a
/// challenge on mid-attack without a redeploy). Code given to <c>AddChallenges(o =&gt; …)</c> runs after the section.
/// </summary>
public sealed class SparkChallengesOptions : ISparkConfigurationSection
{
    public static string SectionName => "Spark:Challenges";

    /// <summary>
    /// The provider offered at every point, by name. Null: the last one registered (<c>UseTurnstile()</c> after
    /// <c>UseAltcha()</c> makes Turnstile the provider, with ALTCHA as its consent fallback). Switchable at runtime among
    /// the registered providers.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// The points where a challenge is asked (<see cref="SparkChallengePoints"/>): <c>["queue-join"]</c>. Empty by default:
    /// no point is challenged until the host or an operator says so. <c>hold</c> means a step-up only, when risk reaches
    /// <see cref="StepUpScore"/>; <c>admission</c> is refused.
    /// </summary>
    public List<string> Points { get; set; } = [];

    /// <summary>The risk score from which a step-up is asked at a point in <see cref="Points"/>. Default 0.7 (research §6.7).</summary>
    public double StepUpScore { get; set; } = 0.7;

    /// <summary>How long a used response is remembered, so a replay is refused. Default 10 minutes (longer than any provider's token lifetime: Turnstile 300 s).</summary>
    public TimeSpan ReplayWindow { get; set; } = TimeSpan.FromMinutes(10);

    public SparkAltchaOptions Altcha { get; set; } = new();

    public SparkRiskOptions Risk { get; set; } = new();

    public SparkDeceptiveOptions Deceptive { get; set; } = new();
}

/// <summary><c>Spark:Challenges:Altcha</c>: the built-in proof of work (altcha-lib v1 protocol, SHA-256).</summary>
public sealed class SparkAltchaOptions
{
    /// <summary>
    /// The search space (altcha's <c>maxnumber</c>); the client tries on average half of it. Default 100,000: ALTCHA's own
    /// benchmark at that complexity is ≈ 0.83 s on an iPhone 12 mini and ≈ 2.5 s on a Galaxy A14 (research §6.4). The
    /// low-end Android criterion of S12 was dropped in round 4, so this default is unmeasured here.
    /// </summary>
    public long MaxNumber { get; set; } = 100_000;

    /// <summary>How long an offered challenge stays solvable. Default 10 minutes.</summary>
    public TimeSpan Expires { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary><c>Spark:Challenges:Risk</c>: the risk pipeline's thresholds and built-in signals (research §6.7). Starting values, to be calibrated (S12).</summary>
public sealed class SparkRiskOptions
{
    /// <summary>From this score a decision is flagged (allowed, logged). Default 0.3.</summary>
    public double FlagScore { get; set; } = 0.3;

    /// <summary>From this score a challenge is asked where one is configured; otherwise the caller is slowed down. Default 0.7.</summary>
    public double ChallengeScore { get; set; } = 0.7;

    /// <summary>From this score the caller is slowed down as well as challenged. Default 0.9.</summary>
    public double SlowScore { get; set; } = 0.9;

    /// <summary>How long one signal may take before it is ignored (time-boxed). Default 20 ms.</summary>
    public TimeSpan SignalTimeout { get; set; } = TimeSpan.FromMilliseconds(20);

    /// <summary>The <c>Retry-After</c> of an honest slow-down (429); jittered by up to half. Default 30 s.</summary>
    public TimeSpan SlowRetryAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Attempts per identity per point per minute beyond human plausibility (the velocity rule): over it, the caller is slowed down. Default 30.</summary>
    public int VelocityPerMinute { get; set; } = 30;

    /// <summary>
    /// Distinct identities from one network address (an IPv4 address or an IPv6 /64) within <see cref="SharedAddressWindow"/>
    /// above which the shared-address signal speaks. It weighs at most 0.1: a shared address alone (a carrier's NAT, an
    /// office, a venue's Wi-Fi) never refuses anyone. Default 50.
    /// </summary>
    public int SharedAddressIdentities { get; set; } = 50;

    /// <summary>The window of the shared-address count. Default 5 minutes.</summary>
    public TimeSpan SharedAddressWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long deterministic evidence recorded against an identity (a replayed admission token) keeps counting. Default 24 hours.</summary>
    public TimeSpan RecordedEvidenceLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Whether a plain Allow without any evidence is logged too. Default false (only decisions with evidence are).</summary>
    public bool LogAllows { get; set; }

    /// <summary>How long a decision is kept for review, appeals and GDPR access requests. Default 90 days (research §6.10 item 7).</summary>
    public TimeSpan DecisionRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>
    /// <c>Off</c> (default) or <c>DeterministicOnly</c>: with the latter, a caller with deterministic evidence is put in a
    /// shadow queue (it looks queued, it is never admitted) instead of being refused (research §6.7). A deceptive measure:
    /// logged for appeal, and startup warns.
    /// </summary>
    public SparkShadowQueueMode ShadowQueue { get; set; } = SparkShadowQueueMode.Off;
}

/// <summary>Whether deterministic cases go to a shadow queue.</summary>
public enum SparkShadowQueueMode
{
    Off,
    DeterministicOnly,
}

/// <summary>
/// <c>Spark:Challenges:Deceptive</c> (D29, owner's choice; research §6.8): each measure is a separate switch, off by
/// default, and needs an explicit <see cref="SparkDeceptiveMeasureOptions.Trigger"/>. By default a measure fires only on
/// deterministic evidence (a forged or replayed token, a honeypot or decoy hit). Every application is logged for appeal,
/// and startup warns while any is on (legal exposure: Directive 2005/29/EC (UCPD) Art. 5–7 on misleading practices toward
/// a consumer who is in fact human; GDPR Art. 22 on decisions based solely on automated processing).
/// </summary>
public sealed class SparkDeceptiveOptions
{
    /// <summary>A hidden form field; a request that fills it looks accepted but goes to the shadow queue.</summary>
    public SparkDeceptiveMeasureOptions Honeypot { get; set; } = new();

    /// <summary>Decoy routes no page links to visibly (<c>…/priority-access</c>); a hit is deterministic evidence and answers a plausible fake.</summary>
    public SparkDeceptiveMeasureOptions Decoys { get; set; } = new();

    /// <summary>A flagged caller is shown "sold out" instead of the real state.</summary>
    public SparkDeceptiveMeasureOptions FakeSoldOut { get; set; } = new();

    /// <summary>A flagged caller's admission token validates but is refused when it is consumed (at the hold).</summary>
    public SparkDeceptiveMeasureOptions PoisonedTokens { get; set; } = new();

    /// <summary>A flagged caller's responses are delayed by <see cref="SparkDeceptiveMeasureOptions.Delay"/>.</summary>
    public SparkDeceptiveMeasureOptions Tarpit { get; set; } = new() { Delay = TimeSpan.FromSeconds(5) };
}

/// <summary>One deceptive measure's switch.</summary>
public sealed class SparkDeceptiveMeasureOptions
{
    /// <summary>Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// What fires the measure. Must be set explicitly when <see cref="Enabled"/> (startup refuses otherwise):
    /// <see cref="SparkDeceptiveTrigger.Deterministic"/> (recommended) or <see cref="SparkDeceptiveTrigger.RiskScore"/>
    /// with <see cref="TriggerScore"/>, an explicit host choice.
    /// </summary>
    public SparkDeceptiveTrigger Trigger { get; set; } = SparkDeceptiveTrigger.Unset;

    /// <summary>The score from which a <see cref="SparkDeceptiveTrigger.RiskScore"/> trigger fires (0–1). Required with that trigger.</summary>
    public double? TriggerScore { get; set; }

    /// <summary>The tarpit's delay (capped at 30 s). Ignored by the other measures.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
}

/// <summary>What fires a deceptive measure.</summary>
public enum SparkDeceptiveTrigger
{
    /// <summary>Not set: refused at startup when the measure is enabled.</summary>
    Unset,

    /// <summary>Only deterministic evidence: a forged or replayed token, a honeypot or decoy hit.</summary>
    Deterministic,

    /// <summary>A risk score at or above <see cref="SparkDeceptiveMeasureOptions.TriggerScore"/>; probabilistic, so a human can be hit.</summary>
    RiskScore,
}
