using MintPlayer.Spark.Messaging.Abstractions;

namespace MintPlayer.Spark.Messaging;

/// <summary>
/// Settings that apply to messaging as a whole. Per-lane behaviour — delivery mode, concurrency,
/// retry schedule — is declared through the lane builder instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never give a collection property a non-empty default here.</b> .NET's configuration binder does
/// not replace a collection that already has elements, it <i>appends</i> to it, so a hardcoded
/// initializer survives binding and stays first — an application configuring a faster schedule would
/// still wait the built-in delay, silently, with a config file that said otherwise.
/// </para>
/// <para>
/// Measured, an array fails three ways and only the first is widely known: a non-empty default is
/// appended to; <b>two configuration layers overlay element-wise</b>, so a base <c>[1m,5m,1h]</c>
/// overridden by <c>[7s]</c> becomes <c>[7s,5m,1h]</c> — a ladder nobody wrote, needing no non-empty
/// default at all; and re-binding the same object doubles it. This is why a retry ladder is written
/// as a <b>scalar string</b> (<c>"5s 30s 2m"</c>): a scalar is replaced by the last source and is
/// idempotent under re-binding.
/// </para>
/// </remarks>
public class SparkMessagingOptions
{
    /// <summary>
    /// A retry schedule applied to <b>every</b> lane, overriding whatever each declares.
    /// </summary>
    /// <remarks>
    /// One switch for test and development environments: <c>"5s"</c> flattens every ladder to a flat
    /// five seconds. It deliberately does not collapse attempt counts, so a test still exercises the
    /// real dead-letter path rather than a shortened one.
    /// </remarks>
    public string? RetryOverride { get; set; }

    /// <summary>The schedule for lanes that declare none. Written in the same grammar as configuration.</summary>
    public string DefaultRetry { get; set; } = "5s 30s 2m";

    /// <summary>
    /// The resolved default schedule: the global override if one is set, else <see cref="DefaultRetry"/>.
    /// </summary>
    public IRetrySchedule ResolvedDefaultRetry => RetrySchedule.Ladder(
        string.IsNullOrWhiteSpace(RetryOverride) ? DefaultRetry : RetryOverride);

    /// <summary>
    /// How long a message may sit in <c>Processing</c> before the reaper assumes its host died.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. A handler may legitimately run for minutes, and reaping too eagerly
    /// double-processes work that is still running — worse than reaping late, because the message is
    /// blocking only its own partition meanwhile.
    /// </remarks>
    public TimeSpan ProcessingLease { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often to look for messages stranded past <see cref="ProcessingLease"/>.</summary>
    public TimeSpan ReaperInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a terminal message is kept before RavenDB expires it.</summary>
    public int RetentionDays { get; set; } = 7;
}
