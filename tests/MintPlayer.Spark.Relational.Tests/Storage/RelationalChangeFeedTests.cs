using Microsoft.EntityFrameworkCore;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Model;
using MintPlayer.Spark.Relational.Storage;
using MintPlayer.Spark.Relational.Tests._Infrastructure;

namespace MintPlayer.Spark.Relational.Tests.Storage;

[ValueObject]
public class FeedLine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = string.Empty;
}

public class FeedNote
{
    public Id<FeedNote> Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public List<FeedLine> Lines { get; set; } = [];
}

/// <summary>
/// The change feed on the relational providers (#536 M4e, D6): a commit-ordered stamp on every table, tombstones for
/// deletes, and reads below the safe horizon, so a transaction that commits late is never skipped.
/// </summary>
public abstract class RelationalChangeFeedTests : IAsyncLifetime
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private RelationalTestDatabase? database;
    private RelationalSparkStore? other;

    protected abstract RelationalTestServer? Server { get; }

    private RelationalSparkStore Store => database?.Store ?? throw new SkipException("No server configured for this provider.");

    /// <summary>A second store over the same database: another process's writes, which the in-process half never sees.</summary>
    private RelationalSparkStore Other => other ??= new RelationalSparkStore(Store.Setup, Store.ConnectionString);

    public async Task InitializeAsync()
    {
        if (Server is { } server)
            database = await RelationalTestDatabase.CreateAsync(server, [typeof(FeedNote)]);
    }

    public async Task DisposeAsync()
    {
        if (database is not null)
            await database.DisposeAsync();
    }

    private async Task<Id<FeedNote>> StoreNoteAsync(RelationalSparkStore store, string title)
    {
        using var session = store.OpenSession();
        var note = new FeedNote { Title = title };
        await session.StoreAsync(note);
        await session.SaveChangesAsync();
        return note.Id;
    }

    private async Task<ulong> StampOfAsync(Id<FeedNote> id)
    {
        await using var db = Store.NewContext();
        return await db.Set<FeedNote>().Where(n => n.Id == id).Select(n => EF.Property<ulong>(n, SparkDbContext.StampProperty)).SingleAsync();
    }

    [SkippableFact]
    public async Task A_write_and_a_child_row_change_move_the_stamp()
    {
        var id = await StoreNoteAsync(Store, "One");
        var inserted = await StampOfAsync(id);

        using (var session = Store.OpenSession())
        {
            (await session.LoadAsync<FeedNote>(id.Value))!.Title = "Two";
            await session.SaveChangesAsync();
        }
        var updated = await StampOfAsync(id);

        using (var session = Store.OpenSession())
        {
            (await session.LoadAsync<FeedNote>(id.Value))!.Lines.Add(new FeedLine { Text = "child" });
            await session.SaveChangesAsync();
        }
        var childChanged = await StampOfAsync(id);

        inserted.Should().BeGreaterThan(0UL);
        updated.Should().BeGreaterThan(inserted);
        childChanged.Should().BeGreaterThan(updated, "a child row change moves the root's version, so its row is written (D3)");
    }

    [SkippableFact]
    public async Task OnCommitted_reports_this_process_s_stores_and_deletes_after_the_commit()
    {
        using var feed = new RelationalSparkChangeFeed(Store);
        var seen = new List<SparkStoreChange>();
        using var _ = feed.OnCommitted(c => { lock (seen) seen.Add(c); });

        var id = await StoreNoteAsync(Store, "One");
        using (var session = Store.OpenSession())
        {
            session.Delete<FeedNote>(id);
            await session.SaveChangesAsync();
        }

        seen.Should().Equal([
            new SparkStoreChange(id.Value, "FeedNotes", "1"),
            new SparkStoreChange(id.Value, "FeedNotes", null),
        ]);
    }

    [SkippableFact]
    public async Task A_collection_watch_sees_another_process_s_write_and_delete()
    {
        using var feed = new RelationalSparkChangeFeed(Store, TimeSpan.FromMilliseconds(50));
        var seen = new List<SparkStoreChange>();
        var watch = feed.WatchCollection("feednotes", c => { lock (seen) seen.Add(c); });
        await watch.Confirmed.WaitAsync(Bound);

        var id = await StoreNoteAsync(Other, "Elsewhere");
        await WaitUntilAsync(() => seen.Any(c => c.DocumentId == id.Value && c.Version == "1"));

        using (var session = Other.OpenSession())
        {
            session.Delete<FeedNote>(id);
            await session.SaveChangesAsync();
        }
        await WaitUntilAsync(() => seen.Any(c => c.DocumentId == id.Value && c.Version is null));

        watch.Subscription.Dispose();
    }

    [SkippableFact]
    public async Task A_document_watch_reports_that_document_only()
    {
        var watched = await StoreNoteAsync(Store, "Watched");
        var unwatched = await StoreNoteAsync(Store, "Not watched");
        using var feed = new RelationalSparkChangeFeed(Store, TimeSpan.FromHours(1));
        var seen = new List<SparkStoreChange>();
        using var watch = feed.WatchDocument(watched.Value.ToUpperInvariant(), c => { lock (seen) seen.Add(c); }).Subscription;
        await feed.PollAsync(default);

        foreach (var id in new[] { watched, unwatched })
            using (var session = Other.OpenSession())
            {
                (await session.LoadAsync<FeedNote>(id.Value))!.Title += "!";
                await session.SaveChangesAsync();
            }
        await PollUntilAsync(feed, () => seen.Contains(new SparkStoreChange(watched.Value, "FeedNotes", "2")));

        // A write from before the watch may still be reported once (PostgreSQL's horizon is the whole server's), never missed.
        seen.Should().OnlyContain(c => c.DocumentId == watched.Value);
    }

    [SkippableFact]
    public async Task A_transaction_that_commits_late_is_read_once_it_has_committed()
    {
        using var feed = new RelationalSparkChangeFeed(Store, TimeSpan.FromHours(1));
        var seen = new List<string?>();
        using var watch = feed.WatchCollection("FeedNotes", c => { lock (seen) seen.Add(c.DocumentId); }).Subscription;
        await feed.PollAsync(default);

        // A takes its stamp first and stays open; B writes after it and commits.
        await using var a = Store.NewContext();
        await using var transaction = await a.Database.BeginTransactionAsync();
        var late = new FeedNote { Id = Id<FeedNote>.FromTrusted("feednotes/" + Guid.NewGuid().ToString("N")), Title = "Late" };
        a.Add(late);
        a.Entry(late).Property(SparkDbContext.VersionProperty).CurrentValue = 1L;
        await a.SaveChangesAsync();
        var early = await StoreNoteAsync(Other, "Early");

        await feed.PollAsync(default);
        seen.Should().BeEmpty("A holds the horizon back, so B's stamp is not below it yet");

        await transaction.CommitAsync();
        await PollUntilAsync(feed, () => seen.Contains(late.Id.Value) && seen.Contains(early.Value));
        seen.Should().BeEquivalentTo([late.Id.Value, early.Value]);
    }

    [SkippableFact]
    public async Task A_set_based_delete_leaves_a_tombstone_per_deleted_row()
    {
        var one = await StoreNoteAsync(Store, "Gone");
        await StoreNoteAsync(Store, "Kept");
        using var feed = new RelationalSparkChangeFeed(Store, TimeSpan.FromHours(1));
        var seen = new List<SparkStoreChange>();
        using var watch = feed.WatchCollection("FeedNotes", c => { lock (seen) seen.Add(c); }).Subscription;
        await feed.PollAsync(default);

        using (var session = Other.OpenSession())
            (await session.DeleteWhereAsync<FeedNote>(n => n.Title == "Gone")).Should().Be(1);
        await PollUntilAsync(feed, () => seen.Contains(new SparkStoreChange(one.Value, "FeedNotes", null)));

        seen.Where(c => c.Version is null).Should().Equal([new SparkStoreChange(one.Value, "FeedNotes", null)]);
        (await RelationalSparkChangeFeed.TrimTombstonesAsync(Store, DateTimeOffset.UtcNow.AddMinutes(1), default))
            .Should().Be(1, "a tombstone past its retention is deleted");
    }

    /// <summary>Polls until <paramref name="condition"/> holds: on PostgreSQL another database's open transaction holds the horizon back.</summary>
    private static async Task PollUntilAsync(RelationalSparkChangeFeed feed, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (true)
        {
            await feed.PollAsync(default);
            if (condition())
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The change was not reported within " + Bound + ".");
            await Task.Delay(20);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The change was not reported within " + Bound + ".");
            await Task.Delay(20);
        }
    }
}

public sealed class SqlServerChangeFeedTests : RelationalChangeFeedTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.SqlServer;
}

public sealed class PostgreSqlChangeFeedTests : RelationalChangeFeedTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.PostgreSql;
}
