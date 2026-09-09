using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// One option of a GitHub Projects V2 board's single-select "Status" field — what a person calls a
/// column when they look at the board.
/// <para>
/// Embedded in <see cref="GitHubProject"/> rather than stored as its own document, and cached rather
/// than fetched: moving a card needs the option id, GraphQL only gives option ids alongside the
/// field definition, and re-reading the field on every webhook delivery would spend a GitHub API
/// call per event to learn something that changes when a human edits the board. The cache is
/// refreshed by the <c>SyncColumns</c> action and by the nightly reconciliation.
/// </para>
/// </summary>
[ValueObject]
public partial class ProjectColumn
{
    /// <summary>
    /// The single-select option id, as GraphQL reports it. This is the value a card-move mutation
    /// takes, so it is the identity that matters — not <see cref="Name"/>, which a person can rename
    /// at any time without the id changing.
    /// </summary>
    [ValueKey]
    public string Id { get; set; } = string.Empty;

    /// <summary>The option's display name, e.g. <c>In Progress</c>. Shown to the user when picking a target column.</summary>
    public string Name { get; set; } = string.Empty;
}
