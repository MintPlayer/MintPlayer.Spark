using System.Diagnostics;
using System.Reflection;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;

namespace MintPlayer.Spark.FlakeRepro;

/// <summary>
/// Minimal reproduction of the teardown failure that makes the suite flaky:
/// <code>
/// TimeoutException: Waited for 00:00:15 for task with index N to complete. Last commit index is: M.
///   at AdminDatabasesHandler.WaitForDeletionToComplete → AdminDatabasesHandler.Delete()
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// <b>What this strips away.</b> The failure was first seen in the messaging E2E classes, which were
/// the only ones running background loops against the store — so the natural reading was that a
/// lane pump's 15s <c>WaitForNonStaleResults</c> was holding the database open. It is not that: this
/// reproduction has no messaging, no hosted services, no background work, no documents and no
/// indexes. It creates a database, does nothing at all, and deletes it.
/// </para>
/// <para>
/// <b>What it keeps.</b> Only CPU starvation. Under an idle machine the suite passed 11 consecutive
/// runs; under saturation it produced 191 failures in one. The raft indices are the decisive
/// evidence — failures appear at <c>index 65, last commit 92</c>, a log barely a hundred entries
/// long, which rules out the theory that ~676 accumulated create/delete commands are what break it.
/// </para>
/// <para>
/// <b>Read the numbers, not the pass/fail.</b> <see cref="Databases_can_be_created_and_deleted"/>
/// reports how many deletions exceeded the server's 15s wait. Zero on an idle machine is expected
/// and is not evidence of a fix — run it against a build with the candidate change AND without,
/// under the same load, and compare.
/// </para>
/// </remarks>
public class DatabaseTeardownTimeoutRepro
{
    /// <summary>Cycles per worker. Each cycle is a full database created and deleted on the shared server.</summary>
    private const int CyclesPerWorker = 12;

    /// <summary>Documents written per database, so indexing and deletion both cost something.</summary>
    private const int DocumentsPerDatabase = 300;

    /// <summary>
    /// Concurrent workers, mirroring the suite's own parallelism.
    /// </summary>
    /// <remarks>
    /// This is the ingredient sequential churn was missing: <c>xunit.runner.json</c> sets
    /// <c>maxParallelThreads: 0.5x</c>, so on an 8-core machine four test classes create and delete
    /// their databases at the same time. Forty SEQUENTIAL cycles under full CPU load produced zero
    /// failures and a slowest delete of 0.1s — deleting an idle database is cheap even on a starved
    /// machine. Deletions competing with each other for the same single-threaded raft apply loop is
    /// what makes one of them wait behind the others.
    /// </remarks>
    private static int Workers => Math.Max(2, Environment.ProcessorCount / 2);

    /// <summary>Exposes the driver's lifecycle so the loop can drive it without xUnit's fixture machinery.</summary>
    private sealed class Harness : SparkTestDriver
    {
        // A licence is irrelevant here — nothing licensed is touched — and demanding one would make
        // the reproduction unrunnable for anyone who cannot supply it.
        protected override bool RequireLicense => false;

        // Every real fixture deploys indexes; an empty database is not what the suite deletes.
        protected override IEnumerable<Assembly> IndexAssemblies => [typeof(Things_ByName).Assembly];

        /// <summary>The driver keeps Store protected; the loop drives it from outside.</summary>
        public IDocumentStore Db => Store;
    }

    /// <summary>Gives the database something to hold and something to do before it is deleted.</summary>
    private static async Task FillAsync(Harness harness)
    {
        using var session = harness.Db.OpenAsyncSession();

        for (var i = 0; i < DocumentsPerDatabase; i++)
        {
            await session.StoreAsync(new Thing
            {
                Name = $"thing-{i}",
                Category = $"category-{i % 7}",
                Value = i,
            });
        }

        await session.SaveChangesAsync();

        // A query forces the index to actually run rather than sit idle behind the write.
        using var reader = harness.Db.OpenAsyncSession();
        _ = await reader.Query<Thing, Things_ByName>()
            .Customize(x => x.WaitForNonStaleResults(TimeSpan.FromSeconds(30)))
            .Where(t => t.Value > DocumentsPerDatabase / 2)
            .ToListAsync();
    }

    [Fact]
    public async Task Databases_can_be_created_and_deleted()
    {
        var timeouts = new System.Collections.Concurrent.ConcurrentBag<string>();
        var created = 0;
        var slowestTicks = 0L;

        // Start the server BEFORE the load, or it never starts at all: RavenTestDriver spins up the
        // embedded server lazily on the first GetDocumentStore, and a saturated machine misses its
        // 60s startup budget. Worse, the driver caches that faulted factory in a Lazy, so every
        // later cycle inherits the same stale exception and this measures nothing. Warming up first
        // isolates the DELETION path, which is what this test is about.
        var warmup = new Harness();
        await warmup.InitializeAsync();
        await warmup.DisposeAsync();

        using (new CpuLoad())
        {
            await Task.WhenAll(Enumerable.Range(0, Workers).Select(worker => Task.Run(async () =>
            {
                for (var i = 0; i < CyclesPerWorker; i++)
                {
                    var harness = new Harness();

                    try
                    {
                        await harness.InitializeAsync();
                        Interlocked.Increment(ref created);
                    }
                    catch (Exception ex)
                    {
                        // Creation can starve too, and its failure is a different bug with a
                        // different stack. Recorded separately so the two are never conflated.
                        timeouts.Add($"w{worker} cycle {i}: CREATE failed: {Summarize(ex)}");
                        continue;
                    }

                    try
                    {
                        await FillAsync(harness);
                    }
                    catch (Exception ex)
                    {
                        timeouts.Add($"w{worker} cycle {i}: FILL failed: {Summarize(ex)}");
                    }

                    var watch = Stopwatch.StartNew();
                    try
                    {
                        await harness.DisposeAsync();
                    }
                    catch (Exception ex)
                    {
                        timeouts.Add(
                            $"w{worker} cycle {i}: DELETE failed after "
                            + $"{watch.Elapsed.TotalSeconds:F1}s: {Summarize(ex)}");
                    }

                    var ticks = watch.Elapsed.Ticks;
                    long seen;
                    while (ticks > (seen = Interlocked.Read(ref slowestTicks)))
                        if (Interlocked.CompareExchange(ref slowestTicks, ticks, seen) == seen)
                            break;
                }
            })));
        }

        var slowest = TimeSpan.FromTicks(Interlocked.Read(ref slowestTicks));

        Console.WriteLine(
            $"[repro] cores={Environment.ProcessorCount} workers={Workers} "
            + $"cycles={Workers * CyclesPerWorker} created={created} "
            + $"failures={timeouts.Count} slowest-delete={slowest.TotalSeconds:F1}s");

        foreach (var line in timeouts)
            Console.WriteLine($"[repro] {line}");

        timeouts.Should().BeEmpty(
            "a database must be deletable within the server's 15s confirmation wait even when every "
            + "core is busy; {0} of {1} cycles exceeded it",
            timeouts.Count, Workers * CyclesPerWorker);
    }

    /// <summary>The first line that carries information, unwrapped from RavenDB's exception nesting.</summary>
    private static string Summarize(Exception exception)
    {
        var inner = exception;
        while (inner.InnerException is not null)
            inner = inner.InnerException;

        var message = inner.Message.Split('\n')[0].Trim();
        return $"{inner.GetType().Name}: {message}";
    }
}
