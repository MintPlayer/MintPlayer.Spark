using System.Collections.Concurrent;
using System.Diagnostics;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// <see cref="ISparkSubscriptions"/> (#536 D39) as every provider keeps it: durable, exclusive, at least once, the backlog
/// included. One suite, run once per provider.
/// </summary>
public abstract class SparkSubscriptionsConformanceTests : IAsyncLifetime
{
    public enum SubStatus { Draft, Published }

    public class SubDoc
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public SubStatus Status { get; set; }
        public bool Flag { get; set; }
        public string? Note { get; set; }
    }

    private (ISparkStore Store, ISparkSubscriptions Subscriptions)? created;

    /// <summary>The provider's store; a provider with no server configured skips the test here.</summary>
    protected ISparkStore Store => (created ?? throw new SkipException("No server is configured for this provider.")).Store;

    protected ISparkSubscriptions Subscriptions => (created ?? throw new SkipException("No server is configured for this provider.")).Subscriptions;

    /// <summary>The store (with <see cref="SubDoc"/> mapped) and its subscriptions, or null when the provider's server is not configured.</summary>
    protected abstract Task<(ISparkStore Store, ISparkSubscriptions Subscriptions)?> CreateAsync();

    public virtual async Task InitializeAsync() => created = await CreateAsync();

    public virtual Task DisposeAsync() => Task.CompletedTask;

    protected static readonly SparkSubscriptionRunOptions Fast = new()
    {
        MaxDocsPerBatch = 2,
        RetryDelay = TimeSpan.FromMilliseconds(200),
        MaxDownTime = TimeSpan.FromMinutes(1),
    };

    protected async Task StoreAsync(params SubDoc[] documents)
    {
        using var session = Store.OpenSession();
        foreach (var document in documents)
            await session.StoreAsync(document);
        await session.SaveChangesAsync();
    }

    protected static SubDoc Doc(string id, SubStatus status = SubStatus.Published, bool flag = true, string? note = null)
        => new() { Id = id, Name = id, Status = status, Flag = flag, Note = note };

    /// <summary>A running subscription that records every batch it was handed.</summary>
    protected sealed class Runner : IAsyncDisposable
    {
        private readonly CancellationTokenSource stop = new();
        private readonly Task run;

        public ConcurrentQueue<IReadOnlyList<string>> Batches { get; } = new();

        public Runner(ISparkSubscriptions subscriptions, SparkSubscriptionDefinition<SubDoc> definition,
            SparkSubscriptionRunOptions options, Func<IReadOnlyList<string>, Task>? before = null)
        {
            run = Task.Run(() => subscriptions.RunAsync(definition, options, async (batch, ct) =>
            {
                var ids = batch.Items.Select(i => i.Id.ToLowerInvariant()).ToList();
                if (before is not null)
                    await before(ids);
                Batches.Enqueue(ids);
            }, stop.Token));
        }

        public IReadOnlyList<string> Received => [.. Batches.SelectMany(b => b)];

        /// <summary>The run itself: it ends on a stop, or throws when the subscription cannot run.</summary>
        public Task Completion => run;

        /// <summary>Waits until every one of <paramref name="ids"/> arrived; 30 s is the failure bound.</summary>
        public async Task WaitForAsync(params string[] ids)
        {
            var clock = Stopwatch.StartNew();
            while (!ids.All(Received.Contains))
            {
                if (run.IsCompleted)
                    await run;
                if (clock.Elapsed > TimeSpan.FromSeconds(30))
                    throw new TimeoutException($"Waited 30 s for {string.Join(", ", ids)}; received {string.Join(", ", Received)}.");
                await Task.Delay(50);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await run;
            stop.Dispose();
        }
    }

    [SkippableFact]
    public async Task The_backlog_and_later_commits_are_delivered()
    {
        // Separate commits: one transaction may come as one batch, however large (PostgreSQL's xid stamp).
        await StoreAsync(Doc("subdocs/a"));
        await StoreAsync(Doc("subdocs/b"));
        await StoreAsync(Doc("subdocs/c"));
        var definition = new SparkSubscriptionDefinition<SubDoc>("conf-backlog");

        await using var runner = new Runner(Subscriptions, definition, Fast);
        await runner.WaitForAsync("subdocs/a", "subdocs/b", "subdocs/c");
        await StoreAsync(Doc("subdocs/d"));
        await runner.WaitForAsync("subdocs/d");

        runner.Batches.Should().AllSatisfy(b => b.Count.Should().BeLessThanOrEqualTo(2));
    }

    [SkippableFact]
    public async Task Only_documents_matching_the_filter_are_delivered()
    {
        await StoreAsync(
            Doc("subdocs/match"),
            Doc("subdocs/draft", status: SubStatus.Draft),
            Doc("subdocs/unflagged", flag: false),
            Doc("subdocs/noted", note: "x"));
        var definition = new SparkSubscriptionDefinition<SubDoc>("conf-filter",
            x => x.Status == SubStatus.Published && x.Flag && x.Note == null);

        await using var runner = new Runner(Subscriptions, definition, Fast);
        await runner.WaitForAsync("subdocs/match");
        await StoreAsync(Doc("subdocs/draft2", status: SubStatus.Draft), Doc("subdocs/marker"));
        await runner.WaitForAsync("subdocs/marker");

        runner.Received.Should().BeEquivalentTo(["subdocs/match", "subdocs/marker"]);
    }

    [SkippableFact]
    public async Task A_batch_whose_handler_threw_is_delivered_again()
    {
        await StoreAsync(Doc("subdocs/a"));
        var attempts = 0;
        var retries = 0;
        var options = new SparkSubscriptionRunOptions
        {
            MaxDocsPerBatch = 2,
            RetryDelay = TimeSpan.FromMilliseconds(200),
            MaxDownTime = TimeSpan.FromMinutes(1),
            OnRetry = _ => Interlocked.Increment(ref retries),
        };

        await using var runner = new Runner(Subscriptions, new SparkSubscriptionDefinition<SubDoc>("conf-retry"), options, _ =>
            Interlocked.Increment(ref attempts) == 1 ? throw new InvalidOperationException("first attempt fails") : Task.CompletedTask);
        await runner.WaitForAsync("subdocs/a");

        attempts.Should().BeGreaterThanOrEqualTo(2);
        retries.Should().BeGreaterThanOrEqualTo(1);
    }

    [SkippableFact]
    public async Task A_restarted_run_resumes_after_what_it_handled()
    {
        var definition = new SparkSubscriptionDefinition<SubDoc>("conf-resume");
        await StoreAsync(Doc("subdocs/a"), Doc("subdocs/b"));
        await using (var first = new Runner(Subscriptions, definition, Fast))
        {
            await first.WaitForAsync("subdocs/a", "subdocs/b");
            // A later batch arrives only once the earlier one is acknowledged, so a and b are settled when it does.
            await StoreAsync(Doc("subdocs/settle"));
            await first.WaitForAsync("subdocs/settle");
        }

        await StoreAsync(Doc("subdocs/c"));
        await using var second = new Runner(Subscriptions, definition, Fast);
        await second.WaitForAsync("subdocs/c");

        second.Received.Should().NotContain("subdocs/a");
        second.Received.Should().NotContain("subdocs/b");
    }

    [SkippableFact]
    public async Task A_second_runner_waits_until_the_first_stops()
    {
        var definition = new SparkSubscriptionDefinition<SubDoc>("conf-exclusive");
        var first = new Runner(Subscriptions, definition, Fast);
        await using var second = new Runner(Subscriptions, definition, Fast);
        try
        {
            await StoreAsync(Doc("subdocs/a"));
            // One of the two holds the subscription; the other receives nothing while it does.
            var holder = await HolderAsync(first, second, "subdocs/a");
            await StoreAsync(Doc("subdocs/b"));
            await holder.WaitForAsync("subdocs/b");

            (holder == first ? second : first).Received.Should().BeEmpty();
            if (holder == second)
                return;
        }
        finally
        {
            await first.DisposeAsync();
        }

        await StoreAsync(Doc("subdocs/c"));
        await second.WaitForAsync("subdocs/c");
    }

    private static async Task<Runner> HolderAsync(Runner first, Runner second, string id)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (first.Received.Contains(id))
                return first;
            if (second.Received.Contains(id))
                return second;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Neither runner received {id} within 30 s.");
    }

    /// <summary>A delete under a running subscription ends the run: it does not start over from the first document.</summary>
    [SkippableFact]
    public async Task A_subscription_deleted_under_its_runner_ends_the_run()
    {
        var definition = new SparkSubscriptionDefinition<SubDoc>("conf-deleted");
        await StoreAsync(Doc("subdocs/a"));
        var runner = new Runner(Subscriptions, definition, Fast);
        await runner.WaitForAsync("subdocs/a");

        await Subscriptions.DeleteAsync(definition.Name);

        var ended = await Task.WhenAny(runner.Completion, Task.Delay(TimeSpan.FromSeconds(30)));
        ended.Should().BeSameAs(runner.Completion, "the run ends within 30 s of the delete");
        await ((Func<Task>)(() => runner.Completion)).Should().ThrowAsync<SparkSubscriptionException>();
        runner.Received.Should().HaveCount(1);
    }

    [SkippableFact]
    public async Task Names_list_the_definitions_and_a_delete_removes_one()
    {
        await Subscriptions.EnsureAsync(new SparkSubscriptionDefinition<SubDoc>("conf-one"));
        await Subscriptions.EnsureAsync(new SparkSubscriptionDefinition<SubDoc>("conf-two", x => x.Flag));
        await Subscriptions.EnsureAsync(new SparkSubscriptionDefinition<SubDoc>("conf-two", x => !x.Flag));

        (await Subscriptions.GetNamesAsync()).Should().Contain("conf-one").And.Contain("conf-two");

        await Subscriptions.DeleteAsync("conf-one");
        await Subscriptions.DeleteAsync("conf-missing");

        var names = await Subscriptions.GetNamesAsync();
        names.Should().NotContain("conf-one");
        names.Should().Contain("conf-two");
    }
}
