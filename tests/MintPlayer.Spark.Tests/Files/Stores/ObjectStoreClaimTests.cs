using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Files;
using MintPlayer.Spark.Files.FileSystem;
using MintPlayer.Spark.Files.Images.SkiaSharp;
using MintPlayer.Spark.Files.Services;
using MintPlayer.Spark.Files.Staging;
using MintPlayer.Spark.Files.Storage;

namespace MintPlayer.Spark.Tests.Files.Stores;

/// <summary>
/// The save path on a store outside the session (M5), with the file system store: a claim copies the staged bytes to the
/// entity's key before the commit and deletes the staged copy after it; a replace, clear or entity delete removes the old
/// bytes after the commit; a refused save leaves the store as it was; and the reconcile removes only what nothing holds,
/// and only past its grace.
/// </summary>
public class ObjectStoreClaimTests : FilesTestHost
{
    private const string Alice = "users/alice";
    private readonly string root = Path.Combine(Path.GetTempPath(), "spark-fs-claims-" + Guid.NewGuid().ToString("N"));

    public ObjectStoreClaimTests() => ConfigureFiles = files => files.UseFileSystem(root);

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private ObjectFileStore FileStore => (ObjectFileStore)Factory.GetService<IFileStore>();

    private async Task<string[]> KeysAsync(string prefix)
    {
        var keys = new List<string>();
        await foreach (var item in FileStore.ListAsync(prefix, CancellationToken.None))
            keys.Add(item.Key);
        return keys.Order(StringComparer.Ordinal).ToArray();
    }

    private async Task<byte[]?> ReadAsync(string key)
    {
        await using var stream = await FileStore.OpenReadAsync(key, CancellationToken.None);
        if (stream is null)
            return null;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private void Backdate(string key, TimeSpan age)
        => File.SetLastWriteTimeUtc(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)), DateTime.UtcNow - age);

    [Fact]
    public async Task A_claim_copies_to_the_entity_key_and_removes_the_staged_bytes_after_the_commit()
    {
        var (token, _) = await StageAsync(Alice, "Document", "scan.pdf");
        (await KeysAsync(ObjectFileStore.StagingRoot)).Should().Equal(FileStore.StagingKey(token));

        var (status, body) = await SaveCarAsync(Alice, null, ("Name", "Golf"), ("Document", new { uploadToken = token }));

        status.Should().Be(HttpStatusCode.Created, body.ToString());
        var id = body.GetProperty("result").GetProperty("id").GetString()!;
        var fileId = (await LoadCarAsync(id)).Document!.FileId;
        (await ReadAsync(FileStore.EntityKey(id, fileId))).Should().Equal(FileSnifferTests.Fixture("scan.pdf"));
        (await KeysAsync(ObjectFileStore.StagingRoot)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_images_variants_are_staged_beside_it_copied_with_it_and_removed_with_it()
    {
        ConfigureFiles = files => files.UseFileSystem(root).UseSkiaSharpImages();
        var (token, _) = await StageAsync(Alice, "Photos", "photo.png");
        (await KeysAsync(ObjectFileStore.StagingRoot)).Should().Equal(
            FileStore.StagingKey(token), FileStore.StagingVariantKey(token, "medium"), FileStore.StagingVariantKey(token, "thumb"));

        var (status, body) = await SaveCarAsync(Alice, null, ("Name", "Golf"), ("Photos", new[] { new { uploadToken = token } }));

        status.Should().Be(HttpStatusCode.Created, body.ToString());
        var id = body.GetProperty("result").GetProperty("id").GetString()!;
        var photo = (await LoadCarAsync(id)).Photos.Single();
        (await KeysAsync(FileStore.EntityPrefix(id))).Should().Equal(
            FileStore.EntityKey(id, photo.FileId), FileStore.EntityKey(id, photo.FileId + ".medium"), FileStore.EntityKey(id, photo.FileId + ".thumb"));
        (await KeysAsync(ObjectFileStore.StagingRoot)).Should().BeEmpty();
        ObjectFileStore.TryParseStaging(FileStore.StagingVariantKey(token, "thumb"), out var parsed).Should().BeTrue();
        parsed.Should().Be(token, "the reconcile finds a variant's upload by its token");

        (await SaveCarAsync(Alice, id, ("Photos", Array.Empty<object>()))).Status.Should().Be(HttpStatusCode.OK);

        (await KeysAsync(FileStore.EntityPrefix(id))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_save_leaves_the_store_as_it_was()
    {
        var (token, _) = await StageAsync(Alice, "Document", "scan.pdf");

        var (status, _) = await SaveCarAsync(Alice, null, ("Document", new { uploadToken = token }), ("Photos", new[] { new { fileId = "forged" } }));

        status.Should().Be(HttpStatusCode.BadRequest);
        (await KeysAsync(ObjectFileStore.StagingRoot)).Should().Equal(FileStore.StagingKey(token));
        (await KeysAsync(ObjectFileStore.EntitiesRoot)).Should().BeEmpty();
    }

    [Fact]
    public async Task Replacing_and_clearing_delete_the_old_bytes_after_the_commit()
    {
        var id = await CreateCarWithAsync(Alice, "Document", "scan.pdf");
        var old = (await LoadCarAsync(id)).Document!.FileId;
        var (token, _) = await StageAsync(Alice, "Document", "scan.pdf", asName: "v2.pdf", id: id);

        (await SaveCarAsync(Alice, id, ("Document", new { uploadToken = token }))).Status.Should().Be(HttpStatusCode.OK);

        var current = (await LoadCarAsync(id)).Document!.FileId;
        (await KeysAsync(FileStore.EntityPrefix(id))).Should().Equal(FileStore.EntityKey(id, current));
        (await ReadAsync(FileStore.EntityKey(id, old))).Should().BeNull();

        (await SaveCarAsync(Alice, id, ("Document", null))).Status.Should().Be(HttpStatusCode.OK);
        (await KeysAsync(FileStore.EntityPrefix(id))).Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_the_entity_deletes_every_object_it_held()
    {
        var id = await CreateCarWithAsync(Alice, "Photos", "photo.png");
        var fileId = (await LoadCarAsync(id)).Photos[0].FileId;
        // A variant (M8) sits beside its file and goes with the entity too.
        await FileStore.WriteAsync(FileStore.EntityKey(id, fileId + ".thumb"), new MemoryStream([1, 2]), null, CancellationToken.None);

        var (status, _) = await PostJsonAsync("/spark/po/delete", MintPlayer.Spark.Testing.Wire.Typed(CarTypeId, id: id, etag: await MintPlayer.Spark.Tests._Infrastructure.StoredEtag.OfAsync(Store, id)), Alice);

        status.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        (await KeysAsync(FileStore.EntityPrefix(id))).Should().BeEmpty();
    }

    [Fact]
    public async Task The_reconcile_removes_only_unheld_objects_past_the_grace()
    {
        var id = await CreateCarWithAsync(Alice, "Document", "scan.pdf");
        var held = FileStore.EntityKey(id, (await LoadCarAsync(id)).Document!.FileId);
        var heldVariant = held + ".thumb";
        var orphan = FileStore.EntityKey(id, "orphan");
        var freshOrphan = FileStore.EntityKey(id, "fresh");
        var goneEntity = FileStore.EntityKey("FiCars/404-A", "lost");
        var (committed, _) = await StageAsync(Alice, "Document", "scan.pdf");
        var expired = FileStore.StagingKey("expiredtoken");
        var claimedToken = await ClaimedStagedObjectAsync();
        foreach (var key in new[] { heldVariant, orphan, freshOrphan, goneEntity, expired })
            await FileStore.WriteAsync(key, new MemoryStream([7]), null, CancellationToken.None);
        foreach (var key in new[] { held, heldVariant, orphan, goneEntity, expired, FileStore.StagingKey(committed), FileStore.StagingKey(claimedToken) })
            Backdate(key, TimeSpan.FromHours(2));

        var deleted = await Factory.GetService<FilesReconciler>().ReconcileAsync(CancellationToken.None);

        deleted.Should().BeEquivalentTo([orphan, goneEntity, expired, FileStore.StagingKey(claimedToken)]);
        (await KeysAsync(FileStore.EntityPrefix(id))).Should().BeEquivalentTo([held, heldVariant, freshOrphan]);
        (await KeysAsync(ObjectFileStore.StagingRoot)).Should().Equal(FileStore.StagingKey(committed));
    }

    /// <summary>
    /// #536 D7: an entity's files are keyed by its lower-case id, so any casing of the id finds them. An object stored before
    /// (keyed by the id as the entity had it, e.g. <c>Accounts/github/1</c>) is moved to its lower-case key by the reconcile,
    /// grace or not: a move loses nothing.
    /// </summary>
    [Fact]
    public async Task A_legacy_cased_key_is_moved_to_the_lower_case_one()
    {
        var id = await CreateCarWithAsync(Alice, "Document", "scan.pdf");
        var fileId = (await LoadCarAsync(id)).Document!.FileId;
        var key = FileStore.EntityKey(id, fileId);
        FileStore.EntityKey(id.ToUpperInvariant(), fileId).Should().Be(key);
        var legacyId = char.ToUpperInvariant(id[0]) + id[1..];
        var legacy = ObjectFileStore.EntitiesRoot
            + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(legacyId)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            + "/" + fileId;
        await FileStore.CopyAsync(key, legacy, CancellationToken.None);
        await FileStore.DeleteAsync(key, CancellationToken.None);

        var deleted = await Factory.GetService<FilesReconciler>().ReconcileAsync(CancellationToken.None);

        deleted.Should().BeEmpty();
        (await KeysAsync(ObjectFileStore.EntitiesRoot)).Should().Equal(key);
        (await ReadAsync(key)).Should().Equal(FileSnifferTests.Fixture("scan.pdf"));
    }

    /// <summary>A staged object whose upload was claimed, but whose after-commit delete never ran (a crash).</summary>
    private async Task<string> ClaimedStagedObjectAsync()
    {
        var (token, _) = await StageAsync(Alice, "Document", "scan.pdf");
        using (var session = Store.OpenAsyncSession())
        {
            var upload = await session.LoadAsync<SparkFileUpload>(SparkFileUpload.IdFor(token).Value);
            upload!.State = SparkFileUploadState.Claimed;
            await session.SaveChangesAsync();
        }
        return token;
    }
}
