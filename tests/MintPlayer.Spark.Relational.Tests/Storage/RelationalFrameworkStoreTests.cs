using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Relational.Model;
using MintPlayer.Spark.Relational.Storage;
using MintPlayer.Spark.Relational.Tests._Infrastructure;

namespace MintPlayer.Spark.Relational.Tests.Storage;

[IdKind(SparkIdKind.Integer)]
public class RelTicket
{
    public Id<RelTicket> Id { get; set; }
    public string Subject { get; set; } = string.Empty;
}

public class RelToken : IExpiring
{
    public string? Id { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}

/// <summary>
/// The store's own tables on the relational providers (#536 M4b): integer ids by HiLo, the Data Protection key ring,
/// and expiration (the stamper and the sweep).
/// </summary>
public abstract class RelationalFrameworkStoreTests : IAsyncLifetime
{
    private RelationalTestDatabase? database;

    protected abstract RelationalTestServer? Server { get; }

    private RelationalSparkStore Store => database?.Store ?? throw new SkipException("No server configured for this provider.");

    public async Task InitializeAsync()
    {
        if (Server is { } server)
            database = await RelationalTestDatabase.CreateAsync(server, [typeof(RelTicket), typeof(RelToken)]);
    }

    public async Task DisposeAsync()
    {
        if (database is not null)
            await database.DisposeAsync();
    }

    [SkippableFact]
    public async Task Two_processes_never_hand_out_the_same_integer_key()
    {
        var values = new RelationalAtomicValues(Store);
        var one = new RelationalIntegerIdSource(values, Store);
        var two = new RelationalIntegerIdSource(values, Store);

        var keys = new List<string>();
        for (var i = 0; i < RelationalIntegerIdSource.RangeSize + 5; i++)
        {
            keys.Add(await one.NextKeyAsync(typeof(RelTicket), default));
            keys.Add(await two.NextKeyAsync(typeof(RelTicket), default));
        }

        keys.Should().OnlyHaveUniqueItems();
        keys.Take(2).Should().Equal(["1", (RelationalIntegerIdSource.RangeSize + 1).ToString()]);
    }

    [SkippableFact]
    public async Task An_integer_keyed_entity_round_trips()
    {
        var source = new RelationalIntegerIdSource(new RelationalAtomicValues(Store), Store);
        var id = Id<RelTicket>.FromTrusted("reltickets/" + await source.NextKeyAsync(typeof(RelTicket), default));

        using (var session = Store.OpenSession())
        {
            await session.StoreAsync(new RelTicket { Id = id, Subject = "Printer" });
            await session.SaveChangesAsync();
        }

        using var read = Store.OpenSession();
        (await read.LoadAsync<RelTicket>(id.Value))!.Subject.Should().Be("Printer");
        (await read.LoadStartingWithAsync<RelTicket>("reltickets/")).Should().HaveCount(1);
    }

    [SkippableFact]
    public void The_key_ring_round_trips()
    {
        var keys = new RelationalDataProtectionKeyStore(Store);
        keys.StoreElement(new XElement("key", new XAttribute("id", "one")), "key-one");
        keys.StoreElement(new XElement("key", new XAttribute("id", "two")), "");

        keys.GetAllElements().Select(e => (string?)e.Attribute("id")).Should().BeEquivalentTo(["one", "two"]);
    }

    [SkippableFact]
    public async Task The_stamper_mirrors_the_expiry_and_the_sweep_deletes_past_rows()
    {
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedTime(now);
        var expiration = new RelationalExpiration(Store);
        using var stamper = expiration.InstallStamper(t => t == typeof(RelToken) ? TimeSpan.FromHours(1) : null, time);

        using (var session = Store.OpenSession())
        {
            await session.StoreAsync(new RelToken(), "reltokens/defaulted");
            await session.StoreAsync(new RelToken { ExpiresAtUtc = now.UtcDateTime.AddDays(2) }, "reltokens/later");
            await session.SaveChangesAsync();
        }

        using (var session = Store.OpenSession())
        {
            var defaulted = (await session.LoadAsync<RelToken>("reltokens/defaulted"))!;
            defaulted.ExpiresAtUtc.Should().Be(now.UtcDateTime.AddHours(1));
            session.GetExpiry(defaulted).Should().Be(now.AddHours(1));
            session.GetExpiry((await session.LoadAsync<RelToken>("reltokens/later"))!).Should().Be(now.AddDays(2));
        }

        (await expiration.SweepAsync(new FixedTime(now.AddHours(2)), default)).Should().Be(1);

        using var read = Store.OpenSession();
        (await read.ExistsAsync(typeof(RelToken), "reltokens/defaulted")).Should().BeFalse();
        (await read.ExistsAsync(typeof(RelToken), "reltokens/later")).Should().BeTrue();
    }

    [SkippableFact]
    public async Task A_row_that_stops_matching_before_the_delete_stays_and_leaves_no_tombstone()
    {
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        using (new RelationalExpiration(Store).InstallStamper(t => t == typeof(RelToken) ? TimeSpan.FromHours(1) : null, new FixedTime(now)))
        using (var session = Store.OpenSession())
        {
            await session.StoreAsync(new RelToken { ExpiresAtUtc = now.UtcDateTime.AddHours(-1) }, "reltokens/gone");
            await session.StoreAsync(new RelToken { ExpiresAtUtc = now.UtcDateTime.AddHours(-1) }, "reltokens/extended");
            await session.SaveChangesAsync();
        }

        // The ids are read first; before the delete runs, another writer extends one row, so it no longer matches.
        var filtered = 0;
        await using (var db = Store.NewContext())
        {
            var deleted = await RelationalTombstones.DeleteAsync<RelToken>(db, q =>
            {
                if (++filtered == 2)
                {
                    using var other = Store.NewContext();
                    other.Set<RelToken>().Where(t => t.Id == "reltokens/extended")
                        .ExecuteUpdate(s => s.SetProperty(t => EF.Property<DateTimeOffset?>(t, SparkDbContext.ExpiresProperty), now.AddDays(1)));
                }
                return q.Where(x => EF.Property<DateTimeOffset?>(x, SparkDbContext.ExpiresProperty) <= now);
            }, now, default);
            deleted.Should().Be(1);
        }

        await using var check = Store.NewContext();
        (await check.Set<SparkTombstoneRow>().Select(t => t.DocumentId).ToListAsync()).Should().Equal(["reltokens/gone"]);
        using var read = Store.OpenSession();
        (await read.ExistsAsync(typeof(RelToken), "reltokens/extended")).Should().BeTrue();
    }

    [SkippableFact]
    public async Task An_atomic_put_refused_for_another_reason_than_a_taken_key_throws()
    {
        var values = new RelationalAtomicValues(Store);
        (await values.TryPutAsync("taken", "one", 0)).Should().NotBeNull();
        (await values.TryPutAsync("taken", "two", 0)).Should().BeNull();

        // A key longer than the column is no other writer's: the HiLo loop would retry it forever.
        await values.Invoking(v => v.TryPutAsync(new string('k', SparkFrameworkSchema.KeyLength + 1), "value", 0)).Should().ThrowAsync<DbUpdateException>();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public sealed class SqlServerFrameworkStoreTests : RelationalFrameworkStoreTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.SqlServer;
}

public sealed class PostgreSqlFrameworkStoreTests : RelationalFrameworkStoreTests
{
    protected override RelationalTestServer? Server => RelationalTestServer.PostgreSql;
}
