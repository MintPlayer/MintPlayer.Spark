using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Storage.InMemory;
using MintPlayer.Spark.Storage.Raven;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// The store contract every provider keeps (#551 M7): one suite, run once per provider, so a provider that differs from
/// RavenDB on a point core relies on fails here, not in an application. S12's value cases (DateTime, Guid, enums,
/// decimals) and D16 (an absent field reads as the CLR default) are among them.
/// </summary>
public abstract class SparkStoreConformanceTests : IAsyncLifetime
{
    public enum ConfStatus { Draft, Published, Archived }

    public class ConfAddress
    {
        public string Street { get; set; } = string.Empty;
        public int Number { get; set; }
    }

    public class ConfDoc
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
        public decimal Price { get; set; }
        public bool Flag { get; set; }
        public DateTime Utc { get; set; }
        public DateTime Unspecified { get; set; }
        public DateOnly Day { get; set; }
        public Guid Key { get; set; }
        public ConfStatus Status { get; set; }
        public ConfStatus? MaybeStatus { get; set; }
        public string? Nothing { get; set; }
        public ConfAddress? Address { get; set; }
        public List<string> Tags { get; set; } = [];
    }

    /// <summary>The same collection's documents as an older shape wrote them: no Count, Flag or Tags.</summary>
    public class ConfDocV1
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public class ConfPerson { public string? Id { get; set; } }
    public class ConfCategory { public string? Id { get; set; } }
    public class ConfBox { public string? Id { get; set; } }

    protected ISparkStore Store { get; private set; } = null!;

    protected abstract Task<ISparkStore> CreateStoreAsync();

    /// <summary>A query whose reads see every committed write: RavenDB's indexes are eventually consistent.</summary>
    protected virtual IQueryable<T> Settled<T>(IQueryable<T> query) => query;

    public virtual async Task InitializeAsync() => Store = await CreateStoreAsync();

    public virtual Task DisposeAsync() => Task.CompletedTask;

    private static ConfDoc Sample(string id) => new()
    {
        Id = id,
        Name = "Sample",
        Count = 42,
        Price = 1234.5678m,
        Flag = true,
        Utc = new DateTime(2026, 3, 29, 1, 30, 15, 123, DateTimeKind.Utc),
        Unspecified = new DateTime(2026, 10, 25, 2, 45, 0, DateTimeKind.Unspecified),
        Day = new DateOnly(2024, 2, 29),
        Key = Guid.Parse("8d3c2f6e-4b1a-4c55-9a7e-2f1d0b9c8e7a"),
        Status = ConfStatus.Published,
        MaybeStatus = null,
        Address = new ConfAddress { Street = "Main", Number = 7 },
        Tags = ["a", "b"],
    };

    private async Task StoreAsync(params object[] entities)
    {
        using var session = Store.OpenSession();
        foreach (var entity in entities)
            await session.StoreAsync(entity);
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task Values_round_trip_through_a_new_session()
    {
        await StoreAsync(Sample("ConfDocs/values"));

        using var session = Store.OpenSession();
        var loaded = await session.LoadAsync<ConfDoc>("ConfDocs/values");

        loaded.Should().NotBeNull();
        var expected = Sample("ConfDocs/values");
        loaded!.Name.Should().Be(expected.Name);
        loaded.Count.Should().Be(expected.Count);
        loaded.Price.Should().Be(expected.Price);
        loaded.Flag.Should().BeTrue();
        loaded.Utc.Should().Be(expected.Utc);
        loaded.Utc.Kind.Should().Be(DateTimeKind.Utc);
        loaded.Unspecified.Should().Be(expected.Unspecified);
        loaded.Unspecified.Kind.Should().Be(DateTimeKind.Unspecified);
        loaded.Day.Should().Be(expected.Day);
        loaded.Key.Should().Be(expected.Key);
        loaded.Status.Should().Be(ConfStatus.Published);
        loaded.MaybeStatus.HasValue.Should().BeFalse();
        loaded.Nothing.Should().BeNull();
        loaded.Address!.Street.Should().Be("Main");
        loaded.Address.Number.Should().Be(7);
        loaded.Tags.Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public async Task An_absent_field_reads_as_the_clr_default()
    {
        // D16: a document written before a property existed loads with that property at its default.
        await StoreAsync(new ConfDocV1 { Id = "ConfDocs/old", Name = "Old" });

        using var session = Store.OpenSession();
        var loaded = await session.LoadAsync<ConfDoc>("ConfDocs/old");

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("Old");
        loaded.Count.Should().Be(0);
        loaded.Flag.Should().BeFalse();
        loaded.Key.Should().Be(Guid.Empty);
        loaded.Status.Should().Be(ConfStatus.Draft);
        loaded.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task Collection_names_follow_the_same_conventions()
    {
        Store.GetCollectionName(typeof(ConfPerson)).Should().Be("ConfPeople");
        Store.GetCollectionName(typeof(ConfCategory)).Should().Be("ConfCategories");
        Store.GetCollectionName(typeof(ConfBox)).Should().Be("ConfBoxes");
        Store.GetCollectionName(typeof(ConfDoc)).Should().Be("ConfDocs");

        using var session = Store.OpenSession();
        var person = new ConfPerson();
        await session.StoreAsync(person);
        await session.SaveChangesAsync();

        person.Id.Should().NotBeNull();
        person.Id!.StartsWith("ConfPeople/", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        session.GetId(person).Should().Be(person.Id);
        session.GetStoredCollectionName(person).Should().Be("ConfPeople");
    }

    [Fact]
    public async Task A_session_returns_one_instance_per_id()
    {
        await StoreAsync(Sample("ConfDocs/identity"));

        using var session = Store.OpenSession();
        var first = await session.LoadAsync<ConfDoc>("ConfDocs/identity");
        var second = await session.LoadAsync<ConfDoc>("conFDocs/IDENTITY");

        ReferenceEquals(first, second).Should().BeTrue("ids are case-insensitive and a session tracks one instance");
        session.IsLoaded("ConfDocs/identity").Should().BeTrue();
    }

    [Fact]
    public async Task Changes_are_tracked_and_saved()
    {
        await StoreAsync(Sample("ConfDocs/tracked"));

        using (var session = Store.OpenSession())
        {
            var loaded = (await session.LoadAsync<ConfDoc>("ConfDocs/tracked"))!;
            session.HasChanged(loaded).Should().BeFalse();

            loaded.Count = 43;
            session.HasChanged(loaded).Should().BeTrue();
            await session.SaveChangesAsync();
        }

        using var verify = Store.OpenSession();
        (await verify.LoadAsync<ConfDoc>("ConfDocs/tracked"))!.Count.Should().Be(43);
    }

    [Fact]
    public async Task BeforeStore_fires_at_commit_for_new_and_changed_documents_only()
    {
        await StoreAsync(Sample("ConfDocs/hook-a"), Sample("ConfDocs/hook-b"));

        using var session = Store.OpenSession();
        var stored = new List<string?>();
        session.BeforeStore += entity => stored.Add(session.GetId(entity));

        var a = (await session.LoadAsync<ConfDoc>("ConfDocs/hook-a"))!;
        _ = await session.LoadAsync<ConfDoc>("ConfDocs/hook-b");
        a.Name = "Changed";
        await session.StoreAsync(Sample("ConfDocs/hook-c"));

        stored.Should().BeEmpty("the hook runs when the session commits");
        await session.SaveChangesAsync();

        stored.Should().BeEquivalentTo(["ConfDocs/hook-a", "ConfDocs/hook-c"]);
    }

    [Fact]
    public async Task Delete_removes_the_document()
    {
        await StoreAsync(Sample("ConfDocs/delete"));

        using (var session = Store.OpenSession())
        {
            session.Delete("ConfDocs/delete");
            await session.SaveChangesAsync();
        }

        using var verify = Store.OpenSession();
        (await verify.ExistsAsync("ConfDocs/delete")).Should().BeFalse();
        (await verify.LoadAsync<ConfDoc>("ConfDocs/delete")).Should().BeNull();
    }

    [Fact]
    public async Task A_save_that_expects_absence_fails_when_the_document_exists_and_writes_nothing()
    {
        await StoreAsync(Sample("ConfDocs/taken"));

        using (var session = Store.OpenSession())
        {
            await session.StoreAsync(Sample("ConfDocs/bystander"));
            await session.StoreAsync(Sample("ConfDocs/taken"), expectedVersion: string.Empty, id: "ConfDocs/taken");

            var act = () => session.SaveChangesAsync();
            await act.Should().ThrowAsync<SparkStoreConcurrencyException>();
        }

        using var verify = Store.OpenSession();
        (await verify.ExistsAsync("ConfDocs/bystander")).Should().BeFalse("a commit is all or nothing");
    }

    [Fact]
    public async Task A_save_with_a_stale_version_fails()
    {
        await StoreAsync(Sample("ConfDocs/versioned"));

        string staleVersion;
        using (var session = Store.OpenSession())
        {
            var loaded = (await session.LoadAsync<ConfDoc>("ConfDocs/versioned"))!;
            staleVersion = session.GetVersion(loaded)!;
            loaded.Count++;
            await session.SaveChangesAsync();
            session.GetVersion(loaded).Should().NotBe(staleVersion, "a commit gives the document a new version");
        }

        using (var session = Store.OpenSession())
        {
            (await session.GetVersionsAsync(["ConfDocs/versioned"]))["ConfDocs/versioned"].Should().NotBe(staleVersion);
        }

        using var writer = Store.OpenSession();
        await writer.StoreAsync(Sample("ConfDocs/versioned"), staleVersion, "ConfDocs/versioned");
        var act = () => writer.SaveChangesAsync();
        await act.Should().ThrowAsync<SparkStoreConcurrencyException>();
    }

    [Fact]
    public async Task LoadStartingWith_pages_in_id_order()
    {
        var c = Sample("ConfPrefix/c");
        var a = Sample("ConfPrefix/a");
        var b = Sample("ConfPrefix/b");
        await StoreAsync(c, a, b, Sample("ConfOther/a"));

        using var session = Store.OpenSession();
        var all = await session.LoadStartingWithAsync<ConfDoc>("ConfPrefix/");
        all.Select(d => d.Id).Should().BeEquivalentTo(["ConfPrefix/a", "ConfPrefix/b", "ConfPrefix/c"],
            o => o.WithStrictOrdering());

        var page = await session.LoadStartingWithAsync<ConfDoc>("ConfPrefix/", start: 1, pageSize: 1);
        page.Select(d => d.Id).Should().BeEquivalentTo(["ConfPrefix/b"]);
    }

    [Fact]
    public async Task Queries_filter_on_strings_enums_dates_and_guids()
    {
        var match = Sample("ConfQueried/match");
        var other = Sample("ConfQueried/other");
        other.Name = "Other";
        other.Status = ConfStatus.Archived;
        other.Utc = match.Utc.AddDays(-10);
        other.Key = Guid.Parse("00000000-0000-0000-0000-000000000001");
        await StoreAsync(match, other);

        using var session = Store.OpenSession();
        var byName = await session.ToListAsync(Settled(session.Query<ConfDoc>()).Where(d => d.Name == "Sample"));
        byName.Select(d => d.Id).Should().BeEquivalentTo(["ConfQueried/match"]);

        var byStatus = await session.ToListAsync(Settled(session.Query<ConfDoc>()).Where(d => d.Status == ConfStatus.Archived));
        byStatus.Select(d => d.Id).Should().BeEquivalentTo(["ConfQueried/other"]);

        var since = match.Utc.AddDays(-1);
        var byDate = await session.ToListAsync(Settled(session.Query<ConfDoc>()).Where(d => d.Utc >= since));
        byDate.Select(d => d.Id).Should().BeEquivalentTo(["ConfQueried/match"]);

        var key = match.Key;
        var byKey = await session.ToListAsync(Settled(session.Query<ConfDoc>()).Where(d => d.Key == key));
        byKey.Select(d => d.Id).Should().BeEquivalentTo(["ConfQueried/match"]);

        (await session.CountAsync(Settled(session.Query<ConfDoc>()))).Should().Be(2);
    }

    [Fact]
    public async Task Queried_documents_are_tracked_by_the_session()
    {
        await StoreAsync(Sample("ConfDocs/queried"));

        using var session = Store.OpenSession();
        var fromQuery = (await session.ToListAsync(Settled(session.Query<ConfDoc>()).Where(d => d.Id == "ConfDocs/queried"))).Single();
        var loaded = await session.LoadAsync<ConfDoc>("ConfDocs/queried");

        ReferenceEquals(fromQuery, loaded).Should().BeTrue();
    }

    [Fact]
    public async Task The_request_budget_is_enforced_and_can_be_lifted()
    {
        using var session = Store.OpenSession();
        var act = async () =>
        {
            for (var i = 0; i < 40; i++)
                await session.ExistsAsync($"ConfDocs/budget-{i}");
        };
        await act.Should().ThrowAsync<InvalidOperationException>();

        using var lifted = Store.OpenSession();
        using (lifted.IgnoreMaxRequests())
        {
            for (var i = 0; i < 40; i++)
                await lifted.ExistsAsync($"ConfDocs/budget-{i}");
        }
    }
}

/// <summary>The suite against RavenDB, an embedded server per class.</summary>
public sealed class RavenSparkStoreConformanceTests : SparkStoreConformanceTests
{
    private readonly Driver driver = new();

    protected override async Task<ISparkStore> CreateStoreAsync()
    {
        await driver.InitializeAsync();
        return new RavenSparkStore(driver.DocumentStore);
    }

    protected override IQueryable<T> Settled<T>(IQueryable<T> query)
        => ((IRavenQueryable<T>)query).Customize(c => c.WaitForNonStaleResults(TimeSpan.FromSeconds(30)));

    public override async Task DisposeAsync() => await driver.DisposeAsync();

    private sealed class Driver : SparkTestDriver
    {
        public IDocumentStore DocumentStore => Store;
    }
}

/// <summary>The suite against the in-memory store.</summary>
public sealed class InMemorySparkStoreConformanceTests : SparkStoreConformanceTests
{
    protected override Task<ISparkStore> CreateStoreAsync()
        => Task.FromResult<ISparkStore>(new InMemorySparkStore(new InMemoryDatabase()));
}
