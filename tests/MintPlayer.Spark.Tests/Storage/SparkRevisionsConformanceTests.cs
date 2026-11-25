using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Tests.Storage;

/// <summary>
/// <see cref="ISparkRevisions"/> (#536 S8, PRD O12) as every provider keeps it: one suite, run once per provider. RavenDB
/// keeps its own revisions; the relational providers write <c>spark.Revisions</c> in the writing transaction; the
/// in-memory store keeps them beside its documents. Every write path leaves one: the unit of work, the set-based update
/// and delete.
/// </summary>
public abstract class SparkRevisionsConformanceTests : IAsyncLifetime
{
    public class RevDoc
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public class RevOther
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private (ISparkStore Store, ISparkRevisions Revisions)? subject;

    /// <summary>The provider's store; a provider with no server configured skips the test here.</summary>
    protected ISparkStore Store => subject?.Store ?? throw new SkipException("No server is configured for this provider.");

    protected ISparkRevisions Revisions => subject?.Revisions ?? throw new SkipException("No server is configured for this provider.");

    /// <summary>The store and its revisions, or <see langword="null"/> when the provider's server is not configured.</summary>
    protected abstract Task<(ISparkStore Store, ISparkRevisions Revisions)?> CreateAsync();

    public virtual async Task InitializeAsync() => subject = await CreateAsync();

    public virtual Task DisposeAsync() => Task.CompletedTask;

    private static readonly SparkRevisionSettings Keep = new(true, null, null, false);

    private Task<IReadOnlyList<string>> ConfigureAsync(SparkRevisionSettings settings)
        => Revisions.ConfigureAsync([new SparkRevisionCollection(Store.GetCollectionName(typeof(RevDoc)), "RevDocs (RevDoc)", settings)]);

    private async Task<string> StoreAsync(string id, string name, int count = 0)
    {
        using var session = Store.OpenSession();
        var doc = await session.LoadAsync<RevDoc>(id);
        if (doc is null)
        {
            doc = new RevDoc { Id = id };
            await session.StoreAsync(doc);
        }
        doc.Name = name;
        doc.Count = count;
        await session.SaveChangesAsync();
        return session.GetVersion(doc)!;
    }

    private async Task DeleteAsync(string id)
    {
        using var session = Store.OpenSession();
        session.Delete(typeof(RevDoc), id);
        await session.SaveChangesAsync();
    }

    private Task<IReadOnlyList<SparkRevisionInfo>> ListAsync(string id) => Revisions.ListAsync(typeof(RevDoc), id, 0, 50);

    [SkippableFact]
    public async Task A_collection_not_configured_keeps_no_revisions()
    {
        await StoreAsync("revdocs/plain", "one");
        await StoreAsync("revdocs/plain", "two");

        (await ListAsync("revdocs/plain")).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task Configuring_twice_with_the_same_settings_changes_nothing()
    {
        (await ConfigureAsync(Keep)).Should().Equal(["RevDocs (RevDoc)"]);
        (await ConfigureAsync(Keep)).Should().BeEmpty();
        (await ConfigureAsync(Keep with { MinimumRevisionsToKeep = 5 })).Should().Equal(["RevDocs (RevDoc)"]);
    }

    [SkippableFact]
    public async Task Every_save_leaves_a_revision_newest_first_read_back_by_version()
    {
        await ConfigureAsync(Keep);
        var v1 = await StoreAsync("revdocs/a", "one", 1);
        var v2 = await StoreAsync("revdocs/a", "two", 2);
        var v3 = await StoreAsync("revdocs/a", "three", 3);

        var list = await ListAsync("revdocs/a");

        list.Select(r => r.Version).Should().Equal([v3, v2, v1], "the newest first, each at the version the save gave the document");
        list.Should().OnlyContain(r => !r.IsDelete && r.LastModified != null);

        var read = await Revisions.GetAsync(typeof(RevDoc), "revdocs/a", [v1, v3]);
        read.Should().HaveCount(2);
        read[v1].Should().BeOfType<RevDoc>().Which.Name.Should().Be("one");
        ((RevDoc)read[v3]).Count.Should().Be(3);
    }

    [SkippableFact]
    public async Task A_version_of_another_document_or_type_is_not_returned()
    {
        await ConfigureAsync(Keep);
        await StoreAsync("revdocs/mine", "mine");
        // A version mine never had: relational versions count per document, so theirs' first would also be mine's.
        await StoreAsync("revdocs/theirs", "theirs", 1);
        await StoreAsync("revdocs/theirs", "theirs", 2);
        var theirs = await StoreAsync("revdocs/theirs", "theirs", 3);

        (await Revisions.GetAsync(typeof(RevDoc), "revdocs/mine", [theirs])).Should().BeEmpty("a version is scoped to its document");
        (await Revisions.GetAsync(typeof(RevOther), "revdocs/theirs", [theirs])).Should().BeEmpty("and to its type's collection");
        (await Revisions.GetAsync(typeof(RevDoc), "revdocs/theirs", ["not-a-version"])).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task A_delete_leaves_a_delete_revision_without_content()
    {
        await ConfigureAsync(Keep);
        var v1 = await StoreAsync("revdocs/d", "one");
        await DeleteAsync("revdocs/d");

        var list = await ListAsync("revdocs/d");

        list.Should().HaveCount(2);
        list[0].IsDelete.Should().BeTrue("the delete is the newest");
        list[1].Version.Should().Be(v1);
        (await Revisions.GetAsync(typeof(RevDoc), "revdocs/d", [list[0].Version])).Should().BeEmpty("a delete revision has no content");
    }

    [SkippableFact]
    public async Task Purge_on_delete_takes_every_revision_with_the_document()
    {
        await ConfigureAsync(Keep with { PurgeOnDelete = true });
        await StoreAsync("revdocs/p", "one");
        await StoreAsync("revdocs/p", "two");
        await DeleteAsync("revdocs/p");

        (await ListAsync("revdocs/p")).Should().BeEmpty();
    }

    [SkippableFact]
    public async Task A_count_limit_keeps_the_newest()
    {
        await ConfigureAsync(Keep with { MinimumRevisionsToKeep = 2 });
        await StoreAsync("revdocs/n", "one");
        var v2 = await StoreAsync("revdocs/n", "two");
        var v3 = await StoreAsync("revdocs/n", "three");

        (await ListAsync("revdocs/n")).Select(r => r.Version).Should().Equal([v3, v2]);
    }

    [SkippableFact]
    public async Task A_page_skips_and_takes()
    {
        await ConfigureAsync(Keep);
        await StoreAsync("revdocs/pg", "one");
        var v2 = await StoreAsync("revdocs/pg", "two");
        await StoreAsync("revdocs/pg", "three");

        (await Revisions.ListAsync(typeof(RevDoc), "revdocs/pg", 1, 1)).Select(r => r.Version).Should().Equal([v2]);
    }

    [SkippableFact]
    public async Task A_set_based_update_leaves_a_revision_of_each_row_it_changed()
    {
        await ConfigureAsync(Keep);
        await StoreAsync("revdocs/u1", "hit");
        await StoreAsync("revdocs/u2", "miss");

        using (var session = Store.OpenSession())
            (await session.UpdateWhereAsync<RevDoc>(d => d.Name == "hit", s => s.Set(d => d.Count, 42))).Should().Be(1);

        var list = await ListAsync("revdocs/u1");
        list.Should().HaveCount(2);
        var newest = await Revisions.GetAsync(typeof(RevDoc), "revdocs/u1", [list[0].Version]);
        ((RevDoc)newest[list[0].Version]).Count.Should().Be(42, "the revision holds the row after the update");
        (await ListAsync("revdocs/u2")).Should().HaveCount(1, "an unmatched row is untouched");
    }

    [SkippableFact]
    public async Task A_set_based_delete_leaves_a_delete_revision()
    {
        await ConfigureAsync(Keep);
        await StoreAsync("revdocs/x1", "gone");

        using (var session = Store.OpenSession())
            (await session.DeleteWhereAsync<RevDoc>(d => d.Name == "gone")).Should().Be(1);

        var list = await ListAsync("revdocs/x1");
        list.Should().HaveCount(2);
        list[0].IsDelete.Should().BeTrue();
    }

    [SkippableFact]
    public async Task Deleting_a_documents_revisions_removes_them_all()
    {
        await ConfigureAsync(Keep);
        await StoreAsync("revdocs/r", "one");
        await StoreAsync("revdocs/r", "two");
        await DeleteAsync("revdocs/r");
        await Revisions.EnsureCanDeleteAsync();

        (await Revisions.DeleteAsync(typeof(RevDoc), "revdocs/r")).Should().Be(3);
        (await ListAsync("revdocs/r")).Should().BeEmpty();
    }
}
