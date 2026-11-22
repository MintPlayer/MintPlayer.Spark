using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Files;
using MintPlayer.Spark.Files.Staging;

namespace MintPlayer.Spark.Tests.Files;

/// <summary>
/// #700 D3/D9 on the save path: a JSON save claims staged uploads, keeps the files it already holds, and nothing else.
/// The upload steps themselves are in <see cref="FileUploadEndpointTests"/>.
/// </summary>
public class FileClaimTests : FilesTestHost
{
    private const string Alice = "users/alice";
    private const string Bob = "users/bob";

    [Fact]
    public async Task A_create_claims_its_upload_and_the_bytes_become_an_attachment_of_the_entity()
    {
        var (token, _) = await StageAsync(Alice, "Document", "scan.pdf", asName: "Scan.pdf");

        var (status, body) = await SaveCarAsync(Alice, null, ("Name", "Golf"), ("Document", new { uploadToken = token }));

        status.Should().Be(HttpStatusCode.Created, body.ToString());
        var id = body.GetProperty("result").GetProperty("id").GetString()!;
        var car = await LoadCarAsync(id);
        car.Document.Should().NotBeNull();
        car.Document!.FileName.Should().Be("Scan.pdf");
        car.Document.ContentType.Should().Be("application/pdf");
        car.Document.ScanStatus.Should().Be(SparkFileScanStatus.Clean);
        car.Document.UploadedBy.Should().Be(Alice);
        car.Document.FileId.Should().NotContain(token, "the stored id is the server's, not the upload's");
        (await CarAttachmentsAsync(id)).Should().Equal(car.Document.FileId);
        (await CarAttachmentBytesAsync(id, car.Document.FileId)).Should().Equal(FileSnifferTests.Fixture("scan.pdf"));
        (await StagedAsync(token))!.State.Should().Be(SparkFileUploadState.Claimed);
    }

    [Fact]
    public async Task An_edit_claims_a_slot_prepared_for_that_object()
    {
        var id = await SeedCarAsync();
        var (token, _) = await StageAsync(Alice, "Document", "scan.pdf", id: id);

        var (status, body) = await SaveCarAsync(Alice, id, ("Document", new { uploadToken = token }));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var car = await LoadCarAsync(id);
        (await CarAttachmentsAsync(id)).Should().Equal(car.Document!.FileId);
    }

    [Fact]
    public async Task A_token_is_claimed_once()
    {
        var (token, _) = await StageAsync(Alice, "Document", "scan.pdf");
        (await SaveCarAsync(Alice, null, ("Document", new { uploadToken = token }))).Status.Should().Be(HttpStatusCode.Created);

        var (status, _) = await SaveCarAsync(Alice, null, ("Document", new { uploadToken = token }));

        status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Another_users_token_is_refused_and_stays_claimable_by_its_owner()
    {
        var (token, _) = await StageAsync(Alice, "Document", "scan.pdf");

        var (status, body) = await SaveCarAsync(Bob, null, ("Document", new { uploadToken = token }));

        status.Should().Be(HttpStatusCode.BadRequest);
        body.ToString().Should().Contain("can't be used");
        (await StagedAsync(token))!.State.Should().Be(SparkFileUploadState.Committed);
        (await SaveCarAsync(Alice, null, ("Document", new { uploadToken = token }))).Status.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_token_for_another_attribute_is_refused()
    {
        var (token, _) = await StageAsync(Alice, "Photos", "photo.png");

        (await SaveCarAsync(Alice, null, ("Document", new { uploadToken = token }))).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_create_slot_cannot_be_claimed_by_an_edit_nor_an_edit_slot_by_another_object()
    {
        var golf = await SeedCarAsync("Golf");
        var polo = await SeedCarAsync("Polo");
        var (createToken, _) = await StageAsync(Alice, "Document", "scan.pdf");
        var (golfToken, _) = await StageAsync(Alice, "Document", "scan.pdf", id: golf);

        (await SaveCarAsync(Alice, polo, ("Document", new { uploadToken = createToken }))).Status.Should().Be(HttpStatusCode.BadRequest);
        (await SaveCarAsync(Alice, polo, ("Document", new { uploadToken = golfToken }))).Status.Should().Be(HttpStatusCode.BadRequest);
        (await SaveCarAsync(Alice, null, ("Document", new { uploadToken = golfToken }))).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_uncommitted_or_refused_upload_is_not_claimable()
    {
        var uploaded = await PrepareOneAsync(Alice, "Document", "scan.pdf");
        using (var upload = await UploadAsync(Alice, uploaded, "scan.pdf", FileSnifferTests.Fixture("scan.pdf")))
            upload.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var (refused, _) = await StageAsync(Alice, "Document", "photo.png", asName: "photo.pdf");

        (await SaveCarAsync(Alice, null, ("Document", new { uploadToken = uploaded }))).Status.Should().Be(HttpStatusCode.BadRequest);
        (await SaveCarAsync(Alice, null, ("Document", new { uploadToken = refused }))).Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_forged_file_value_is_refused()
    {
        var other = await CreateCarWithAsync(Alice, "Document", "scan.pdf");
        var stolen = (await LoadCarAsync(other)).Document!;

        var (status, _) = await SaveCarAsync(Bob, null, ("Document", new
        {
            fileId = stolen.FileId,
            fileName = "mine.pdf",
            contentType = "application/pdf",
            length = stolen.Length,
            scanStatus = "Clean",
        }));

        status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Echoing_the_stored_value_keeps_the_file()
    {
        var id = await CreateCarWithAsync(Alice, "Document", "scan.pdf");
        var stored = (await LoadCarAsync(id)).Document!;

        var (status, body) = await SaveCarAsync(Alice, id, ("Name", "Polo"), ("Document", new { fileId = stored.FileId, fileName = "renamed-by-client.exe", url = "https://evil.example/x" }));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var car = await LoadCarAsync(id);
        car.Name.Should().Be("Polo");
        car.Document!.FileId.Should().Be(stored.FileId);
        car.Document.FileName.Should().Be(stored.FileName, "a kept file takes nothing from the echoed value");
        (await CarAttachmentsAsync(id)).Should().Equal(stored.FileId);
    }

    [Fact]
    public async Task Null_clears_the_attribute_and_deletes_the_attachment()
    {
        var id = await CreateCarWithAsync(Alice, "Document", "scan.pdf");

        var (status, body) = await SaveCarAsync(Alice, id, ("Document", null));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        (await LoadCarAsync(id)).Document.Should().BeNull();
        (await CarAttachmentsAsync(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Replacing_a_file_deletes_the_old_attachment()
    {
        var id = await CreateCarWithAsync(Alice, "Document", "scan.pdf");
        var old = (await LoadCarAsync(id)).Document!.FileId;
        var (token, _) = await StageAsync(Alice, "Document", "scan.pdf", asName: "v2.pdf", id: id);

        (await SaveCarAsync(Alice, id, ("Document", new { uploadToken = token }))).Status.Should().Be(HttpStatusCode.OK);

        var car = await LoadCarAsync(id);
        car.Document!.FileName.Should().Be("v2.pdf");
        car.Document.FileId.Should().NotBe(old);
        (await CarAttachmentsAsync(id)).Should().Equal(car.Document.FileId);
    }

    [Fact]
    public async Task A_list_adds_removes_and_reorders_in_one_save()
    {
        var id = await SeedCarAsync();
        var (png, _) = await StageAsync(Alice, "Photos", "photo.png", id: id);
        var (jpg, _) = await StageAsync(Alice, "Photos", "photo.jpg", id: id);
        (await SaveCarAsync(Alice, id, ("Photos", new[] { new { uploadToken = png }, new { uploadToken = jpg } }))).Status.Should().Be(HttpStatusCode.OK);
        var first = (await LoadCarAsync(id)).Photos.Select(p => p.FileId).ToArray();
        var (gif, _) = await StageAsync(Alice, "Photos", "photo.gif", id: id);

        // jpg first, png dropped, gif added.
        var (status, body) = await SaveCarAsync(Alice, id, ("Photos", new object[] { new { fileId = first[1] }, new { uploadToken = gif } }));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var photos = (await LoadCarAsync(id)).Photos;
        photos.Select(p => p.ContentType).Should().Equal("image/jpeg", "image/gif");
        photos[0].FileId.Should().Be(first[1]);
        (await CarAttachmentsAsync(id)).Should().Equal(photos.Select(p => p.FileId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_list_keeps_each_file_once_and_respects_maxCount()
    {
        var id = await CreateCarWithAsync(Alice, "Photos", "photo.png");
        var kept = (await LoadCarAsync(id)).Photos[0].FileId;

        (await SaveCarAsync(Alice, id, ("Photos", new[] { new { fileId = kept }, new { fileId = kept } }))).Status.Should().Be(HttpStatusCode.BadRequest);

        var tokens = new List<string>();
        for (var i = 0; i < 3; i++)
            tokens.Add((await StageAsync(Alice, "Photos", "photo.png", id: id)).Token);
        var (status, _) = await SaveCarAsync(Alice, id, ("Photos", tokens.Select(t => (object)new { uploadToken = t }).Prepend(new { fileId = kept }).ToArray()));
        status.Should().Be(HttpStatusCode.BadRequest);
        (await LoadCarAsync(id)).Photos.Should().ContainSingle();
    }

    [Fact]
    public async Task A_refused_save_claims_nothing()
    {
        var (token, _) = await StageAsync(Alice, "Document", "scan.pdf");
        var (photo, _) = await StageAsync(Alice, "Photos", "photo.png");

        // The Document claim is valid, the Photos value is forged: the whole save is refused.
        var (status, _) = await SaveCarAsync(Alice, null, ("Document", new { uploadToken = token }), ("Photos", new[] { new { fileId = "forged" } }));

        status.Should().Be(HttpStatusCode.BadRequest);
        (await StagedAsync(token))!.State.Should().Be(SparkFileUploadState.Committed);
        (await StagedAsync(photo))!.State.Should().Be(SparkFileUploadState.Committed);
    }

    [Fact]
    public async Task Deleting_the_entity_deletes_its_files()
    {
        var id = await CreateCarWithAsync(Alice, "Document", "scan.pdf");
        var fileId = (await LoadCarAsync(id)).Document!.FileId;

        var (status, _) = await PostJsonAsync("/spark/po/delete", MintPlayer.Spark.Testing.Wire.Typed(CarTypeId, id: id, etag: await MintPlayer.Spark.Tests._Infrastructure.StoredEtag.OfAsync(Store, id)), Alice);

        status.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        (await CarAttachmentBytesAsync(id, fileId)).Should().BeNull();
    }

    [Fact]
    public async Task The_accepted_hook_runs_after_the_commit_with_the_stored_file()
    {
        var hook = new FiAcceptedRecorder();
        ExtraServices = services => services.AddSingleton(hook);
        ConfigureFiles = files => files.AddUploadHandler<FiAcceptedHandler>();

        var id = await CreateCarWithAsync(Alice, "Document", "scan.pdf");

        var stored = (await LoadCarAsync(id)).Document!;
        hook.Seen.Should().ContainSingle();
        hook.Seen[0].Should().Be((id, "Document", stored.FileId, Alice));
    }
}

internal sealed class FiAcceptedRecorder
{
    public List<(string? CarId, string Attribute, string FileId, string UserId)> Seen { get; } = [];
}

internal sealed class FiAcceptedHandler(FiAcceptedRecorder recorder) : IFileUploadHandler
{
    public ValueTask OnFileAcceptedAsync(FileAcceptedContext context, CancellationToken cancellationToken)
    {
        recorder.Seen.Add((((FiCar)context.Entity).Id, context.Attribute.Name, context.File.FileId, context.UserId));
        return ValueTask.CompletedTask;
    }
}
