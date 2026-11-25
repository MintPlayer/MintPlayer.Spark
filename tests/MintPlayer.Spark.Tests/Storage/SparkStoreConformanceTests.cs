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
}
