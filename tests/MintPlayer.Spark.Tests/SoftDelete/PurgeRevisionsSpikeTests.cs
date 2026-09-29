using System.Text.Json;
using MintPlayer.Spark.Testing;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations.Revisions;
using Xunit.Abstractions;

namespace MintPlayer.Spark.Tests.SoftDelete;

/// <summary>
/// #460 spike H1, the part M6 needs: does <c>DeleteRevisionsOperation</c> — what a SoftDelete
/// purge runs for GDPR — work on the licence production has (Community), and in which order must a
/// purge delete the document and its revisions so that nothing is left?
/// </summary>
/// <remarks>
/// The kept test is licence-agnostic: it prints the server's licence type and asserts the
/// behaviour. The PRD records the run with <c>RAVENDB_LICENSE</c> pointing at the Community licence
/// file (§4.1, H1).
/// </remarks>
public class PurgeRevisionsSpikeTests(ITestOutputHelper output) : SparkTestDriver
{
    private const string Collection = "H1SpikeDocs";

    [Fact]
    public async Task H1_DeleteRevisionsOperation_removes_every_revision_of_a_purged_document()
    {
        output.WriteLine($"licence: {await LicenceTypeAsync()}");

        await Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(new RevisionsConfiguration
        {
            Collections = new Dictionary<string, RevisionsCollectionConfiguration>
            {
                [Collection] = new() { Disabled = false },
            },
        }));

        // A: delete the document first, then its revisions (the order a purge takes).
        await WriteHistoryAsync("H1SpikeDocs/a");
        var aBefore = await RevisionCountAsync("H1SpikeDocs/a");
        await DeleteDocumentAsync("H1SpikeDocs/a");
        var aAfterDelete = await RevisionCountAsync("H1SpikeDocs/a");
        var aResult = await Store.Maintenance.SendAsync(new DeleteRevisionsOperation("H1SpikeDocs/a", removeForceCreatedRevisions: true));
        var aAfterPurge = await RevisionCountAsync("H1SpikeDocs/a");
        output.WriteLine($"A delete-then-revisions: before={aBefore} afterDelete={aAfterDelete} deleted={aResult.TotalDeletes} after={aAfterPurge}");

        // B: revisions first, then the document.
        await WriteHistoryAsync("H1SpikeDocs/b");
        var bBefore = await RevisionCountAsync("H1SpikeDocs/b");
        var bResult = await Store.Maintenance.SendAsync(new DeleteRevisionsOperation("H1SpikeDocs/b", removeForceCreatedRevisions: true));
        var bAfterRevisions = await RevisionCountAsync("H1SpikeDocs/b");
        await DeleteDocumentAsync("H1SpikeDocs/b");
        var bAfterDelete = await RevisionCountAsync("H1SpikeDocs/b");
        output.WriteLine($"B revisions-then-delete: before={bBefore} deleted={bResult.TotalDeletes} afterRevisions={bAfterRevisions} afterDelete={bAfterDelete}");

        // C: a force-created revision in a collection without a revisions configuration.
        using (var session = Store.OpenAsyncSession())
        {
            var doc = new H1SpikeUnconfigured { Id = "H1SpikeUnconfigureds/c", Name = "v1" };
            await session.StoreAsync(doc);
            await session.SaveChangesAsync();
            session.Advanced.Revisions.ForceRevisionCreationFor(doc);
            await session.SaveChangesAsync();
        }
        var cBefore = await RevisionCountAsync("H1SpikeUnconfigureds/c");
        await DeleteDocumentAsync("H1SpikeUnconfigureds/c");
        var cAfterDelete = await RevisionCountAsync("H1SpikeUnconfigureds/c");
        var cWithoutFlag = await Store.Maintenance.SendAsync(new DeleteRevisionsOperation("H1SpikeUnconfigureds/c", removeForceCreatedRevisions: false));
        var cAfterWithoutFlag = await RevisionCountAsync("H1SpikeUnconfigureds/c");
        var cWithFlag = await Store.Maintenance.SendAsync(new DeleteRevisionsOperation("H1SpikeUnconfigureds/c", removeForceCreatedRevisions: true));
        var cAfter = await RevisionCountAsync("H1SpikeUnconfigureds/c");
        output.WriteLine($"C force-created: before={cBefore} afterDelete={cAfterDelete} withoutFlag deleted={cWithoutFlag.TotalDeletes} left={cAfterWithoutFlag} withFlag deleted={cWithFlag.TotalDeletes} left={cAfter}");

        aBefore.Should().Be(3, "a create and two edits");
        aAfterDelete.Should().Be(4, "a delete adds a delete revision");
        aAfterPurge.Should().Be(0, "a purge that deletes the document first leaves no revision");
        bAfterRevisions.Should().Be(0);
        bAfterDelete.Should().Be(2, "deleting the document after its revisions writes fresh revisions — the wrong order for a purge");
        cBefore.Should().Be(1);
        cAfterDelete.Should().Be(2, "a document with a force-created revision gets a delete revision even without a configuration");
        cAfterWithoutFlag.Should().Be(1, "force-created revisions survive without the flag");
        cAfter.Should().Be(0);
    }

    private async Task WriteHistoryAsync(string id)
    {
        foreach (var name in new[] { "v1", "v2", "v3" })
        {
            using var session = Store.OpenAsyncSession();
            var doc = await session.LoadAsync<H1SpikeDoc>(id) ?? new H1SpikeDoc { Id = id };
            doc.Name = name;
            await session.StoreAsync(doc);
            await session.SaveChangesAsync();
        }
    }

    private async Task DeleteDocumentAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        session.Delete(id);
        await session.SaveChangesAsync();
    }

    private async Task<long> RevisionCountAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return await session.Advanced.Revisions.GetCountForAsync(id);
    }

    private async Task<string> LicenceTypeAsync()
    {
        using var http = new HttpClient();
        var json = await http.GetStringAsync(Store.Urls[0].TrimEnd('/') + "/license/status");
        using var doc = JsonDocument.Parse(json);
        // Only non-identifying fields: the status also names the licensee.
        return string.Join(", ", new[] { "Type", "Status", "Expired", "Expiration", "MaxCores" }
            .Select(name => $"{name}={(doc.RootElement.TryGetProperty(name, out var value) ? value.ToString() : "(absent)")}"));
    }
}

public class H1SpikeDoc
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class H1SpikeUnconfigured
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
