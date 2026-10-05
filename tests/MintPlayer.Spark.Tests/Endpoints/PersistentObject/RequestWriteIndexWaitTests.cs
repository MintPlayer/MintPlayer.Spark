using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Client;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Operations.Indexes;
using Raven.Client.Documents.Session;
using PO = MintPlayer.Spark.Abstractions.PersistentObject;
using POA = MintPlayer.Spark.Abstractions.PersistentObjectAttribute;

namespace MintPlayer.Spark.Tests.Endpoints.PersistentObject;

/// <summary>
/// Read-after-write for a user's own write (#264): a write <see cref="IDatabaseAccess"/> makes while
/// serving an HTTP request commits FIRST, then waits — separately, bounded, never failing — for the
/// indexes over the collections it wrote, so the same user's next query sees it.
/// </summary>
/// <remarks>
/// Made deterministic by STOPPING the index rather than hoping it lags: a stopped index never catches
/// up, so "the request is still waiting while the write is already committed" is observable on demand
/// instead of only under CPU load. The E2E symptom this pins was QnA's Answers card re-fetching after
/// the row menu's Duplicate and reading the stale index (QnASubQueryTests). The rejected predecessor,
/// <c>WaitForIndexesAfterSaveChanges</c>, waited inside the commit request and answered a committed
/// write with HTTP 500 when the index was disposed during the wait — the last two tests pin that case.
/// </remarks>
public class RequestWriteIndexWaitTests : SparkTestDriver
{
    private static readonly Guid PersonTypeId = Guid.Parse("7a1e5c30-2b4d-4e8f-9a61-0c3d5e7f9b21");

    private readonly LogRecorder logs = new();
    private SparkEndpointFactory _factory = null!;
    private SparkClient _client = null!;

    public sealed class People_ByFirstName : AbstractIndexCreationTask<Person>
    {
        public People_ByFirstName()
        {
            Map = people => from p in people select new { p.FirstName };
        }
    }

    private static readonly string IndexName = new People_ByFirstName().IndexName;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _factory = new SparkEndpointFactory(Store, [TestModels.Person(PersonTypeId)],
            configureServices: services => services.AddSingleton<ILoggerProvider>(logs));
        _client = new SparkClient(_factory.CreateClient(), ownsClient: true);

        await new People_ByFirstName().ExecuteAsync(Store);
        await Store.WaitForIndexingAsync();
    }

    public override async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await base.DisposeAsync();
    }

    private static PO NewPerson(string firstName) => new()
    {
        Name = "Person",
        ObjectTypeId = PersonTypeId,
        Attributes =
        [
            new POA { Name = "FirstName", Value = firstName },
            new POA { Name = "LastName", Value = "Smith" },
        ],
    };

    private Task StopIndexAsync() => Store.Maintenance.SendAsync(new StopIndexOperation(IndexName));
    private Task StartIndexAsync() => Store.Maintenance.SendAsync(new StartIndexOperation(IndexName));

    /// <summary>How many People documents are stored — read from the collection, no index involved.</summary>
    private async Task<long> StoredPeopleAsync()
    {
        var stats = await Store.Maintenance.SendAsync(new GetCollectionStatisticsOperation());
        return stats.Collections.TryGetValue("People", out var count) ? count : 0;
    }

    private bool WarnedAbout(string index)
        => logs.Has(LogLevel.Warning, nameof(DatabaseAccess), $"index {index} ");

    /// <summary>
    /// Starts a create against the stopped index and returns once the request is provably in the wait:
    /// still pending, with its write already committed.
    /// </summary>
    private async Task<Task<PO>> CreateAndReachTheWaitAsync(string firstName)
    {
        await StopIndexAsync();
        var create = _client.CreatePersistentObjectAsync(NewPerson(firstName));

        var deadline = Stopwatch.StartNew();
        while (await StoredPeopleAsync() == 0 && deadline.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(50);

        (await StoredPeopleAsync()).Should().Be(1, "the write commits BEFORE the wait starts");
        create.IsCompleted.Should().BeFalse("the request waits for the stopped index over the document it wrote");
        return create;
    }

    [Fact]
    public async Task A_request_save_returns_only_once_the_index_has_the_write()
    {
        var create = await CreateAndReachTheWaitAsync("Alice");

        // While the index cannot catch up, the save does not answer. Before the fix it answered at once,
        // and the client's next query read an index that did not have the row yet.
        (await Task.WhenAny(create, Task.Delay(TimeSpan.FromSeconds(2)))).Should().NotBeSameAs(create);

        await StartIndexAsync();
        (await create).Id.Should().NotBeNullOrEmpty();

        // Straight after the response, with NO wait on the query: the index already has the row.
        using var session = Store.OpenAsyncSession();
        var hits = await session.Query<Person, People_ByFirstName>()
            .Statistics(out QueryStatistics stats)
            .Where(p => p.FirstName == "Alice")
            .ToListAsync();

        hits.Should().ContainSingle();
        stats.IsStale.Should().BeFalse();
        WarnedAbout(IndexName).Should().BeFalse("the index caught up inside the bound");
    }

    [Fact]
    public async Task The_wait_is_bounded_and_a_timeout_does_not_fail_the_committed_write()
    {
        await StopIndexAsync();

        var watch = Stopwatch.StartNew();
        var created = await _client.CreatePersistentObjectAsync(NewPerson("Bob"));
        watch.Stop();

        // The bound is a failure bound: an index that is not back in 15 s is broken or paused, and the
        // user gets the answer (their write is saved) rather than an error for a write that succeeded.
        created.Id.Should().NotBeNullOrEmpty();
        watch.Elapsed.Should().BeGreaterThanOrEqualTo(DatabaseAccess.RequestWriteIndexWait - TimeSpan.FromSeconds(1));
        watch.Elapsed.Should().BeLessThan(DatabaseAccess.RequestWriteIndexWait + TimeSpan.FromSeconds(10));
        WarnedAbout(IndexName).Should().BeTrue("a write left behind by its index is logged, not surfaced");

        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<Person>(created.Id)).Should().NotBeNull("the write itself was committed");
    }

    [Fact]
    public async Task A_write_outside_a_request_does_not_wait_for_indexes()
    {
        await StopIndexAsync();

        // Message handlers, cron jobs, migrations: a DI scope without an HttpContext. Nobody reads
        // straight after them, so they keep Raven's default and do not pay for the wait.
        using var scope = _factory.CreateScope();
        var databaseAccess = scope.ServiceProvider.GetRequiredService<IDatabaseAccess>();

        var watch = Stopwatch.StartNew();
        await databaseAccess.SaveDocumentUncheckedAsync(new Person { FirstName = "Carol", LastName = "Smith" });
        watch.Stop();

        watch.Elapsed.Should().BeLessThan(DatabaseAccess.RequestWriteIndexWait - TimeSpan.FromSeconds(5),
            "a background save against a stopped index would otherwise sit out the whole bound");
        (await StoredPeopleAsync()).Should().Be(1);
        WarnedAbout(IndexName).Should().BeFalse("nothing waited");
    }

    [Fact]
    public async Task An_index_deleted_during_the_wait_still_answers_the_committed_write()
    {
        var create = await CreateAndReachTheWaitAsync("Dave");

        // The index the request waits on is disposed under it — what an auto-index merge or a
        // side-by-side swap does. The predecessor answered this committed write with HTTP 500.
        var watch = Stopwatch.StartNew();
        await Store.Maintenance.SendAsync(new DeleteIndexOperation(IndexName));

        var created = await create;
        watch.Stop();

        created.Id.Should().NotBeNullOrEmpty("the write committed, so the request answers it");
        watch.Elapsed.Should().BeLessThan(DatabaseAccess.RequestWriteIndexWait - TimeSpan.FromSeconds(3),
            "a disposed index ends the wait at once instead of sitting out the bound");
        WarnedAbout(IndexName).Should().BeTrue("the failed wait is logged and swallowed");
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<Person>(created.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task An_index_replaced_during_the_wait_still_answers_the_committed_write()
    {
        var create = await CreateAndReachTheWaitAsync("Erin");

        // A reset disposes the index instance the query waits on and builds a new one under the same
        // name — the replacement half of a side-by-side swap.
        await Store.Maintenance.SendAsync(new ResetIndexOperation(IndexName));
        await StartIndexAsync();

        var created = await create;

        created.Id.Should().NotBeNullOrEmpty("the write committed, so the request answers it");
        using var session = Store.OpenAsyncSession();
        (await session.LoadAsync<Person>(created.Id)).Should().NotBeNull();
        var hits = await session.Query<Person, People_ByFirstName>()
            .Customize(x => x.WaitForNonStaleResults())
            .Where(p => p.FirstName == "Erin")
            .ToListAsync();
        hits.Should().ContainSingle("the replacement index has the write");
    }

    /// <summary>Captures every log line with its category.</summary>
    private sealed class LogRecorder : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Message)> entries = new();

        public bool Has(LogLevel level, string category, string fragment)
            => entries.Any(e => e.Level == level
                && e.Category.EndsWith(category, StringComparison.Ordinal)
                && e.Message.Contains(fragment, StringComparison.Ordinal));

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, entries);
        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<(string, LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue((category, logLevel, formatter(state, exception)));
        }
    }
}
