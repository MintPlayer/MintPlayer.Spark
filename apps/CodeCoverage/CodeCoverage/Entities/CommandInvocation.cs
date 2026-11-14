using CodeCoverage.Forge;
using MintPlayer.Spark.Abstractions;

namespace CodeCoverage.Entities;

/// <summary>
/// One bot command written in a comment (#660 §4, <c>docs/code-coverage/command-reactions-PRD.md</c>): what was asked, by
/// whom, how it ended, the App's current reaction on the comment and the log of what the bot did.
/// </summary>
/// <remarks>
/// <para>
/// One document per comment, <c>CommandInvocations/{provider}/{repoId}/{commentId}</c>, stored and loaded by id: a replayed
/// delivery or a retried message reaches the same record, so a command gets one record and one reaction (R7). The
/// repository page lists them by streaming the repository's id prefix, so no index is built.
/// </para>
/// <para>
/// Written only by <c>CommandLifecycle</c>, never through Spark. A finished record expires after
/// <c>Coverage:Retention:CommandInvocationDays</c> (G5); one still in progress (a <c>/queue</c> waiting its turn) never does.
/// </para>
/// </remarks>
public class CommandInvocation : IExpiring
{
    public const string Prefix = "CommandInvocations/";

    /// <summary>The longest command text kept: its first line.</summary>
    public const int MaxCommandLength = 200;

    public static string DocumentId(EForgeProvider provider, long repositoryId, long commentId)
        => $"{ForRepository(provider, repositoryId)}{commentId}";

    /// <summary>The id prefix every command of one repository shares.</summary>
    public static string ForRepository(EForgeProvider provider, long repositoryId)
        => $"{Prefix}{provider.ToString().ToLowerInvariant()}/{repositoryId}/";

    /// <summary>The key of the command's log (<c>Logs/commands/{provider}/{repoId}/{commentId}</c>).</summary>
    public static string LogKey(EForgeProvider provider, long repositoryId, long commentId)
        => $"commands/{provider.ToString().ToLowerInvariant()}/{repositoryId}/{commentId}";

    /// <summary>The log key of the command whose record id is <paramref name="invocationId"/>.</summary>
    public static string LogKeyOf(string invocationId)
        => invocationId.StartsWith(Prefix, StringComparison.Ordinal)
            ? "commands/" + invocationId[Prefix.Length..]
            : throw new ArgumentException($"'{invocationId}' is not a command id.", nameof(invocationId));

    public string? Id { get; set; }

    /// <summary>The repository the command was written in.</summary>
    [Reference(typeof(Repository))]
    public string Repository { get; set; } = string.Empty;

    /// <summary>The pull request or issue the comment is on.</summary>
    public int Number { get; set; }

    public long CommentId { get; set; }

    /// <summary>The command as the bot understood it, such as <c>/approve Angular</c>.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>The commenter's forge login (the signed webhook's sender).</summary>
    public string Actor { get; set; } = string.Empty;

    public DateTime ReceivedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>Where the command stands: received, started, done, nothing changed, refused or failed.</summary>
    public ECommandOutcome Outcome { get; set; }

    /// <summary>The sentence the user can act on: what happened, or why nothing did.</summary>
    public string? Message { get; set; }

    /// <summary>The reaction the App has on the comment now, if any (G4).</summary>
    [IgnoreProperty]
    public EForgeReaction? CurrentReaction { get; set; }

    /// <summary>Its id, so the next state removes it.</summary>
    [IgnoreProperty]
    public long? CurrentReactionId { get; set; }

    /// <summary>The merge-queue run a <c>/queue</c> started (G3): only that run's end finishes this command.</summary>
    [IgnoreProperty]
    public string? MergeQueueRun { get; set; }

    /// <summary>The number of the dependency run a <c>/refresh</c> or <c>/bisect</c> started (G3). The comment's pull request names the line.</summary>
    [IgnoreProperty]
    public string? DependencyRun { get; set; }

    /// <summary>The command's log: its own (a <c>/backport</c>'s attempts point at it too), or the merge-queue run's for <c>/queue</c>.</summary>
    [Reference(typeof(MintPlayer.Spark.Logging.Models.Log))]
    public string? LogId { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }
}

/// <summary>Where a command stands (#660 G2, G3).</summary>
public enum ECommandOutcome
{
    /// <summary>👀 The bot is on it.</summary>
    Received,
    /// <summary>🚀 A long-running process started; it finishes the command when it ends.</summary>
    Started,
    /// <summary>🎉 Done.</summary>
    Done,
    /// <summary>👍 Nothing changed.</summary>
    NoChange,
    /// <summary>👎 Refused: the request could not be honoured as asked.</summary>
    Refused,
    /// <summary>👎 Failed: the bot or the forge ran into an error.</summary>
    Failed,
}
