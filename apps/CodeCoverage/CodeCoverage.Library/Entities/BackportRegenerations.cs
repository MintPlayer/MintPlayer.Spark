namespace CodeCoverage.Entities;

/// <summary>
/// The backports of one repository whose pull request waits for the line's regenerate workflow (#570 M6, D9): the backport
/// branch is pushed, its regenerated files kept at the line's side, and the pull request opens once the workflow's patch
/// is folded into the backport commit, or with a note saying why it was not.
/// </summary>
/// <remarks>
/// Loaded by id only (<see cref="ForRepository"/>), so the <c>workflow_run</c> webhook's filter is one point load. A
/// backport leaves <see cref="Pending"/> when its pull request opens; the document then holds nothing.
/// </remarks>
public class BackportRegenerations
{
    public string? Id { get; set; }

    public List<BackportRegeneration> Pending { get; set; } = [];

    /// <summary>The pending backport waiting for workflow run <paramref name="runId"/>, if any.</summary>
    public BackportRegeneration? ForRun(long runId)
        => Pending.FirstOrDefault(p => p.Runs.Any(r => r.RunId == runId && !r.Finished));

    /// <summary><c>Repositories/github/1</c> becomes <c>BackportRegenerations/github/1</c>.</summary>
    public static string ForRepository(string repositoryId)
    {
        const string prefix = "Repositories/";
        if (!repositoryId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{repositoryId}' is not a repository document id.", nameof(repositoryId));
        return "BackportRegenerations/" + repositoryId[prefix.Length..];
    }
}

/// <summary>One backport branch waiting for its line's regenerate workflow.</summary>
public class BackportRegeneration
{
    public const int MaxHistory = 50;

    /// <summary>The backport branch, <c>backport/&lt;pr&gt;-&lt;line&gt;</c>; unique per repository.</summary>
    public string Head { get; set; } = string.Empty;

    /// <summary>The release line.</summary>
    public string Branch { get; set; } = string.Empty;

    /// <summary>The backported pull request, where the outcome is commented.</summary>
    public int Number { get; set; }

    /// <summary>The backport pull request to open.</summary>
    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>The backport branch's head: the backport commit, then each regenerated fold of it.</summary>
    public string Sha { get; set; } = string.Empty;

    /// <summary>The backported pull request's merge commit (segment B), recorded on the pull request once it opens.</summary>
    public string? SourceSha { get; set; }

    /// <summary>The workflows to run, one after the other, each on the commit the one before it left (O36's order).</summary>
    public List<string> Workflows { get; set; } = [];

    /// <summary>The globs a patch may touch (T4): the merge queue's <c>regeneratedPaths</c> and the concrete paths.</summary>
    public List<string> RegeneratedPaths { get; set; } = [];

    /// <summary>The files that need regenerating, for the note.</summary>
    public List<string> Paths { get; set; } = [];

    public int PostRebaseTimeoutMinutes { get; set; } = 20;

    /// <summary>Whether the opened pull request goes to the merge queue once regenerated (#570 M7, D12); decided when the
    /// backport ran, from the default branch's <c>backport.enqueue</c> and <c>mergeQueue.branches</c>.</summary>
    public bool Enqueue { get; set; }

    /// <summary>Stamped on every step message; a step for a state the backport has left is dropped.</summary>
    public int Generation { get; set; }

    public List<BackportRegenerationRun> Runs { get; set; } = [];

    /// <summary>Failures in a row that may pass; past the engine's limit the pull request opens unregenerated.</summary>
    public int TransientFailures { get; set; }

    /// <summary>The paths the folded patches changed.</summary>
    public List<string> Regenerated { get; set; } = [];

    public List<BackportRegenerationEvent> History { get; set; } = [];

    public void Note(DateTime atUtc, string text)
    {
        History.Add(new BackportRegenerationEvent { AtUtc = atUtc, Text = text });
        if (History.Count > MaxHistory) History.RemoveAt(0);
    }
}

/// <summary>One regenerate run on a backport branch; the workflow hook's fields.</summary>
public sealed class BackportRegenerationRun : WorkflowHookRun;

public sealed class BackportRegenerationEvent
{
    public DateTime AtUtc { get; set; }

    public string Text { get; set; } = string.Empty;
}
