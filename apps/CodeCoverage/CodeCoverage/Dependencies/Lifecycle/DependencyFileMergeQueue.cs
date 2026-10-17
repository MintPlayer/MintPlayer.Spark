using CodeCoverage.Entities;
using CodeCoverage.MergeQueue;
using Raven.Client.Documents.Session;

namespace CodeCoverage.Dependencies.Lifecycle;

/// <summary>
/// R26 / D-O9: what the default branch's <c>coverage.yml</c> says about the merge queue, as the bot last read it
/// (<see cref="DependencyUpdateRun.FileMergeQueue"/>). Shown read-only as <c>Repository.MergeQueueInCoverageYml</c> and on
/// the onboarding page.
/// </summary>
/// <remarks>
/// D-O16 (live test 2026-10-17): it was recorded only when a dependency run started or an onboarding pull request was built,
/// so a repository whose workflows were already merged showed nothing although its file said <c>enabled: true</c>. Every
/// read of the default branch's <c>coverage.yml</c> by the bot records it now: the onboarding check, a run's start and
/// verdict, and the merge queue's own reads (<see cref="Record"/>).
/// </remarks>
public static class DependencyFileMergeQueue
{
    public const string Enabled = "enabled";
    public const string Disabled = "disabled";
    public const string Absent = "absent";

    /// <summary>The translation key of the "not read yet" text, beside <c>dependencyOnboardingUi.mergeQueueInFile.{state}</c>.</summary>
    public const string NotRead = "unknown";

    public static string State(string? yml, string defaultBranch)
        => MergeQueueYml.Parse(yml, defaultBranch).Section is { Enabled: true } ? Enabled
            : DependencyOnboarding.HasTopLevelKey(yml, "mergeQueue") ? Disabled
            : Absent;

    /// <summary>The translation key for a recorded state, "not read yet" when none is.</summary>
    public static string TranslationKey(string? state) => $"dependencyOnboardingUi.mergeQueueInFile.{state ?? NotRead}";

    /// <summary>
    /// Records the state from outside the saga (the merge queue) as a deferred patch, applied by the caller's
    /// <c>SaveChangesAsync</c>.
    /// </summary>
    /// <remarks>
    /// A patch, never a load-and-store: the run document is the saga's, and a whole-document store from another session
    /// would overwrite whatever the saga wrote in between. The script writes only when the value differs, so the
    /// document (and the saga's optimistic concurrency on it) is untouched while the file says the same thing; a
    /// repository without a run document has nothing to record on, and the patch of a missing document is a no-op.
    /// </remarks>
    public static void Record(IAsyncDocumentSession session, string repositoryId, string? yml, string defaultBranch)
        => session.Advanced.Defer(new Raven.Client.Documents.Commands.Batches.PatchCommandData(
            DependencyUpdateRun.ForRepository(repositoryId), null,
            new Raven.Client.Documents.Operations.PatchRequest
            {
                Script = "if (this.FileMergeQueue !== args.state) { this.FileMergeQueue = args.state; }",
                Values = { ["state"] = State(yml, defaultBranch) },
            }));
}
