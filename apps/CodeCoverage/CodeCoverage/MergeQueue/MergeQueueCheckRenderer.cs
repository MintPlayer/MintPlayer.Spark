using System.Globalization;
using System.Text;
using CodeCoverage.Entities;
using CodeCoverage.Forge;

namespace CodeCoverage.MergeQueue;

/// <summary>
/// The queue's own check run on the pull request's head (merge-queue PRD M5): position, state, eviction reason, the
/// effective configuration with each value's source (O15), the excluded checks (O12) and, when a call was refused for a
/// permission, the link to accept it (O17). On a pull request the queue does not hold, the "Not queued" check with the
/// "Add to merge queue" button (§0 O30, <see cref="RenderOffer"/>).
/// </summary>
public static class MergeQueueCheckRenderer
{
    /// <summary>The title of the check on a pull request the queue does not hold (§0 O30).</summary>
    public const string NotQueuedTitle = "Not queued";

    /// <param name="offer">
    /// The run ended without merging and the pull request may be queued again: the check keeps the "Add to merge queue"
    /// button and says how to queue it (§0 O30). Only for a forge whose checks carry buttons.
    /// </param>
    public static ForgeQueueCheck Render(MergeQueueEntry entry, int? position, string? detailsUrl, string? note = null, bool offer = false)
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
            // The title says why the pull request left; with the button (§0 O30), the summary says how to queue it again.
            // An eviction ends red, so the pull request list shows its author an X to act on (owner, 2026-10-15); a
            // dequeue or a refusal is neutral, like a pull request that was never queued. GitHub shows a check's buttons
            // whatever it concluded.
            EMergeQueueEntryState.Evicted => (EForgeQueueCheckState.Failure, Why("Evicted", entry.EvictionReason)),
            EMergeQueueEntryState.Dequeued => (EForgeQueueCheckState.Neutral, "Dequeued"),
            EMergeQueueEntryState.Refused => (EForgeQueueCheckState.Neutral, Why("Refused", entry.EvictionReason)),
            _ => (EForgeQueueCheckState.Neutral, entry.State.ToString()),
        };
        offer &= entry.State is EMergeQueueEntryState.Evicted or EMergeQueueEntryState.Dequeued or EMergeQueueEntryState.Refused;

        var summary = new StringBuilder();
        if (entry.EvictionMessage is { Length: > 0 } message)
        {
            summary.AppendLine(message);
            summary.AppendLine();
        }
        if (offer)
        {
            summary.AppendLine(CultureInfo.InvariantCulture, $"Queue it again with {HowToQueue(entry.Config.Label)}.");
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

        return new ForgeQueueCheck(state, title, summary.ToString(), detailsUrl)
        {
            PullRequestNumber = entry.Number,
            Actions = offer ? [ForgeQueueCheckAction.Enqueue] : [],
        };
    }

    /// <summary>
    /// The check on an open, ready pull request into a queue branch that the queue does not hold (§0 O30): completed,
    /// neutral, "Not queued", with the "Add to merge queue" button. The summary says how to queue it and links the
    /// repository's queue panel and the setup guide, when there are https links to give.
    /// </summary>
    public static ForgeQueueCheck RenderOffer(int number, string label, string? panelUrl, string? setupUrl)
    {
        var summary = new StringBuilder();
        summary.AppendLine(CultureInfo.InvariantCulture,
            $"This pull request is not in the merge queue. Queue it with {HowToQueue(label)}. Queueing takes the `write` role on the repository.");
        var links = new[] { panelUrl is null ? null : $"[The repository's merge queue]({panelUrl})", setupUrl is null ? null : $"[Setting up the merge queue]({setupUrl})" }
            .OfType<string>()
            .ToList();
        if (links.Count > 0)
        {
            summary.AppendLine();
            summary.AppendLine(string.Join(" · ", links));
        }

        return new ForgeQueueCheck(EForgeQueueCheckState.Neutral, NotQueuedTitle, summary.ToString(), panelUrl)
        {
            PullRequestNumber = number,
            Actions = [ForgeQueueCheckAction.Enqueue],
        };
    }

    /// <summary>The ways to queue a pull request from the forge itself; the panel is the fourth.</summary>
    private static string HowToQueue(string label)
        => $"the **{ForgeQueueCheckAction.Enqueue.Label}** button on this check, the `{label}` label or a `/queue` comment";

    private static string Why(string what, string? reason) => reason is { Length: > 0 } ? $"{what}: {reason}" : what;

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ');
}
