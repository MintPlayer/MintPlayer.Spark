using System.Threading.Channels;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// One lane's claimed-but-unstarted message ids (R29): two FIFO queues — expedited and normal — sharing
/// one capacity, read by the lane's pump(s). It replaces the lane's plain bounded channel, which could
/// only hand out ids in arrival order: an expedited message the feeder claimed first still queued behind
/// every normal id already in the channel.
/// <para>
/// <b>Order.</b> A reader takes the oldest expedited id before any normal one. <b>Starvation cap:</b>
/// after <see cref="StarvationCap"/> expedited ids in a row, the next take is a normal id if one is ready;
/// with none ready, expedited ids keep going and the first normal id to arrive goes next. The counter is
/// per lane, so it holds across the pumps of a <c>MaxConcurrency</c> &gt; 1 lane too.
/// </para>
/// <para>
/// <b>The channel semantics the router relies on are kept:</b> writers wait while the lane is full
/// (backpressure to the feeder), <see cref="TryComplete"/> stops writes (a write then throws
/// <see cref="ChannelClosedException"/>) while readers drain what is left, and a reader's cancellation
/// throws <see cref="OperationCanceledException"/>.
/// </para>
/// <para>
/// Not disposable on purpose: nothing here holds a wait handle or a timer, and a lane dropped by
/// <c>MessageQueueRouter.StopAsync</c> may still have a writer unwinding through it.
/// </para>
/// </summary>
internal sealed class LaneBuffer
{
    /// <summary>Expedited ids taken in a row before a ready normal id must go next.</summary>
    public const int StarvationCap = 10;

    private readonly object gate = new();
    private readonly Queue<string> expedited = new();
    private readonly Queue<string> normal = new();
    private readonly SemaphoreSlim space;
    private readonly SemaphoreSlim items = new(0);
    private readonly CancellationTokenSource completed = new();
    private bool isCompleted;
    private int expeditedInARow;

    public LaneBuffer(int capacity)
    {
        space = new SemaphoreSlim(Math.Max(1, capacity));
    }

    /// <summary>Ids currently held. For tests and diagnostics.</summary>
    public int Count
    {
        get { lock (gate) return expedited.Count + normal.Count; }
    }

    /// <summary>Adds an id, waiting while the lane is full.</summary>
    public async ValueTask WriteAsync(string id, bool expedite, CancellationToken cancellationToken)
    {
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, completed.Token))
        {
            try
            {
                await space.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ChannelClosedException();
            }
        }

        lock (gate)
        {
            if (isCompleted)
            {
                space.Release();
                throw new ChannelClosedException();
            }
            (expedite ? expedited : normal).Enqueue(id);
        }
        items.Release();
    }

    /// <summary>Stops accepting writes; readers still drain what is held. False when already completed.</summary>
    public bool TryComplete()
    {
        lock (gate)
        {
            if (isCompleted)
                return false;
            isCompleted = true;
        }
        completed.Cancel();
        return true;
    }

    /// <summary>Every id in serve order, until the buffer is completed and empty.</summary>
    public async IAsyncEnumerable<string> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await TakeAsync(cancellationToken) is { } id)
            yield return id;
    }

    /// <summary>The next id in serve order, or null once the buffer is completed and drained.</summary>
    private async ValueTask<string?> TakeAsync(CancellationToken cancellationToken)
    {
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, completed.Token))
        {
            try
            {
                // One release per enqueued id and one wait per dequeue, so a successful wait always
                // finds an id.
                await items.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Completed: drain without the semaphore (it may still count ids we are about to take).
                lock (gate)
                    return TryDequeue(out var rest) ? Released(rest) : null;
            }
        }

        lock (gate)
            return TryDequeue(out var id) ? Released(id) : null;
    }

    private string Released(string id)
    {
        space.Release();
        return id;
    }

    /// <summary>The serve order. Callers hold <see cref="gate"/>.</summary>
    private bool TryDequeue(out string id)
    {
        if (expedited.Count > 0 && (expeditedInARow < StarvationCap || normal.Count == 0))
        {
            expeditedInARow++;
            id = expedited.Dequeue();
            return true;
        }

        if (normal.Count > 0)
        {
            expeditedInARow = 0;
            id = normal.Dequeue();
            return true;
        }

        id = null!;
        return false;
    }
}
