namespace MintPlayer.Spark.Contributions;

/// <summary>
/// The attribution a <see cref="ContributionAttribute"/> declaration asks for (PRD Q6). The generator
/// emits only what is requested: each flag adds a read-only, shielded row attribute marked with the
/// <c>contributionAttribution</c> rendering hint.
/// </summary>
[Flags]
public enum ContributionAttribution
{
    /// <summary>Nothing extra is stored, loaded or shown.</summary>
    None = 0,

    /// <summary>
    /// <c>ContributorName</c>: the name of whoever wrote the current version, looked up at read time in
    /// the same lazy request, never copied into the document.
    /// </summary>
    Contributor = 1,

    /// <summary><c>UpdatedAt</c>: when the current version was written.</summary>
    UpdatedAt = 2,

    /// <summary>
    /// <c>ContributionCount</c>: how many contributions the slot has, kept on the current document,
    /// with a link to the slot's contributions query.
    /// </summary>
    History = 4,
}
