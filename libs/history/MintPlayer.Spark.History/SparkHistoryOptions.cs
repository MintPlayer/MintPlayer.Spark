namespace MintPlayer.Spark.History;

/// <summary>Options for <c>AddHistory()</c>, bound from <c>Spark:History</c>.</summary>
/// <remarks>
/// <b>Revision limits: configuration beats code</b> (#460 M16, D14). <see cref="Revisions"/> and
/// <see cref="Types"/> are bound from <c>Spark:History:Revisions</c> and <c>Spark:History:Types:{type}</c>
/// once more after every <c>Configure</c> — the <c>AddHistory</c> delegate included — so an operator can
/// retune retention through appsettings, environment variables
/// (<c>Spark__History__Types__Question__MinimumRevisionsToKeep=2</c>) or user secrets without a redeploy.
/// </remarks>
public sealed class SparkHistoryOptions
{
    /// <summary>
    /// Whether startup merges each model type's revisions configuration into the database's (T10).
    /// Default <see langword="true"/>. Turn it off when an operator manages the configuration by hand,
    /// or when the app's RavenDB credentials may not change it (<c>ConfigureRevisionsOperation</c> is a
    /// database-admin operation), or when the licence refuses the limits and they cannot be lowered —
    /// startup otherwise refuses with the reason.
    /// </summary>
    public bool ConfigureRevisions { get; set; } = true;

    /// <summary>
    /// The limits for every type whose revisions are enabled, where neither <see cref="Types"/> nor the
    /// model's <c>revisions</c> block sets them (<c>Spark:History:Revisions</c>). By default revisions
    /// are kept for <see cref="DefaultMinimumRevisionAgeToKeep"/> (30 days) with no count limit and are
    /// not purged on delete — within what RavenDB's Community licence allows (at most 2 revisions and 45
    /// days, measured in spike H1), and bounded, because revisions hold personal data that account
    /// deletion does not rewrite (D8).
    /// </summary>
    public SparkRevisionLimits Revisions { get; set; } = new() { MinimumRevisionAgeToKeep = DefaultMinimumRevisionAgeToKeep };

    /// <summary>
    /// Per model type (keyed by the type's <c>name</c>, case-insensitive): <c>Spark:History:Types:{type}</c>.
    /// Beats the model's <c>revisions</c> block property by property; <see cref="SparkRevisionTypeOptions.Enabled"/>
    /// can switch revisions on for a type without a block, or off for one with.
    /// </summary>
    public Dictionary<string, SparkRevisionTypeOptions> Types { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The default age limit: 30 days.</summary>
    public static readonly TimeSpan DefaultMinimumRevisionAgeToKeep = TimeSpan.FromDays(30);
}

/// <summary>
/// RavenDB's revision limits for one collection. Every property is optional: unset falls back to the
/// next source (per-type options → the model's <c>revisions</c> block → <see cref="SparkHistoryOptions.Revisions"/>).
/// RavenDB deletes a revision only when it is beyond <see cref="MinimumRevisionsToKeep"/> <b>and</b> older than
/// <see cref="MinimumRevisionAgeToKeep"/>, and only when the document is written again.
/// </summary>
public class SparkRevisionLimits
{
    /// <summary>At least this many revisions are kept per document. <c>0</c> = no count limit.</summary>
    public long? MinimumRevisionsToKeep { get; set; }

    /// <summary>Revisions younger than this are kept (<c>"30.00:00:00"</c>). <c>00:00:00</c> = no age limit.</summary>
    public TimeSpan? MinimumRevisionAgeToKeep { get; set; }

    /// <summary>Whether a document's revisions go when the document is hard-deleted.</summary>
    public bool? PurgeOnDelete { get; set; }
}

/// <summary>One model type's revision settings (<c>Spark:History:Types:{type}</c>).</summary>
public sealed class SparkRevisionTypeOptions : SparkRevisionLimits
{
    /// <summary>Switches revisions on or off for the type, over the model's <c>revisions.enabled</c>. Null = the model decides.</summary>
    public bool? Enabled { get; set; }
}
