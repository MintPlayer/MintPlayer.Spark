using CodeCoverage.Entities;
using CodeCoverage.Forge;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Logging;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace CodeCoverage.Commands;

/// <summary>
/// The one lifecycle every comment command goes through (#660 G4, <c>docs/code-coverage/command-reactions-PRD.md</c>):
/// <see cref="BeginAsync"/> at the start of the command's own handler (👀), <see cref="StartedAsync"/> when it set a
/// long-running process going (🚀), and <see cref="FinishAsync"/> with the outcome (🎉 done, 👍 nothing changed, 👎
/// refused or failed). Each state replaces the App's previous reaction, and each step is a line in the command's log.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every member takes a null id and does nothing with it.</b> A label, a button or a merge raises the same events as a
/// comment, with no comment to react to, so a handler calls the lifecycle unconditionally.
/// </para>
/// <para>
/// <b>Best effort (R7).</b> The record is written in a session of its own, so it stands whatever the handler's own session
/// does, and a reaction or a write that fails is logged, never thrown: a command never fails because its bookkeeping did.
/// </para>
/// </remarks>
public interface ICommandLifecycle
{
    /// <summary>
    /// Records the command (once: a replay finds its record) and puts 👀 on its comment. Returns the record's id, or null
    /// when there is no comment (<paramref name="commentId"/> null) or the record could not be written.
    /// </summary>
    Task<string?> BeginAsync(Repository repository, int number, long? commentId, string command, string actor, CancellationToken cancellationToken = default);

    /// <summary>
    /// A log scope on the command's log, for everything the handler logs (G7). Open and close it in the same method, as
    /// <c>await using</c> does. Null for a null id.
    /// </summary>
    ILoggerScope? OpenLog(string? invocationId);

    /// <summary>
    /// The command's log when there is a command, else a log of its own under <paramref name="key"/> (G11: a label, a button
    /// or a merge gets a log too), related to <paramref name="relatedPersistentObjectId"/> and expiring after the retention.
    /// </summary>
    ILoggerScope OpenLog(string? invocationId, string key, string? relatedPersistentObjectId);

    /// <summary>🚀: a long-running process started; <paramref name="mergeQueueRun"/> or <paramref name="dependencyRun"/> names it (G3).</summary>
    Task StartedAsync(string? invocationId, string message, string? mergeQueueRun = null, string? dependencyRun = null, CancellationToken cancellationToken = default);

    /// <summary>Ends the command with <paramref name="outcome"/> and the sentence the user can act on.</summary>
    Task FinishAsync(string? invocationId, ECommandOutcome outcome, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends a started command, but only when <paramref name="belongs"/> says the ending process is the one it started (G3):
    /// an old <c>/queue</c> never takes a later run's 🎉. With <paramref name="react"/> false the record ends (and expires)
    /// but the comment keeps its 🚀: a <c>/queue</c> that a <c>/dequeue</c> ended gets no final reaction.
    /// </summary>
    Task FinishStartedAsync(string? invocationId, Func<CommandInvocation, bool> belongs, ECommandOutcome outcome, string message,
        CancellationToken cancellationToken = default, bool react = true);

    /// <summary>Points the record at the engine's own log (G7): the merge queue's and the backport's.</summary>
    Task LinkLogAsync(string? invocationId, string logId, CancellationToken cancellationToken = default);
}

[Register(typeof(ICommandLifecycle), ServiceLifetime.Scoped)]
public partial class CommandLifecycle : ICommandLifecycle
{
    /// <summary>How long a finished command is kept when <c>Coverage:Retention:CommandInvocationDays</c> is not set (G5).</summary>
    public const int DefaultRetentionDays = 90;

    private const int MaxAttempts = 5;

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly IEnumerable<IForgeCommentReactions> reactions;
    [Inject] private readonly ILoggerScopeProvider<CommandLifecycle> logs;
    [Inject] private readonly IConfiguration configuration;
    [Inject] private readonly TimeProvider time;
    [Inject] private readonly ILogger<CommandLifecycle> logger;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<string?> BeginAsync(Repository repository, int number, long? commentId, string command, string actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (commentId is not { } comment || repository.Id is null) return null;
        var id = CommandInvocation.DocumentId(repository.Provider, repository.GitHubId, comment);
        var text = FirstLine(command);
        try
        {
            var record = await UpdateAsync(id, create: () => new CommandInvocation
            {
                Id = id, Repository = repository.Id, Number = number, CommentId = comment, Command = text, Actor = actor,
                ReceivedAtUtc = Now, Outcome = ECommandOutcome.Received, LogId = LogIds.ForKey(CommandInvocation.LogKeyOf(id)),
            }, mutate: (_, isNew) => isNew, cancellationToken);
            if (record is null) return null;
            // The record says who wrote what and when; the log holds what the handler did with it.
            if (record.CurrentReaction is null)
                await ReactAsync(id, EForgeReaction.Eyes, cancellationToken);
            return id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record the command on comment {CommentId} in {Repository}", comment, repository.FullName);
            return null;
        }
    }

    public ILoggerScope? OpenLog(string? invocationId)
        => invocationId is null ? null : logs.CreateScope(CommandInvocation.LogKeyOf(invocationId), relatedPersistentObjectId: invocationId);

    public ILoggerScope OpenLog(string? invocationId, string key, string? relatedPersistentObjectId)
        => OpenLog(invocationId) ?? logs.CreateScope(key, relatedPersistentObjectId, Now.AddDays(RetentionDays));

    public Task StartedAsync(string? invocationId, string message, string? mergeQueueRun = null, string? dependencyRun = null, CancellationToken cancellationToken = default)
        => StepAsync(invocationId, ECommandOutcome.Started, message, record =>
        {
            record.MergeQueueRun = mergeQueueRun ?? record.MergeQueueRun;
            record.DependencyRun = dependencyRun ?? record.DependencyRun;
            return true;
        }, cancellationToken);

    public Task FinishAsync(string? invocationId, ECommandOutcome outcome, string message, CancellationToken cancellationToken = default)
        => StepAsync(invocationId, outcome, message, _ => true, cancellationToken);

    public Task FinishStartedAsync(string? invocationId, Func<CommandInvocation, bool> belongs, ECommandOutcome outcome, string message,
        CancellationToken cancellationToken = default, bool react = true)
        => StepAsync(invocationId, outcome, message, record => record.Outcome == ECommandOutcome.Started && belongs(record), cancellationToken, react);

    public async Task LinkLogAsync(string? invocationId, string logId, CancellationToken cancellationToken = default)
    {
        if (invocationId is null) return;
        try
        {
            await UpdateAsync(invocationId, create: null, mutate: (record, _) =>
            {
                if (record.LogId == logId) return false;
                record.LogId = logId;
                return true;
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not link command {Invocation} to log {LogId}", invocationId, logId);
        }
    }

    /// <summary>The reaction each state shows (G2).</summary>
    internal static EForgeReaction ReactionFor(ECommandOutcome outcome) => outcome switch
    {
        ECommandOutcome.Received => EForgeReaction.Eyes,
        ECommandOutcome.Started => EForgeReaction.Rocket,
        ECommandOutcome.Done => EForgeReaction.Hooray,
        ECommandOutcome.NoChange => EForgeReaction.ThumbsUp,
        _ => EForgeReaction.ThumbsDown,
    };

    private async Task StepAsync(string? invocationId, ECommandOutcome outcome, string message, Func<CommandInvocation, bool> applies, CancellationToken cancellationToken,
        bool react = true)
    {
        if (invocationId is null) return;
        try
        {
            var finished = outcome is not (ECommandOutcome.Received or ECommandOutcome.Started);
            var expires = finished ? Now.AddDays(RetentionDays) : (DateTime?)null;
            var changed = false;
            var record = await UpdateAsync(invocationId, create: null, mutate: (record, _) =>
            {
                if (!applies(record)) return false;
                record.Outcome = outcome;
                record.Message = message;
                record.FinishedAtUtc = finished ? Now : null;
                record.ExpiresAtUtc = expires;
                changed = true;
                return true;
            }, cancellationToken);
            if (!changed || record is null) return;

            // To the log the record points at (G7): its own, or the engine's it was linked to, which keeps its own relation.
            var own = record.LogId is null || record.LogId == LogIds.ForKey(CommandInvocation.LogKeyOf(invocationId));
            var key = own ? CommandInvocation.LogKeyOf(invocationId) : record.LogId![LogIds.Prefix.Length..];
            await logs.LogAsync(key, outcome is ECommandOutcome.Failed ? LogLevel.Warning : LogLevel.Information,
                $"{record.Command} by @{record.Actor}: {Describe(outcome)}: {message}", relatedPersistentObjectId: own ? invocationId : null,
                expiresAtUtc: own ? expires : Now.AddDays(RetentionDays), cancellationToken: cancellationToken);
            if (react)
                await ReactAsync(invocationId, ReactionFor(outcome), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not record the {Outcome} of command {Invocation}", outcome, invocationId);
        }
    }

    /// <summary>
    /// Puts <paramref name="reaction"/> on the comment and removes the App's previous one (G4): the new one first, so the
    /// comment is never without the bot's state. Best effort: a forge that refuses leaves the record's reaction as it was.
    /// </summary>
    private async Task ReactAsync(string invocationId, EForgeReaction reaction, CancellationToken cancellationToken)
    {
        try
        {
            using var session = store.OpenAsyncSession();
            if (await session.LoadAsync<CommandInvocation>(invocationId, cancellationToken) is not { } record) return;
            if (record.CurrentReaction == reaction && record.CurrentReactionId is not null) return;
            if (await session.LoadAsync<Repository>(record.Repository, cancellationToken) is not { } repository) return;
            if (reactions.FirstOrDefault(r => r.Provider == repository.Provider) is not { } forge) return;

            var added = await forge.AddAsync(repository, record.CommentId, reaction, cancellationToken);
            if (record.CurrentReactionId is { } previous && previous != added)
                await forge.RemoveAsync(repository, record.CommentId, previous, cancellationToken);

            await UpdateAsync(invocationId, create: null, mutate: (current, _) =>
            {
                current.CurrentReaction = reaction;
                current.CurrentReactionId = added;
                return true;
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not react {Reaction} to command {Invocation}", reaction, invocationId);
        }
    }

    /// <summary>Loads (or creates) the record in a session of its own and saves what <paramref name="mutate"/> changed, retrying a conflict.</summary>
    private async Task<CommandInvocation?> UpdateAsync(string id, Func<CommandInvocation>? create, Func<CommandInvocation, bool, bool> mutate,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var session = store.OpenAsyncSession();
            session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
            var record = await session.LoadAsync<CommandInvocation>(id, cancellationToken);
            var isNew = false;
            if (record is null)
            {
                if (create is null) return null;
                record = create();
                isNew = true;
                // An empty change vector: only when nothing is stored under the id, so two deliveries create one record.
                await session.StoreAsync(record, string.Empty, id, cancellationToken);
            }
            if (!mutate(record, isNew)) return record;
            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return record;
            }
            catch (ConcurrencyException) when (attempt < MaxAttempts)
            {
                // Another step of the same command (a replay, the run's end) wrote it first; reload and reapply.
            }
        }
    }

    private int RetentionDays => Math.Max(1, configuration.GetValue("Coverage:Retention:CommandInvocationDays", DefaultRetentionDays));

    private static string FirstLine(string command)
    {
        var line = command.Split('\n', 2)[0].TrimEnd('\r').Trim();
        return line.Length > CommandInvocation.MaxCommandLength ? line[..CommandInvocation.MaxCommandLength] : line;
    }

    private static string Describe(ECommandOutcome outcome) => outcome switch
    {
        ECommandOutcome.Started => "Started",
        ECommandOutcome.Done => "Done",
        ECommandOutcome.NoChange => "Nothing changed",
        ECommandOutcome.Refused => "Refused",
        ECommandOutcome.Failed => "Failed",
        _ => "Received",
    };
}
