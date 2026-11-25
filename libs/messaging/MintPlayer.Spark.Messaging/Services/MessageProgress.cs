using MintPlayer.Spark.Messaging.Abstractions;
using MintPlayer.Spark.Messaging.Models;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Scoped <see cref="IMessageProgress"/>. <see cref="MessageProcessor"/> points it at the running
/// handler before each invocation, like <see cref="MessageCheckpoint"/>, and it writes through the
/// processor's session.
/// </summary>
internal sealed class MessageProgress : IMessageProgress
{
    private MessageSession? session;
    private HandlerExecution? handler;
    private string? messageId;
    private int handlerIndex;

    // A copy of the stored steps. The tracked sidecar entity itself is never mutated: a save that both
    // writes a document whole and appends to it is refused (RavenDB), and appending is the point.
    private HashSet<string>? steps;
    private bool exists;

    internal void SetContext(MessageSession currentSession, SparkMessage message, HandlerExecution current, int index)
    {
        session = currentSession;
        handler = current;
        messageId = message.Id;
        handlerIndex = index;
        steps = null;
        exists = false;
    }

    public async Task<bool> IsDoneAsync(string step, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(step);
        return (await LoadAsync(cancellationToken)).Contains(step);
    }

    public async Task MarkDoneAsync(string step, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(step);
        var done = await LoadAsync(cancellationToken);
        if (done.Contains(step))
            return;

        var id = SparkMessageProgress.IdFor(messageId!, handlerIndex);
        if (exists)
        {
            session!.Session.PatchAppend<SparkMessageProgress, string>(id, p => p.Steps, step);
        }
        else
        {
            await session!.Session.StoreAsync(new SparkMessageProgress
            {
                MessageId = messageId!,
                HandlerIndex = handlerIndex,
                Steps = [step],
            }, id, cancellationToken);
            handler!.HasProgress = true;
            exists = true;
        }

        await session.SaveChangesAsync(cancellationToken);
        done.Add(step);
    }

    private async Task<HashSet<string>> LoadAsync(CancellationToken cancellationToken)
    {
        if (session is null || handler is null || messageId is null)
            throw new InvalidOperationException("IMessageProgress can only be used inside a message handler.");

        if (steps is not null)
            return steps;

        var stored = await session.Session.LoadAsync<SparkMessageProgress>(
            SparkMessageProgress.IdFor(messageId, handlerIndex), cancellationToken);
        exists = stored is not null;
        steps = stored is null ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(stored.Steps, StringComparer.Ordinal);
        return steps;
    }
}
