using MintPlayer.Spark.Abstractions;
namespace CodeCoverage.Entities;

/// <summary>
/// The FormatRepository and LintRepository runs of one repository that are still going (#657 D14, M4): the
/// <c>formatting.yml</c> workflow was dispatched on the default branch, and its patch becomes a pull request once the run
/// finishes. At most one run per mode; a run leaves <see cref="Pending"/> when it is finished, whatever the outcome.
/// </summary>
/// <remarks>
/// Loaded by id only (<see cref="ForRepository"/>), so the <c>workflow_run</c> webhook's filter is one point load. The
/// run's deadline (<see cref="FormatRun.TimeoutMinutes"/>) is its expiry: the timeout step finishes it.
/// </remarks>
[IdKind(SparkIdKind.Natural)]
public partial class FormatRuns
{
    public Id<FormatRuns> Id { get; set; }

    public List<FormatRun> Pending { get; set; } = [];

    /// <summary>The pending run waiting for workflow run <paramref name="runId"/>, if any.</summary>
    public FormatRun? ForRun(long runId) => Pending.FirstOrDefault(p => p.Run is { RunId: var id, Finished: false } && id == runId);

    /// <summary><c>Repositories/github/1</c> becomes <c>FormatRuns/github/1</c>.</summary>
    public static string ForRepository(string repositoryId)
    {
        const string prefix = "Repositories/";
        if (!repositoryId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{repositoryId}' is not a repository document id.", nameof(repositoryId));
        return "FormatRuns/" + repositoryId[prefix.Length..];
    }
}

/// <summary>One FormatRepository or LintRepository run.</summary>
public class FormatRun
{
    public const int MaxHistory = 30;

    /// <summary><c>format</c> or <c>lint</c>; unique per repository.</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>The user who started it (<c>SparkUsers/…</c>), who is notified of the outcome.</summary>
    public string? RequestedBy { get; set; }

    /// <summary>The user's forge login, the merge queue's actor when <see cref="Enqueue"/> is set (D5).</summary>
    public string? RequestedByLogin { get; set; }

    /// <summary>D5: hand the pull request to the merge queue once it opens.</summary>
    public bool Enqueue { get; set; }

    /// <summary>The default branch the run formats and the pull request targets.</summary>
    public string Branch { get; set; } = string.Empty;

    /// <summary>The default branch's head the run was dispatched on, and the pull request's base.</summary>
    public string Sha { get; set; } = string.Empty;

    /// <summary>The repository's <c>exclude</c> globs (D11), as its default branch's <c>formatting.yml</c> sets them.</summary>
    public List<string> Exclude { get; set; } = [];

    public int TimeoutMinutes { get; set; } = 30;

    public DateTime StartedUtc { get; set; }

    /// <summary>Stamped on every step message; a step for a state the run has left is dropped.</summary>
    public int Generation { get; set; }

    public FormatRunWorkflow? Run { get; set; }

    /// <summary>Failures in a row that may pass; past the engine's limit the run ends.</summary>
    public int TransientFailures { get; set; }

    public List<FormatRunEvent> History { get; set; } = [];

    public void Note(DateTime atUtc, string text)
    {
        History.Add(new FormatRunEvent { AtUtc = atUtc, Text = text });
        if (History.Count > MaxHistory) History.RemoveAt(0);
    }
}

/// <summary>The dispatched <c>formatting.yml</c> run; the workflow hook's fields.</summary>
public sealed class FormatRunWorkflow : WorkflowHookRun;

public sealed class FormatRunEvent
{
    public DateTime AtUtc { get; set; }

    public string Text { get; set; } = string.Empty;
}
