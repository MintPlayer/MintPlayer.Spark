using System.Net;
using MintPlayer.Spark.Files.Storage;
using Raven.Client.Documents.Operations.Revisions;

namespace MintPlayer.Spark.Tests.Files.Stores;

/// <summary>
/// #700 SP8 on the RavenDB store: a replaced file stays readable from a revision until <see cref="IFileStore.EraseAsync"/>
/// deletes the revisions holding it. Revisions that never held it, and the current file, stay.
/// </summary>
public class RavenEraseTests : FilesTestHost
{
    private const string Alice = "users/alice";

    [Fact]
    public async Task Erase_deletes_the_file_and_the_revisions_that_still_serve_it()
    {
        await Store.Maintenance.SendAsync(new ConfigureRevisionsOperation(new RevisionsConfiguration
        {
            Collections = new() { ["FiCars"] = new RevisionsCollectionConfiguration { Disabled = false } },
        }));
        var id = await SeedCarAsync();
        (await SaveCarAsync(Alice, id, ("Name", "Polo"))).Status.Should().Be(HttpStatusCode.OK);
        var (first, _) = await StageAsync(Alice, "Document", "scan.pdf", id: id);
        (await SaveCarAsync(Alice, id, ("Document", new { uploadToken = first }))).Status.Should().Be(HttpStatusCode.OK);
        var old = (await LoadCarAsync(id)).Document!.FileId;
        var (second, _) = await StageAsync(Alice, "Document", "scan.pdf", asName: "v2.pdf", id: id);
        (await SaveCarAsync(Alice, id, ("Document", new { uploadToken = second }))).Status.Should().Be(HttpStatusCode.OK);
        var current = (await LoadCarAsync(id)).Document!.FileId;
        (await RevisionsHoldingAsync(id, old)).Should().BeGreaterThan(0, "a revision keeps the replaced attachment (SP8)");
        var revisionsBefore = await RevisionCountAsync(id);

        await Factory.GetService<IFileStore>().EraseAsync(id, old, CancellationToken.None);

        (await RevisionsHoldingAsync(id, old)).Should().Be(0);
        (await RevisionCountAsync(id)).Should().BeGreaterThan(0, "revisions that never held the file stay")
            .And.BeLessThan(revisionsBefore);
        (await CarAttachmentsAsync(id)).Should().Equal(current);
        (await CarAttachmentBytesAsync(id, current)).Should().Equal(FileSnifferTests.Fixture("scan.pdf"));
    }

    private async Task<int> RevisionCountAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        return (await session.Advanced.Revisions.GetMetadataForAsync(id, 0, 100)).Count;
    }

    private async Task<int> RevisionsHoldingAsync(string id, string fileId)
    {
        using var session = Store.OpenAsyncSession();
        var count = 0;
        foreach (var metadata in await session.Advanced.Revisions.GetMetadataForAsync(id, 0, 100))
        {
            if (metadata.TryGetValue("@change-vector", out object? vector)
                && (await session.Advanced.Attachments.GetRevisionAsync(id, fileId, vector!.ToString()!)) is { } attachment)
            {
                attachment.Dispose();
                count++;
            }
        }
        return count;
    }
}
