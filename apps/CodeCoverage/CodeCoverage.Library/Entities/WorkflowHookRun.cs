namespace CodeCoverage.Entities;

/// <summary>
/// One dispatched workflow run, as a saga keeps it in its own document: what the workflow hook needs to wait for the run,
/// judge it and cancel it (dependency-updates PRD §13.8 N1). The merge queue's <see cref="MergeQueueDispatch"/> is one;
/// the dependency updater's lookup, apply and migrate runs will be others.
/// </summary>
public class WorkflowHookRun
{
    /// <summary>The workflow file under <c>.github/workflows</c>.</summary>
    public string Workflow { get; set; } = string.Empty;

    public long? RunId { get; set; }

    public string? RunUrl { get; set; }

    /// <summary>
    /// The run reported its completion, vanished, or its cancellation was queued. While false, a saga that stops waiting
    /// for it for any other reason cancels it, as it would otherwise keep spending the repository's CI minutes
    /// (merge-queue PRD §0 D53).
    /// </summary>
    public bool Finished { get; set; }

    /// <summary>The run finished with success on the expected commit; its artifacts may be read.</summary>
    public bool Succeeded { get; set; }
}
