using MintPlayer.Spark.Testing;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Tests.Spikes.Issue467;

/// <summary>
/// D32(2) spike: which raw session deletes raise <c>OnBeforeDelete</c>? The soft-delete guard can only
/// refuse what the event reports.
/// </summary>
public class Issue467RawDeleteEventSpikeTests : SparkTestDriver
{
    private class Widget
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private async Task<List<string>> DeleteAndRecordAsync(Func<IAsyncDocumentSession, Task> delete)
    {
        using (var seed = Store.OpenAsyncSession())
        {
            await seed.StoreAsync(new Widget { Name = "w" }, "widgets/1");
            await seed.SaveChangesAsync();
        }

        var seen = new List<string>();
        using var session = Store.OpenAsyncSession();
        session.Advanced.OnBeforeDelete += (_, e) => seen.Add($"delete:{e.DocumentId}");
        await delete(session);
        await session.SaveChangesAsync();
        return seen;
    }

    [Fact]
    public async Task Delete_of_a_tracked_entity()
    {
        var seen = await DeleteAndRecordAsync(async s => s.Delete(await s.LoadAsync<Widget>("widgets/1")));
        seen.Should().Equal("delete:widgets/1");
    }

    [Fact]
    public async Task Delete_by_id_of_an_untracked_document()
    {
        var seen = await DeleteAndRecordAsync(s => { s.Delete("widgets/1"); return Task.CompletedTask; });
        seen.Should().Equal("delete:widgets/1");
    }

    [Fact]
    public async Task Delete_by_id_of_a_tracked_document()
    {
        var seen = await DeleteAndRecordAsync(async s => { await s.LoadAsync<Widget>("widgets/1"); s.Delete("widgets/1"); });
        seen.Should().Equal("delete:widgets/1");
    }

    [Fact]
    public async Task Delete_by_id_with_a_change_vector()
    {
        var seen = await DeleteAndRecordAsync(async s =>
        {
            var w = await s.LoadAsync<Widget>("widgets/1");
            var cv = s.Advanced.GetChangeVectorFor(w);
            s.Advanced.Evict(w);
            s.Delete("widgets/1", cv);
        });
        seen.Should().Equal("delete:widgets/1");
    }
}
