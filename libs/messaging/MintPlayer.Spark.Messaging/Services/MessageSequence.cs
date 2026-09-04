namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Issues the strictly increasing <see cref="Models.SparkMessage.Sequence"/> that gives messages
/// their broadcast order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the document id.</b> The obvious tiebreak — <c>OrderBy(CreatedAtUtc).ThenBy(Id)</c> —
/// is wrong, and silently so. It compiles to RavenDB's <c>order by id()</c>, a <b>lexicographic</b>
/// sort, and hilo ids are not zero-padded: <c>SparkMessages/10-A</c> sorts before
/// <c>SparkMessages/2-A</c>. Measured over 5000 messages sharing one <c>CreatedAtUtc</c>, the server's
/// order diverged from insertion order at row 1. It is not a weak tiebreak that usually works; it is
/// wrong for every pair spanning a digit-count boundary, which is most pairs.
/// </para>
/// <para>
/// <b>Why not <see cref="DateTime.UtcNow"/> alone.</b> The system clock has coarse granularity and
/// can step backwards (NTP). A producer broadcasting in a loop — which is exactly what an ingestion
/// burst does — issues many messages inside one tick, so timestamps tie and the order within a
/// partition becomes arbitrary.
/// </para>
/// <para>
/// So the sequence is monotonic <i>by construction</i>: it never repeats and never goes backwards
/// within a process, whatever the clock does. It is seeded from the clock so that values remain
/// roughly comparable across restarts and across processes, and ordering is only ever required
/// <i>within a partition</i> — normally one causal chain on one host.
/// </para>
/// </remarks>
internal sealed class MessageSequence(TimeProvider timeProvider)
{
    private long last;

    /// <summary>
    /// The next value, strictly greater than every value this instance has issued.
    /// </summary>
    public long Next()
    {
        var now = timeProvider.GetUtcNow().UtcTicks;

        // Compare-and-swap rather than Interlocked.Increment: the value tracks the clock when the
        // clock is moving, and falls back to "previous + 1" when two calls land inside one tick or
        // the clock steps backwards. Both cases are real; the loop is what makes them harmless.
        while (true)
        {
            var previous = Interlocked.Read(ref last);
            var candidate = now > previous ? now : previous + 1;

            if (Interlocked.CompareExchange(ref last, candidate, previous) == previous)
                return candidate;
        }
    }
}
