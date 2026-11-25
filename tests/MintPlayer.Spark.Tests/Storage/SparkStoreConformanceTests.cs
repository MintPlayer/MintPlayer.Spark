using MintPlayer.Spark.Abstractions.Storage;

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
        public DateTimeOffset At { get; set; }
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

    private ISparkStore? store;

    /// <summary>The provider's store; a provider with no server configured skips the test here.</summary>
    protected ISparkStore Store => store ?? throw new SkipException("No server is configured for this provider.");

    /// <summary>The store under test, or <see langword="null"/> when its server is not configured (the case skips).</summary>
    protected abstract Task<ISparkStore?> CreateStoreAsync();

    /// <summary>
    /// Ids compare case-insensitively on every provider (#536 D7); the relational providers hand them back lowercase,
    /// RavenDB and InMemory as they were written. Assertions compare this form.
    /// </summary>
    protected static string? Canon(string? id) => id?.ToLowerInvariant();

    /// <summary>
    /// Whether a <see cref="DateTime"/> reads back with the <see cref="DateTime.Kind"/> it was written with. RavenDB and
    /// InMemory keep it in the document; a SQL column keeps the ticks only (#536, open question O18).
    /// </summary>
    protected virtual bool KeepsDateTimeKind => true;

    /// <summary>A query whose reads see every committed write: RavenDB's indexes are eventually consistent.</summary>
    protected virtual IQueryable<T> Settled<T>(IQueryable<T> query) => query;

    public virtual async Task InitializeAsync() => store = await CreateStoreAsync();

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

    [SkippableFact]
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
        loaded.Unspecified.Should().Be(expected.Unspecified);
        if (KeepsDateTimeKind)
        {
            loaded.Utc.Kind.Should().Be(DateTimeKind.Utc);
            loaded.Unspecified.Kind.Should().Be(DateTimeKind.Unspecified);
        }
        loaded.Day.Should().Be(expected.Day);
        loaded.Key.Should().Be(expected.Key);
        loaded.Status.Should().Be(ConfStatus.Published);
        loaded.MaybeStatus.HasValue.Should().BeFalse();
        loaded.Nothing.Should().BeNull();
        loaded.Address!.Street.Should().Be("Main");
        loaded.Address.Number.Should().Be(7);
        loaded.Tags.Should().BeEquivalentTo(["a", "b"]);
    }

    [SkippableFact]
    public virtual async Task An_absent_field_reads_as_the_clr_default()
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

    [SkippableFact]
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

    [SkippableFact]
    public async Task A_session_returns_one_instance_per_id()
    {
        await StoreAsync(Sample("ConfDocs/identity"));

        using var session = Store.OpenSession();
        var first = await session.LoadAsync<ConfDoc>("ConfDocs/identity");
        var second = await session.LoadAsync<ConfDoc>("conFDocs/IDENTITY");

        ReferenceEquals(first, second).Should().BeTrue("ids are case-insensitive and a session tracks one instance");
        session.IsLoaded("ConfDocs/identity").Should().BeTrue();
    }

    /// <summary>
    /// A document stored but not saved yet is what the session loads for its id, and one deleted in the session loads as
    /// null (#536 D47: the identity provider's seed stores a resource, then loads it again for the next scope).
    /// </summary>
    [SkippableFact]
    public async Task A_session_loads_what_it_stored_or_deleted_before_saving()
    {
        await StoreAsync(Sample("ConfDocs/doomed"));

        using var session = Store.OpenSession();
        var stored = Sample("ConfDocs/pending");
        await session.StoreAsync(stored);
        ReferenceEquals(await session.LoadAsync<ConfDoc>("CONFDOCS/pending"), stored).Should().BeTrue("the session holds the stored instance");
        (await session.LoadAsync<ConfDoc>(["confdocs/pending"]))["confdocs/pending"].Should().BeSameAs(stored);

        session.Delete(typeof(ConfDoc), "ConfDocs/doomed");
        (await session.LoadAsync<ConfDoc>("ConfDocs/doomed")).Should().BeNull("a document deleted in the session is gone for it");

        await session.SaveChangesAsync();
    }

    [SkippableFact]
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

    [SkippableFact]
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

        stored.Select(Canon).Should().BeEquivalentTo(["confdocs/hook-a", "confdocs/hook-c"]);
    }

    [SkippableFact]
    public async Task Delete_removes_the_document()
    {
        await StoreAsync(Sample("ConfDocs/delete"));

        using (var session = Store.OpenSession())
        {
            session.Delete(typeof(ConfDoc), "ConfDocs/delete");
            await session.SaveChangesAsync();
        }

        using var verify = Store.OpenSession();
        (await verify.ExistsAsync(typeof(ConfDoc), "ConfDocs/delete")).Should().BeFalse();
        (await verify.LoadAsync<ConfDoc>("ConfDocs/delete")).Should().BeNull();
    }

    [SkippableFact]
    public async Task Delete_removes_a_loaded_entity()
    {
        await StoreAsync(Sample("ConfDocs/delete-loaded"));

        using (var session = Store.OpenSession())
        {
            var loaded = await session.LoadAsync<ConfDoc>("ConfDocs/delete-loaded");
            session.Delete(loaded!);
            await session.SaveChangesAsync();
        }

        using var verify = Store.OpenSession();
        (await verify.LoadAsync<ConfDoc>("ConfDocs/delete-loaded")).Should().BeNull();
    }

    [SkippableFact]
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
        (await verify.ExistsAsync(typeof(ConfDoc), "ConfDocs/bystander")).Should().BeFalse("a commit is all or nothing");
    }

    /// <summary>
    /// #536 D37: with <see cref="ISparkSession.OptimisticConcurrency"/> every write checks, without an expected version
    /// passed: a loaded document must still be at the version it was read at, and a new one must not exist yet.
    /// </summary>
    [SkippableFact]
    public async Task OptimisticConcurrency_checks_every_loaded_and_new_document()
    {
        await StoreAsync(Sample("ConfDocs/occ-loaded"));
        await StoreAsync(Sample("ConfDocs/occ-taken"));

        using var reader = Store.OpenSession();
        reader.OptimisticConcurrency = true;
        var loaded = (await reader.LoadAsync<ConfDoc>("ConfDocs/occ-loaded"))!;

        using (var other = Store.OpenSession())
        {
            (await other.LoadAsync<ConfDoc>("ConfDocs/occ-loaded"))!.Count = 7;
            await other.SaveChangesAsync();
        }

        loaded.Count = 8;
        var stale = () => reader.SaveChangesAsync();
        await stale.Should().ThrowAsync<SparkStoreConcurrencyException>();

        using var writer = Store.OpenSession();
        writer.OptimisticConcurrency = true;
        await writer.StoreAsync(Sample("ConfDocs/occ-taken"));
        var taken = () => writer.SaveChangesAsync();
        await taken.Should().ThrowAsync<SparkStoreConcurrencyException>();

        using var fresh = Store.OpenSession();
        fresh.OptimisticConcurrency = true;
        await fresh.StoreAsync(Sample("ConfDocs/occ-new"));
        await fresh.SaveChangesAsync();
        (await fresh.LoadAsync<ConfDoc>("ConfDocs/occ-new"))!.Count++;
        await fresh.SaveChangesAsync(); // the session's own write moved the version it checks against
    }

    /// <summary>
    /// #536 D38: a query that waits for its indexes sees a document committed just before it, even when that query is
    /// the first of its shape (RavenDB creates its auto index on the spot, which a store-level wait cannot cover).
    /// </summary>
    [SkippableFact]
    public async Task A_waited_query_sees_a_document_committed_just_before()
    {
        var name = "waited-" + Guid.NewGuid().ToString("N");
        var doc = Sample("ConfDocs/waited");
        doc.Name = name;
        doc.Count = 41;
        await StoreAsync(doc);

        using var session = Store.OpenSession();
        var found = await session.ToListAsync(session.Query<ConfDoc>(TimeSpan.FromSeconds(15)).Where(d => d.Name == name && d.Count == 41));
        found.Should().ContainSingle().Which.Name.Should().Be(name);
    }

    [SkippableFact]
    public async Task Without_OptimisticConcurrency_a_new_document_replaces_a_stored_one()
    {
        await StoreAsync(Sample("ConfDocs/occ-put"));
        using (var session = Store.OpenSession())
        {
            var replacement = Sample("ConfDocs/occ-put");
            replacement.Count = 42;
            await session.StoreAsync(replacement);
            await session.SaveChangesAsync();
        }
        using var verify = Store.OpenSession();
        (await verify.LoadAsync<ConfDoc>("ConfDocs/occ-put"))!.Count.Should().Be(42);
    }

    [SkippableFact]
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
            (await session.GetVersionsAsync(typeof(ConfDoc), ["ConfDocs/versioned"]))["ConfDocs/versioned"].Should().NotBe(staleVersion);
        }

        using var writer = Store.OpenSession();
        await writer.StoreAsync(Sample("ConfDocs/versioned"), staleVersion, "ConfDocs/versioned");
        var act = () => writer.SaveChangesAsync();
        await act.Should().ThrowAsync<SparkStoreConcurrencyException>();
    }

    [SkippableFact]
    public async Task LoadStartingWith_pages_in_id_order()
    {
        var c = Sample("ConfPrefix/c");
        var a = Sample("ConfPrefix/a");
        var b = Sample("ConfPrefix/b");
        await StoreAsync(c, a, b, Sample("ConfOther/a"));

        using var session = Store.OpenSession();
        var all = await session.LoadStartingWithAsync<ConfDoc>("ConfPrefix/");
        all.Select(d => Canon(d.Id)).Should().BeEquivalentTo(["confprefix/a", "confprefix/b", "confprefix/c"],
            o => o.WithStrictOrdering());

        var page = await session.LoadStartingWithAsync<ConfDoc>("ConfPrefix/", start: 1, pageSize: 1);
        page.Select(d => Canon(d.Id)).Should().BeEquivalentTo(["confprefix/b"]);

        // Keyset paging: the ids after the last one seen, whatever its case.
        var after = await session.LoadStartingWithAsync<ConfDoc>("ConfPrefix/", startAfter: "CONFPREFIX/A");
        after.Select(d => Canon(d.Id)).Should().BeEquivalentTo(["confprefix/b", "confprefix/c"], o => o.WithStrictOrdering());
        (await session.LoadStartingWithAsync<ConfDoc>("ConfPrefix/", startAfter: "ConfPrefix/c")).Should().BeEmpty();
    }

    [SkippableFact]
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
        byName.Select(d => Canon(d.Id)).Should().BeEquivalentTo(["confqueried/match"]);

        var byStatus = await session.ToListAsync(Settled(session.Query<ConfDoc>()).Where(d => d.Status == ConfStatus.Archived));
        byStatus.Select(d => Canon(d.Id)).Should().BeEquivalentTo(["confqueried/other"]);

        var since = match.Utc.AddDays(-1);
        var byDate = await session.ToListAsync(Settled(session.Query<ConfDoc>()).Where(d => d.Utc >= since));
        byDate.Select(d => Canon(d.Id)).Should().BeEquivalentTo(["confqueried/match"]);

        var key = match.Key;
        var byKey = await session.ToListAsync(Settled(session.Query<ConfDoc>()).Where(d => d.Key == key));
        byKey.Select(d => Canon(d.Id)).Should().BeEquivalentTo(["confqueried/match"]);

        (await session.CountAsync(Settled(session.Query<ConfDoc>()))).Should().Be(2);
    }

    [SkippableFact]
    public async Task Queried_documents_are_tracked_by_the_session()
    {
        await StoreAsync(Sample("ConfDocs/queried"));

        using var session = Store.OpenSession();
        var fromQuery = (await session.ToListAsync(Settled(session.Query<ConfDoc>()).Where(d => d.Id == "ConfDocs/queried"))).Single();
        var loaded = await session.LoadAsync<ConfDoc>("ConfDocs/queried");

        ReferenceEquals(fromQuery, loaded).Should().BeTrue();
    }

    [SkippableFact]
    public async Task An_expiry_is_kept_by_later_writes_until_cleared()
    {
        var expires = new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero);
        using (var session = Store.OpenSession())
        {
            var doc = Sample("ConfDocs/expiring");
            await session.StoreAsync(doc, doc.Id!);
            session.SetExpiry(doc, expires);
            await session.SaveChangesAsync();
        }

        using (var session = Store.OpenSession())
        {
            var doc = (await session.LoadAsync<ConfDoc>("ConfDocs/expiring"))!;
            session.GetExpiry(doc).Should().Be(expires);
            doc.Name = "Renamed";
            await session.SaveChangesAsync();
        }

        using (var session = Store.OpenSession())
        {
            var doc = (await session.LoadAsync<ConfDoc>("ConfDocs/expiring"))!;
            doc.Name.Should().Be("Renamed");
            session.GetExpiry(doc).Should().Be(expires, "a write that does not touch the expiry keeps it");
            session.SetExpiry(doc, null);
            await session.SaveChangesAsync();
        }

        using (var session = Store.OpenSession())
        {
            var doc = (await session.LoadAsync<ConfDoc>("ConfDocs/expiring"))!;
            session.GetExpiry(doc).HasValue.Should().BeFalse();
        }
    }

    [SkippableFact]
    public async Task Setting_only_the_expiry_is_a_write()
    {
        await StoreAsync(Sample("ConfDocs/expiry-only"));
        var expires = new DateTimeOffset(2032, 1, 2, 3, 4, 5, TimeSpan.Zero);

        using (var session = Store.OpenSession())
        {
            var doc = (await session.LoadAsync<ConfDoc>("ConfDocs/expiry-only"))!;
            session.SetExpiry(doc, expires.ToOffset(TimeSpan.FromHours(2)));
            await session.SaveChangesAsync();
        }

        using var read = Store.OpenSession();
        read.GetExpiry((await read.LoadAsync<ConfDoc>("ConfDocs/expiry-only"))!).Should().Be(expires);
    }

    [SkippableFact]
    public void An_expiry_needs_an_entity_the_session_tracks()
    {
        using var session = Store.OpenSession();
        var act = () => session.SetExpiry(Sample("ConfDocs/untracked"), DateTimeOffset.UtcNow);
        act.Should().Throw<InvalidOperationException>();
    }

    [SkippableFact]
    public async Task A_stream_yields_every_match_with_its_id_and_version_untracked()
    {
        var small = Sample("ConfStreamed/small");
        small.Count = 1;
        await StoreAsync(Sample("ConfStreamed/a"), Sample("ConfStreamed/b"), small);

        // RavenDB refuses to wait for indexing on a stream: the query settles first, in a session of its own.
        using (var settle = Store.OpenSession())
            await settle.ToListAsync(Settled(settle.Query<ConfDoc>()).Where(d => d.Count > 10));

        using var session = Store.OpenSession();
        var streamed = new List<SparkStreamResult<ConfDoc>>();
        await foreach (var row in session.StreamAsync(session.Query<ConfDoc>().Where(d => d.Count > 10)))
            streamed.Add(row);

        streamed.Select(r => Canon(r.Id)).Should().BeEquivalentTo(["confstreamed/a", "confstreamed/b"]);
        streamed.Select(r => r.Document.Name).Distinct().Should().Equal(["Sample"]);
        streamed.Where(r => string.IsNullOrEmpty(r.Version)).Should().BeEmpty();

        foreach (var row in streamed)
        {
            session.IsLoaded(row.Id!).Should().BeFalse("a streamed row is not tracked");
            row.Document.Name = "Changed";
        }
        await session.SaveChangesAsync();

        using var read = Store.OpenSession();
        (await read.LoadAsync<ConfDoc>("ConfStreamed/a"))!.Name.Should().Be("Sample", "changing a streamed row saves nothing");
    }

    [SkippableFact]
    public async Task A_reservation_is_claimed_with_the_save_and_refused_to_a_second_owner()
    {
        using (var session = Store.OpenSession())
        {
            await session.StoreAsync(Sample("ConfDocs/owner-a"), "ConfDocs/owner-a");
            session.Reserve("conf-unique/alpha", "ConfDocs/owner-a");
            await session.SaveChangesAsync();
        }

        using (var session = Store.OpenSession())
        {
            Canon(await session.GetReservationAsync("conf-unique/alpha")).Should().Be("confdocs/owner-a");

            // The owner reserving again changes nothing.
            session.Reserve("conf-unique/alpha", "ConfDocs/owner-a");
            await session.SaveChangesAsync();
        }

        using (var session = Store.OpenSession())
        {
            await session.StoreAsync(Sample("ConfDocs/owner-b"), "ConfDocs/owner-b");
            session.Reserve("conf-unique/alpha", "ConfDocs/owner-b");
            var act = () => session.SaveChangesAsync();
            var thrown = await act.Should().ThrowAsync<SparkReservationTakenException>();
            thrown.Which.Key.Should().Be("conf-unique/alpha");
        }

        using var read = Store.OpenSession();
        (await read.LoadAsync<ConfDoc>("ConfDocs/owner-b")).Should().BeNull("a refused reservation writes nothing");
        Canon(await read.GetReservationAsync("conf-unique/alpha")).Should().Be("confdocs/owner-a");
    }

    [SkippableFact]
    public async Task Only_the_owner_releases_a_reservation()
    {
        using (var session = Store.OpenSession())
        {
            await session.StoreAsync(Sample("ConfDocs/holder"), "ConfDocs/holder");
            session.Reserve("conf-unique/beta", "ConfDocs/holder");
            await session.SaveChangesAsync();
        }

        using (var session = Store.OpenSession())
        {
            session.Release("conf-unique/beta", "ConfDocs/someone-else");
            await session.SaveChangesAsync();
            Canon(await session.GetReservationAsync("conf-unique/beta")).Should().Be("confdocs/holder");
        }

        using (var session = Store.OpenSession())
        {
            session.Delete(typeof(ConfDoc), "ConfDocs/holder");
            session.Release("conf-unique/beta", "ConfDocs/holder");
            await session.SaveChangesAsync();
        }

        using var read = Store.OpenSession();
        (await read.GetReservationAsync("conf-unique/beta")).Should().BeNull();
    }

    [SkippableFact]
    public async Task A_reservation_whose_owner_is_gone_is_taken_over()
    {
        // Held for a document that was never stored: what a crash between the claim and the write leaves behind.
        using (var session = Store.OpenSession())
        {
            session.Reserve("conf-unique/gamma", "ConfDocs/never-stored");
            await session.SaveChangesAsync();
        }

        using (var session = Store.OpenSession())
        {
            await session.StoreAsync(Sample("ConfDocs/claimant"), "ConfDocs/claimant");
            session.Reserve("conf-unique/gamma", "ConfDocs/claimant");
            await session.SaveChangesAsync();
        }

        using var read = Store.OpenSession();
        Canon(await read.GetReservationAsync("conf-unique/gamma")).Should().Be("confdocs/claimant");
    }

    [SkippableFact]
    public async Task DeleteWhere_deletes_the_matching_rows_only()
    {
        var keep = Sample("ConfBulkDelete/keep");
        keep.Count = 1;
        await StoreAsync(Sample("ConfBulkDelete/a"), Sample("ConfBulkDelete/b"), keep);

        using (var session = Store.OpenSession())
            (await session.DeleteWhereAsync<ConfDoc>(d => d.Count > 10 && d.Name == "Sample")).Should().Be(2);

        using var read = Store.OpenSession();
        var left = await read.LoadAsync<ConfDoc>(["ConfBulkDelete/a", "ConfBulkDelete/b", "ConfBulkDelete/keep"]);
        left.Where(p => p.Value is not null).Select(p => p.Key).Should().BeEquivalentTo(["ConfBulkDelete/keep"]);
    }

    [SkippableFact]
    public async Task UpdateWhere_sets_members_on_the_matching_rows_and_moves_their_version()
    {
        var other = Sample("ConfBulkUpdate/other");
        other.Count = 1;
        await StoreAsync(Sample("ConfBulkUpdate/a"), other);
        string before;
        using (var session = Store.OpenSession())
            before = session.GetVersion((await session.LoadAsync<ConfDoc>("ConfBulkUpdate/a"))!)!;

        using (var session = Store.OpenSession())
        {
            var updated = await session.UpdateWhereAsync<ConfDoc>(d => d.Count > 10,
                set => set.Set(d => d.Name, "Bulk").Set(d => d.Status, ConfStatus.Archived).Set(d => d.Nothing, null));
            updated.Should().Be(1);
        }

        using var read = Store.OpenSession();
        var a = (await read.LoadAsync<ConfDoc>("ConfBulkUpdate/a"))!;
        a.Name.Should().Be("Bulk");
        a.Status.Should().Be(ConfStatus.Archived);
        a.Count.Should().Be(42, "members not set keep their value");
        read.GetVersion(a).Should().NotBe(before);
        (await read.LoadAsync<ConfDoc>("ConfBulkUpdate/other"))!.Name.Should().Be("Sample");
    }

    [SkippableFact]
    public async Task Concurrent_appends_all_land()
    {
        var doc = Sample("ConfDocs/appended");
        doc.Tags = ["first"];
        await StoreAsync(doc);

        // Two units of work that both read the document before either saves: a load-modify-save would lose one.
        using (var one = Store.OpenSession())
        using (var two = Store.OpenSession())
        {
            await one.LoadAsync<ConfDoc>("ConfDocs/appended");
            await two.LoadAsync<ConfDoc>("ConfDocs/appended");
            one.PatchAppend<ConfDoc, string>("ConfDocs/appended", d => d.Tags, "from-one");
            two.PatchAppend<ConfDoc, string>("ConfDocs/appended", d => d.Tags, "from-two");
            await one.SaveChangesAsync();
            await two.SaveChangesAsync();
        }

        using var read = Store.OpenSession();
        var stored = (await read.LoadAsync<ConfDoc>("ConfDocs/appended"))!;
        stored.Tags.Should().Equal(["first", "from-one", "from-two"]);
        stored.Name.Should().Be("Sample", "an append touches only its list");
    }

    [SkippableFact]
    public async Task The_request_budget_is_enforced_and_can_be_lifted()
    {
        // Loads, not ExistsAsync: RavenDB's ExistsAsync does not count against MaxNumberOfRequestsPerSession.
        using var session = Store.OpenSession();
        var act = async () =>
        {
            for (var i = 0; i < 40; i++)
                await session.LoadAsync<ConfDoc>($"ConfDocs/budget-{i}");
        };
        await act.Should().ThrowAsync<InvalidOperationException>();

        using var lifted = Store.OpenSession();
        using (lifted.IgnoreMaxRequests())
        {
            for (var i = 0; i < 40; i++)
                await lifted.LoadAsync<ConfDoc>($"ConfDocs/budget-{i}");
        }
    }

    // #536 M5: the cases the plan lists for every provider (§4 item 5).

    /// <summary>
    /// Removes <paramref name="field"/> from the stored document, as a document written before the member existed lacks
    /// it. On a provider whose rows always carry every member (a column, or JSON read back into the CLR type) an absent
    /// field is <c>null</c> by construction, so this writes the null.
    /// </summary>
    protected virtual async Task RemoveFieldAsync(string id, string field)
    {
        using var session = Store.OpenSession();
        var doc = (await session.LoadAsync<ConfDoc>(id))!;
        typeof(ConfDoc).GetProperty(field)!.SetValue(doc, null);
        await session.SaveChangesAsync();
    }

    /// <summary>O11 (D16 for nullable members): a field the document never wrote is <c>null</c> to every query shape.</summary>
    [SkippableFact]
    public async Task An_absent_nullable_field_is_null_to_queries_and_sorting()
    {
        var absent = Row("ConfAbsent/absent", 1, nothing: "removed", maybe: ConfStatus.Archived);
        await StoreAsync(absent, Row("ConfAbsent/null", 2), Row("ConfAbsent/set", 3, nothing: "set", maybe: ConfStatus.Published));
        await RemoveFieldAsync("ConfAbsent/absent", nameof(ConfDoc.Nothing));
        await RemoveFieldAsync("ConfAbsent/absent", nameof(ConfDoc.MaybeStatus));

        (await IdsAsync(q => q.Where(d => d.Nothing == null))).Should().BeEquivalentTo(["confabsent/absent", "confabsent/null"]);
        (await IdsAsync(q => q.Where(d => d.Nothing != null))).Should().BeEquivalentTo(["confabsent/set"]);
        (await IdsAsync(q => q.Where(d => d.MaybeStatus == null))).Should().BeEquivalentTo(["confabsent/absent", "confabsent/null"]);
        (await IdsAsync(q => q.Where(d => !(d.MaybeStatus == null)))).Should().BeEquivalentTo(["confabsent/set"]);
        (await IdsAsync(q => q.Where(d => d.MaybeStatus != ConfStatus.Published))).Should().BeEquivalentTo(["confabsent/absent", "confabsent/null"]);
        (await IdsAsync(q => q.Where(d => d.Count > 0 && d.Nothing == null))).Should().BeEquivalentTo(["confabsent/absent", "confabsent/null"]);

        var ascending = await IdsAsync(q => q.OrderBy(d => d.Nothing));
        // An absent field sorts with the nulls (D4).
        ascending.Take(2).Should().BeEquivalentTo(["confabsent/absent", "confabsent/null"]);
        ascending.Last().Should().Be("confabsent/set");

        using (var session = Store.OpenSession())
            (await session.DeleteWhereAsync<ConfDoc>(d => d.Nothing == null)).Should().Be(2, "a set-based write reads absent as null too");
        (await IdsAsync(q => q)).Should().BeEquivalentTo(["confabsent/set"]);
    }

    private async Task<List<string?>> IdsAsync(Func<IQueryable<ConfDoc>, IQueryable<ConfDoc>> shape)
    {
        using var session = Store.OpenSession();
        var rows = await session.ToListAsync(shape(Settled(session.Query<ConfDoc>())));
        return rows.Select(d => Canon(d.Id)).ToList();
    }

    private static ConfDoc Row(string id, int count, string name = "Sample", ConfStatus? maybe = null, string? nothing = null, params string[] tags)
    {
        var doc = Sample(id);
        doc.Count = count;
        doc.Name = name;
        doc.MaybeStatus = maybe;
        doc.Nothing = nothing;
        doc.Tags = [.. tags];
        return doc;
    }

    [SkippableFact]
    public async Task Ids_load_in_any_casing_from_a_fresh_session()
    {
        await StoreAsync(Sample("ConfDocs/Casing-1"));

        using var session = Store.OpenSession();
        var loaded = await session.LoadAsync<ConfDoc>("CONFDOCS/CASING-1");
        loaded.Should().NotBeNull("ids compare ignoring case on every provider (D7)");
        (await session.ExistsAsync(typeof(ConfDoc), "confdocs/casing-1")).Should().BeTrue();
        Canon(session.GetId(loaded!)).Should().Be("confdocs/casing-1");
    }

    [SkippableFact]
    public async Task LoadStartingWith_matches_wildcard_characters_literally()
    {
        await StoreAsync(Sample("ConfEscape/a_b"), Sample("ConfEscape/axb"), Sample("ConfEscape/a%c"), Sample("ConfEscape/a[d"));

        using var session = Store.OpenSession();
        (await session.LoadStartingWithAsync<ConfDoc>("ConfEscape/a_")).Select(d => Canon(d.Id))
            .Should().Equal(["confescape/a_b"], "an underscore is not a single-character wildcard");
        (await session.LoadStartingWithAsync<ConfDoc>("ConfEscape/a%")).Select(d => Canon(d.Id))
            .Should().Equal(["confescape/a%c"], "a percent sign is not a wildcard");
        (await session.LoadStartingWithAsync<ConfDoc>("ConfEscape/a[")).Select(d => Canon(d.Id))
            .Should().Equal(["confescape/a[d"], "a bracket opens no character class");
    }

    [SkippableFact]
    public async Task Paging_follows_the_order()
    {
        await StoreAsync(Row("ConfPaged/5", 5), Row("ConfPaged/1", 1), Row("ConfPaged/4", 4), Row("ConfPaged/2", 2), Row("ConfPaged/3", 3));

        (await IdsAsync(q => q.OrderBy(d => d.Count).Skip(1).Take(2)))
            .Should().Equal(["confpaged/2", "confpaged/3"]);
        (await IdsAsync(q => q.OrderByDescending(d => d.Count).Skip(3).Take(5)))
            .Should().Equal(["confpaged/2", "confpaged/1"]);
        (await IdsAsync(q => q.OrderBy(d => d.Name).ThenByDescending(d => d.Count).Take(1)))
            .Should().Equal(["confpaged/5"]);
    }

    /// <summary>D4: nulls sort first ascending and last descending, on every provider (PostgreSQL's default is the reverse).</summary>
    [SkippableFact]
    public async Task Nulls_sort_first_ascending_and_last_descending()
    {
        await StoreAsync(
            Row("ConfNulls/b", 1, nothing: "b", maybe: ConfStatus.Published),
            Row("ConfNulls/none", 2),
            Row("ConfNulls/a", 3, nothing: "a", maybe: ConfStatus.Draft));

        (await IdsAsync(q => q.OrderBy(d => d.Nothing))).Should().Equal(["confnulls/none", "confnulls/a", "confnulls/b"]);
        (await IdsAsync(q => q.OrderByDescending(d => d.Nothing))).Should().Equal(["confnulls/b", "confnulls/a", "confnulls/none"]);
        (await IdsAsync(q => q.OrderBy(d => d.MaybeStatus))).Should().Equal(["confnulls/none", "confnulls/a", "confnulls/b"]);
        (await IdsAsync(q => q.OrderByDescending(d => d.MaybeStatus))).Should().Equal(["confnulls/b", "confnulls/a", "confnulls/none"]);
    }

    /// <summary>D5: a <see cref="DateTimeOffset"/> compares and sorts by its instant, whatever its offset.</summary>
    [SkippableFact]
    public async Task DateTimeOffsets_compare_and_sort_by_their_instant()
    {
        var early = Sample("ConfInstant/early");
        early.At = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.FromHours(2));   // 08:00Z
        var late = Sample("ConfInstant/late");
        late.At = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);              // 09:00Z, an earlier wall clock
        await StoreAsync(late, early);

        using (var session = Store.OpenSession())
            (await session.LoadAsync<ConfDoc>("ConfInstant/early"))!.At.UtcDateTime.Should().Be(new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc));

        (await IdsAsync(q => q.OrderBy(d => d.At))).Should().Equal(["confinstant/early", "confinstant/late"]);
        var cut = new DateTimeOffset(2026, 6, 1, 8, 30, 0, TimeSpan.Zero);
        (await IdsAsync(q => q.Where(d => d.At > cut))).Should().Equal(["confinstant/late"]);
    }

    /// <summary>
    /// A wrong-rows guard: every query shape core and the feature libraries write, against rows that differ in exactly
    /// the member each shape reads, so a provider that drops or loosens a predicate returns a row it must not.
    /// </summary>
    [SkippableFact]
    public async Task Every_query_shape_returns_exactly_its_rows()
    {
        await StoreAsync(
            Row("ConfShapes/a", 1, "Alpha", ConfStatus.Published, "x", "red", "blue"),
            Row("ConfShapes/b", 2, "Beta", ConfStatus.Archived, null, "blue"),
            Row("ConfShapes/c", 3, "Gamma", null, "y"),
            Row("ConfShapes/d", 4, "Delta", ConfStatus.Draft, null, "red"));
        var keys = new[] { "Beta", "Gamma" };

        (await IdsAsync(q => q.Where(d => d.Count > 1 && d.Count < 4))).Should().BeEquivalentTo(["confshapes/b", "confshapes/c"]);
        (await IdsAsync(q => q.Where(d => d.Count <= 1 || d.Count >= 4))).Should().BeEquivalentTo(["confshapes/a", "confshapes/d"]);
        (await IdsAsync(q => q.Where(d => !(d.Count == 2)))).Should().BeEquivalentTo(["confshapes/a", "confshapes/c", "confshapes/d"]);
        (await IdsAsync(q => q.Where(d => d.Name != "Beta"))).Should().BeEquivalentTo(["confshapes/a", "confshapes/c", "confshapes/d"]);
        (await IdsAsync(q => q.Where(d => keys.Contains(d.Name)))).Should().BeEquivalentTo(["confshapes/b", "confshapes/c"]);
        (await IdsAsync(q => q.Where(d => d.Name.StartsWith("Al")))).Should().BeEquivalentTo(["confshapes/a"]);
        (await IdsAsync(q => q.Where(d => d.Tags.Contains("red")))).Should().BeEquivalentTo(["confshapes/a", "confshapes/d"]);
        (await IdsAsync(q => q.Where(d => d.Tags.Any()))).Should().BeEquivalentTo(["confshapes/a", "confshapes/b", "confshapes/d"]);
        (await IdsAsync(q => q.Where(d => d.Nothing == null))).Should().BeEquivalentTo(["confshapes/b", "confshapes/d"]);
        (await IdsAsync(q => q.Where(d => d.Nothing != null))).Should().BeEquivalentTo(["confshapes/a", "confshapes/c"]);
        (await IdsAsync(q => q.Where(d => d.MaybeStatus == null))).Should().BeEquivalentTo(["confshapes/c"]);
        (await IdsAsync(q => q.Where(d => d.MaybeStatus == ConfStatus.Archived))).Should().BeEquivalentTo(["confshapes/b"]);
        (await IdsAsync(q => q.Where(d => d.MaybeStatus != ConfStatus.Published || d.Count == 4)))
            .Should().BeEquivalentTo(["confshapes/b", "confshapes/c", "confshapes/d"]);
        (await IdsAsync(q => q.Where(d => d.Count >= 2).Where(d => d.Nothing == null))).Should().BeEquivalentTo(["confshapes/b", "confshapes/d"]);
    }
}
