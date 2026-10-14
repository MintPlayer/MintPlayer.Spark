using System.Globalization;
using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Forge;

namespace CodeCoverage.MergeQueue;

/// <summary>
/// The queue's own check run on the pull request's head (merge-queue PRD M5): position, state, eviction reason, the
/// effective configuration with each value's source (O15), the excluded checks (O12) and, when a call was refused for a
/// permission, the link to accept it (O17).
/// </summary>
public static class MergeQueueCheckRenderer
{
    public static ForgeQueueCheck Render(MergeQueueEntry entry, int? position, string? detailsUrl, string? note = null)
    {
        var (state, title) = entry.State switch
        {
            EMergeQueueEntryState.Queued => (EForgeQueueCheckState.Queued, position is { } p ? $"Queued, position {p}" : "Queued"),
            EMergeQueueEntryState.Preparing => (EForgeQueueCheckState.InProgress, "Rebasing onto the latest base"),
            EMergeQueueEntryState.Regenerating => (EForgeQueueCheckState.InProgress, "Regenerating generated files"),
            EMergeQueueEntryState.Publishing => (EForgeQueueCheckState.InProgress, "Publishing the rebased commit"),
            EMergeQueueEntryState.AwaitingChecks => (EForgeQueueCheckState.InProgress, "Waiting for checks"),
            EMergeQueueEntryState.Merging => (EForgeQueueCheckState.InProgress, "Merging"),
            EMergeQueueEntryState.Merged => (EForgeQueueCheckState.Success, "Merged by the queue"),
            EMergeQueueEntryState.Evicted => (EForgeQueueCheckState.Failure, $"Removed from the queue: {entry.EvictionReason}"),
            EMergeQueueEntryState.Dequeued => (EForgeQueueCheckState.Neutral, "Removed from the queue"),
            EMergeQueueEntryState.Refused => (EForgeQueueCheckState.Failure, "Not queued"),
            _ => (EForgeQueueCheckState.Neutral, entry.State.ToString()),
        };

        var summary = new StringBuilder();
        if (entry.EvictionMessage is { Length: > 0 } message)
        {
            summary.AppendLine(message);
            summary.AppendLine();
        }
        if (entry.EvictionPaths.Count > 0)
        {
            summary.AppendLine("Paths:");
            foreach (var path in entry.EvictionPaths.Take(50))
                summary.AppendLine(CultureInfo.InvariantCulture, $"- `{path}`");
            summary.AppendLine();
        }
        if (entry.PermissionsUrl is { Length: > 0 } url)
        {
            summary.AppendLine(CultureInfo.InvariantCulture, $"The App needs a permission that has not been accepted yet: [accept the new permissions]({url}).");
            summary.AppendLine();
        }
        if (note is { Length: > 0 })
        {
            summary.AppendLine(note);
            summary.AppendLine();
        }

        summary.AppendLine("| | |");
        summary.AppendLine("|---|---|");
        summary.AppendLine(CultureInfo.InvariantCulture, $"| State | {entry.State} |");
        if (position is { } at) summary.AppendLine(CultureInfo.InvariantCulture, $"| Position | {at} |");
        summary.AppendLine(CultureInfo.InvariantCulture, $"| Queued by | @{entry.EnqueuedBy} ({entry.EnqueuedVia.ToString().ToLowerInvariant()}) |");
        summary.AppendLine(CultureInfo.InvariantCulture, $"| Pinned head | `{entry.PinnedHeadSha}` |");
        if (entry.FinalSha is { } final) summary.AppendLine(CultureInfo.InvariantCulture, $"| Final commit | `{final}` |");
        if (entry.DispatchRunUrl is { } run) summary.AppendLine(CultureInfo.InvariantCulture, $"| Regenerate run | {run} |");
        if (entry.MergeCommitSha is { } merged) summary.AppendLine(CultureInfo.InvariantCulture, $"| Merge commit | `{merged}` |");

        if (entry.Warnings.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine("**Warnings**");
            foreach (var warning in entry.Warnings)
                summary.AppendLine(CultureInfo.InvariantCulture, $"- {warning}");
        }

        summary.AppendLine();
        summary.AppendLine("**Checks not waited for** (organization or repository setting; a check a ruleset requires is always waited for): "
            + (entry.Config.CheckExclusions.Count == 0 ? "none" : string.Join(", ", entry.Config.CheckExclusions.Select(c => $"`{c}`"))));

        if (entry.Config.Values.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine("**Effective configuration**");
            summary.AppendLine();
            summary.AppendLine("| Setting | Value | Source |");
            summary.AppendLine("|---|---|---|");
            foreach (var value in entry.Config.Values)
                summary.AppendLine(CultureInfo.InvariantCulture, $"| `{value.Key}` | {Cell(value.Value)} | {Cell(value.Source)} |");
        }

        return new ForgeQueueCheck(state, title, summary.ToString(), detailsUrl);
    }

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ');
}
