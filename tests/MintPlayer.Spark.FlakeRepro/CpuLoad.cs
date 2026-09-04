namespace MintPlayer.Spark.FlakeRepro;

/// <summary>
/// Saturates every logical core for as long as it is held.
/// </summary>
/// <remarks>
/// The reproduction needs CPU starvation, not disk or memory pressure — that is the finding this
/// whole project exists to pin down. Threads rather than tasks, because the thread pool would happily
/// co-operate with the code under test and the point is that it does not get to.
/// <para>
/// <b>Priority selects which failure you get</b>, and this was measured. At
/// <see cref="ThreadPriority.Highest"/> the embedded server never finishes starting and every cycle
/// fails with <c>Server failed to start in 60 s</c> — the historical suite-wide cascade, amplified by
/// RavenTestDriver caching the faulted store factory in a <c>Lazy</c>. At
/// <see cref="ThreadPriority.Normal"/> the server survives but cannot service a deletion notification
/// inside its 15s wait, which is the <c>AdminDatabasesHandler.Delete()</c> timeout. Same starvation,
/// two severities.
/// </para>
/// </remarks>
internal sealed class CpuLoad : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Thread[] threads;

    public CpuLoad(int? degree = null, ThreadPriority priority = ThreadPriority.Normal)
    {
        var count = degree ?? Environment.ProcessorCount;
        threads = new Thread[count];

        for (var i = 0; i < count; i++)
        {
            threads[i] = new Thread(Spin)
            {
                IsBackground = true,   // never blocks process exit, however the run ends
                Priority = priority,
                Name = $"cpu-load-{i}",
            };
            threads[i].Start();
        }
    }

    private void Spin()
    {
        var token = cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            // A bare empty loop can be optimised away; this cannot, and stays branch-predictable
            // enough to keep the core genuinely busy rather than stalled on memory.
            var x = 0d;
            for (var i = 0; i < 1_000_000; i++)
                x += i * 0.5;

            GC.KeepAlive(x);
        }
    }

    public void Dispose()
    {
        cancellation.Cancel();

        // Joined rather than abandoned: an orphaned spinner outlives the run and silently poisons
        // whatever executes next. That happened during the investigation — eight of them survived a
        // killed script and kept every core pinned until they were hunted down by hand.
        foreach (var thread in threads)
            thread.Join(TimeSpan.FromSeconds(5));

        cancellation.Dispose();
    }
}
