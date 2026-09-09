using CodeCoverage.Entities;
using CodeCoverage.Migrations;
using Raven.Client.Documents;
using Xunit;

namespace CodeCoverage.Tests.Model;

/// <summary>
/// Runs the row-key backfill against a real RavenDB, because its patch is an RQL string and
/// nothing else in the build can check it.
/// </summary>
/// <remarks>
/// The migration deliberately names its collection and property as strings rather than symbols, so
/// that a later model change cannot break an already-applied migration — which also means a typo in
/// either is invisible to the compiler. This test is the thing that catches it.
/// <para>
/// It also pins the two properties the migration relies on and neither the compiler nor a reading
/// can establish: that the derived key is unique per row, and that a replay changes nothing.
/// </para>
/// </remarks>
public class BuildSessionKeyBackfillTests : CoverageRavenTest
{
    /// <summary>
    /// Writes the pre-migration shape: a Sessions array whose elements have no <c>Id</c> at all.
    /// It cannot be written through the entity, because the field initializer would supply one —
    /// which is the whole reason a keyless row is undetectable in memory.
    /// </summary>
    private static async Task SeedKeylessAsync(IDocumentStore store, string id, params string[] sessionIds)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(new Build { Sessions = [.. sessionIds.Select(s => new BuildSession { SessionId = s })] }, id);
        await session.SaveChangesAsync();

        using var patch = store.OpenAsyncSession();
        var operation = await store.Operations.SendAsync(
            new Raven.Client.Documents.Operations.PatchByQueryOperation(
                new Raven.Client.Documents.Queries.IndexQuery
                {
                    Query = $$"""
                        from Builds as b where id() == '{{id}}' update {
                            for (var i = 0; i < b.Sessions.length; i++) { delete b.Sessions[i].Id; }
                        }
                        """,
                }));
        await operation.WaitForCompletionAsync(TimeSpan.FromMinutes(1));
    }

    private static async Task<string?[]> KeysOfAsync(IDocumentStore store, string id)
    {
        using var session = store.OpenAsyncSession();
        var build = await session.LoadAsync<Build>(id);
        return [.. build.Sessions.Select(s => s.Id)];
    }

    private static Task RunAsync(IDocumentStore store)
        => new M_202609091200_BackfillBuildSessionKeys(store).UpAsync(CancellationToken.None);

    [Fact]
    public async Task Every_keyless_row_gets_a_key()
    {
        using var store = GetDocumentStore();
        await SeedKeylessAsync(store, "Builds/1-A", "s1", "s2", "s3");

        await RunAsync(store);

        var keys = await KeysOfAsync(store, "Builds/1-A");
        keys.Should().AllSatisfy(k => k.Should().NotBeNullOrEmpty());
        keys.Distinct().Should().HaveCount(3, "a row key must be unique within its collection");
    }

    /// <summary>
    /// Across documents too — the derived key mixes in the document id precisely for this.
    /// </summary>
    [Fact]
    public async Task Keys_do_not_collide_across_documents()
    {
        using var store = GetDocumentStore();
        await SeedKeylessAsync(store, "Builds/1-A", "s1", "s2");
        await SeedKeylessAsync(store, "Builds/2-A", "t1", "t2");

        await RunAsync(store);

        var all = (await KeysOfAsync(store, "Builds/1-A")).Concat(await KeysOfAsync(store, "Builds/2-A")).ToArray();
        all.Distinct().Should().HaveCount(4);
    }

    /// <summary>
    /// A key that is already there is meaningful — it may be the one the running application
    /// minted — so the backfill must never overwrite it.
    /// </summary>
    [Fact]
    public async Task An_existing_key_is_preserved()
    {
        using var store = GetDocumentStore();
        await SeedKeylessAsync(store, "Builds/1-A", "s1", "s2");

        using (var session = store.OpenAsyncSession())
        {
            var build = await session.LoadAsync<Build>("Builds/1-A");
            build.Sessions[1].Id = "keep-me";
            await session.SaveChangesAsync();
        }

        await RunAsync(store);

        (await KeysOfAsync(store, "Builds/1-A"))[1].Should().Be("keep-me");
    }

    /// <summary>
    /// Replaying it must be a no-op, since the runner retries a migration whose marker was never
    /// written — and because a patch that rewrote keys on every start would churn identity forever.
    /// </summary>
    [Fact]
    public async Task Replaying_it_changes_nothing()
    {
        using var store = GetDocumentStore();
        await SeedKeylessAsync(store, "Builds/1-A", "s1", "s2");

        await RunAsync(store);
        var first = await KeysOfAsync(store, "Builds/1-A");

        await RunAsync(store);
        var second = await KeysOfAsync(store, "Builds/1-A");

        second.Should().Equal(first);
    }

    /// <summary>
    /// A document whose Sessions array is absent or empty must not fault the patch — old documents
    /// predate the property entirely.
    /// </summary>
    [Fact]
    public async Task A_document_with_no_rows_is_untouched()
    {
        using var store = GetDocumentStore();

        using (var session = store.OpenAsyncSession())
        {
            await session.StoreAsync(new Build { Sessions = [] }, "Builds/1-A");
            await session.SaveChangesAsync();
        }

        var act = () => RunAsync(store);

        await act.Should().NotThrowAsync();
        (await KeysOfAsync(store, "Builds/1-A")).Should().BeEmpty();
    }
}
