using CodeCoverage.Feedback;
using MintPlayer.Spark.Messaging.Abstractions;

namespace CodeCoverage.PrSize;

/// <summary>Label one pull request at one head commit (docs/coverage_pr_size_labels_PRD.md D6, D7).</summary>
/// <remarks>The totals are the webhook's (S2): an upper bound on the size, and the fallback above the forge's file cap.</remarks>
[MessageQueue(CoverageQueues.PrSizeLabels)]
public sealed record ApplyPrSizeLabelMessage
{
    public required string RepositoryId { get; init; }
    public required int Number { get; init; }
    public required string HeadSha { get; init; }
    public int? Additions { get; init; }
    public int? Deletions { get; init; }
    public int? ChangedFiles { get; init; }
}

/// <summary>Bring one repository's label definitions in line with its account's scale (D3, D4.2).</summary>
[MessageQueue(CoverageQueues.PrSizeLabels)]
public sealed record ReconcilePrSizeLabelsMessage
{
    public required string RepositoryId { get; init; }
}

/// <summary>Delete one repository's managed labels and forget them: the "Remove labels" answer on disable (D4.3).</summary>
[MessageQueue(CoverageQueues.PrSizeLabels)]
public sealed record RemovePrSizeLabelsMessage
{
    public required string RepositoryId { get; init; }
}
