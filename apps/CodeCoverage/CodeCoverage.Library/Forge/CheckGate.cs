namespace CodeCoverage.Forge;

/// <summary>
/// O12's gate, as a pure function: may the queue move on from a commit, given everything that reported on it?
/// </summary>
/// <remarks>
/// <para>
/// <b>Every check counts unless excluded</b>, because a repository with no rules (MintPlayer.Spark's
/// <c>master</c>, O12) would otherwise have no gate at all. Required checks can never be excluded. The queue's
/// own check (<see cref="QueueCheckName"/>) is always ignored, and so is the status of the check suite it is in:
/// it is pending for as long as the queue waits, so counting it would wait forever.
/// </para>
/// <para>
/// <b>"Nothing failed yet" is not "passed".</b> The commit passes only once every counted check has
/// concluded, every name in the required and expected-minimum sets has reported, and every check suite with
/// runs in it has completed. A failure anywhere fails at once, without waiting for the rest.
/// </para>
/// <para>
/// ⚠️ <b>A suite with no runs is not waited for.</b> GitHub creates a suite for every App with checks
/// permission on every push, and one that never runs anything stays <c>queued</c> forever. The
/// expected-minimum set is what catches a workflow that has not started yet. A suite whose every run is
/// excluded is not waited for either, so an excluded slow scanner cannot hold the queue through its suite.
/// </para>
/// <para>
/// Names compare ordinally. A required check's App pinning (GitHub's <c>integration_id</c>) is not checked
/// here: the forge enforces it on the merge itself.
/// </para>
/// </remarks>
public static class CheckGate
{
    /// <summary>The name of the queue's own check run.</summary>
    public const string QueueCheckName = "Merge queue";

    /// <summary>Evaluates <paramref name="state"/>.</summary>
    /// <param name="exclusions">Names the owner excluded (O12). Ignored for names in <paramref name="required"/>.</param>
    /// <param name="required">Names the branch rules require (<see cref="ForgeBranchRules.RequiredChecks"/>).</param>
    /// <param name="expectedMinimum">Names that must report before the commit can pass: the checks that reported on the
    /// head pinned at enqueue (merge-queue PRD D45, not the base branch's latest commit, whose push-only workflows never run
    /// on a pull request's commit).</param>
    public static CheckGateResult Evaluate(
        ForgeCheckState state,
        IReadOnlyCollection<string> exclusions,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> expectedMinimum)
    {
        ArgumentNullException.ThrowIfNull(state);

        var requiredSet = new HashSet<string>(required, StringComparer.Ordinal);
        bool IsExcluded(string name)
            => name == QueueCheckName || (!requiredSet.Contains(name) && exclusions.Contains(name, StringComparer.Ordinal));

        var failed = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new SortedSet<string>(StringComparer.Ordinal);
        var passed = new SortedSet<string>(StringComparer.Ordinal);
        var excluded = new SortedSet<string>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);

        void Count(string name, ECheckVerdict verdict)
        {
            reported.Add(name);
            if (IsExcluded(name))
            {
                if (name != QueueCheckName) excluded.Add(name);
                return;
            }
            (verdict switch
            {
                ECheckVerdict.Failed => failed,
                ECheckVerdict.Pending => pending,
                _ => passed,
            }).Add(name);
        }

        foreach (var run in state.CheckRuns)
            Count(run.Name, ReadRun(run.Status, run.Conclusion));
        foreach (var status in state.Statuses)
            Count(status.Context, ReadStatus(status.State));

        // A name that passed once and is pending or failed elsewhere (a run and a status of one name, two
        // Apps' runs of one name) is not passed.
        passed.ExceptWith(failed);
        passed.ExceptWith(pending);
        pending.ExceptWith(failed);

        var missing = new SortedSet<string>(
            requiredSet.Concat(expectedMinimum).Where(name => !reported.Contains(name) && !IsExcluded(name)),
            StringComparer.Ordinal);

        var countedSuites = state.CheckRuns
            .Where(run => run.SuiteId is not null && !IsExcluded(run.Name))
            .Select(run => run.SuiteId!.Value)
            .ToHashSet();
        // GitHub keeps one suite per App and commit, so the queue's own check shares its suite with the App's other runs
        // (the coverage checks), and that suite is in progress for as long as the queue's check is: waiting for it would
        // wait for the queue itself (merge-queue PRD D88). Its other runs are counted one by one above.
        var ownSuites = state.CheckRuns
            .Where(run => run.SuiteId is not null && run.Name == QueueCheckName)
            .Select(run => run.SuiteId!.Value)
            .ToHashSet();
        var pendingSuites = state.Suites
            .Where(suite => suite.LatestCheckRunsCount > 0 && suite.Status != "completed" && !ownSuites.Contains(suite.Id))
            // A suite whose runs are all excluded is not waited for. One whose runs we have not seen (the
            // listing raced the suite) is.
            .Where(suite => countedSuites.Contains(suite.Id) || !state.CheckRuns.Any(run => run.SuiteId == suite.Id))
            .Select(suite => suite.AppSlug ?? suite.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToSortedSet();

        var verdict = failed.Count > 0
            ? ECheckVerdict.Failed
            : pending.Count > 0 || missing.Count > 0 || pendingSuites.Count > 0
                ? ECheckVerdict.Pending
                : ECheckVerdict.Passed;

        return new CheckGateResult(verdict, [.. failed], [.. pending], [.. missing], [.. pendingSuites], [.. passed], [.. excluded]);
    }

    private static SortedSet<string> ToSortedSet(this IEnumerable<string> items) => new(items, StringComparer.Ordinal);

    /// <summary>
    /// A check run's verdict. <c>neutral</c> and <c>skipped</c> pass, as GitHub's own required-check rule
    /// treats them; <c>stale</c> (GitHub gave up on it) and <c>action_required</c> fail, since neither will
    /// turn green by waiting.
    /// </summary>
    public static ECheckVerdict ReadRun(string status, string? conclusion)
        => status != "completed"
            ? ECheckVerdict.Pending
            : conclusion switch
            {
                "success" or "neutral" or "skipped" => ECheckVerdict.Passed,
                _ => ECheckVerdict.Failed,
            };

    /// <summary>A commit status's verdict: <c>error</c> fails like <c>failure</c>; an unknown word waits.</summary>
    public static ECheckVerdict ReadStatus(string state) => state switch
    {
        "success" => ECheckVerdict.Passed,
        "failure" or "error" => ECheckVerdict.Failed,
        _ => ECheckVerdict.Pending,
    };
}

public enum ECheckVerdict
{
    Pending,
    Passed,
    Failed,
}

/// <summary>The gate's answer, with every name it is based on, for the queue check run (O12 lists exclusions on every PR).</summary>
/// <param name="Missing">Required or expected names that have not reported.</param>
/// <param name="PendingSuites">Check suites (by App slug) still running.</param>
public sealed record CheckGateResult(
    ECheckVerdict Verdict,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> PendingSuites,
    IReadOnlyList<string> Passed,
    IReadOnlyList<string> Excluded);
