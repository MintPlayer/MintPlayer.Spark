using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Files.Images;
using MintPlayer.Spark.Files.Images.SkiaSharp;
using MintPlayer.Spark.Files.Staging;
using MintPlayer.Spark.Tests.Organizations;
using MintPlayer.Spark.Tests._Infrastructure;
using SkiaSharp;

namespace MintPlayer.Spark.Tests.Files.Images;

/// <summary>
/// #700 M8 through the endpoints: with a processor registered, an <c>Image</c> commit re-encodes the staged bytes in place and
/// stages its variants next to them; the claim moves the variants with the file, and a cleared image takes them along;
/// a bomb is refused at commit, its bytes deleted.
/// </summary>
public class FileImageProcessingTests : FilesTestHost
{
    private const string Alice = "users/alice";

    public FileImageProcessingTests() => ConfigureFiles = files => files.UseSkiaSharpImages();

    private async Task<(string Token, JsonElement Committed)> StageBytesAsync(string fileName, byte[] bytes, string? id = null)
    {
        var (status, body) = await PrepareAsync(Alice, "Photos", id, (fileName, bytes.Length));
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var token = body.GetProperty("files")[0].GetProperty("token").GetString()!;
        using (var upload = await UploadAsync(Alice, token, fileName, bytes))
            upload.StatusCode.Should().Be(HttpStatusCode.NoContent, await upload.Content.ReadAsStringAsync());
        var (commitStatus, committed) = await CommitAsync(Alice, token);
        commitStatus.Should().Be(HttpStatusCode.OK, committed.ToString());
        return (token, committed.GetProperty("files")[0]);
    }

    [Fact]
    public async Task A_commit_re_encodes_the_image_in_place_and_stages_its_default_variants()
    {
        var jpeg = ImageFixtures.WithExif(ImageFixtures.Jpeg(1200, 600), orientation: 6, artist: "SPARK-ARTIST-MARKER", gps: true);

        var (token, committed) = await StageBytesAsync("holiday.jpg", jpeg);

        committed.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null, committed.ToString());
        committed.GetProperty("width").GetInt32().Should().Be(600, "orientation 6 makes the stored image portrait");
        committed.GetProperty("height").GetInt32().Should().Be(1200);
        var staged = (await StagedAsync(token))!;
        staged.State.Should().Be(SparkFileUploadState.Committed);
        staged.ImageProcessed.Should().BeTrue();
        staged.ContentType.Should().Be("image/jpeg");
        staged.Variants!.Keys.Order(StringComparer.Ordinal).Should().Equal("medium", "thumb");
        staged.Variants["thumb"].Width.Should().Be(80);
        staged.Variants["thumb"].Height.Should().Be(160);
        staged.Variants["medium"].ContentType.Should().Be("image/webp");
        (await StagedAttachmentCountAsync(token)).Should().Be(3, "the original and two variants");

        var stored = (await StagedBytesAsync(token))!;
        stored.Should().NotEqual(jpeg);
        ImageFixtures.Contains(stored, "SPARK-ARTIST-MARKER").Should().BeFalse();
        ImageFixtures.Contains(stored, "Exif").Should().BeFalse();
        staged.Length.Should().Be(stored.Length, "the record describes the bytes that are stored, not the upload");
        staged.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(stored)));
    }

    [Fact]
    public async Task The_claim_moves_the_variants_with_the_file_and_a_variant_downloads()
    {
        var (token, _) = await StageBytesAsync("car.png", ImageFixtures.Png(1000, 500));

        var (status, body) = await SaveCarAsync(Alice, null, ("Name", "Golf"), ("Photos", new[] { new { uploadToken = token } }));

        status.Should().Be(HttpStatusCode.Created, body.ToString());
        var id = body.GetProperty("result").GetProperty("id").GetString()!;
        var photo = (await LoadCarAsync(id)).Photos.Single();
        (photo.Width, photo.Height).Should().Be((1000, 500));
        photo.Variants["thumb"].FileId.Should().Be(photo.FileId + ".thumb");
        (photo.Variants["thumb"].Width, photo.Variants["thumb"].Height).Should().Be((160, 80));
        photo.Variants["medium"].FileId.Should().Be(photo.FileId + ".medium");
        (await CarAttachmentsAsync(id)).Should().Equal(photo.FileId, photo.FileId + ".medium", photo.FileId + ".thumb");

        var thumbBytes = (await CarAttachmentBytesAsync(id, photo.FileId + ".thumb"))!;
        photo.Variants["thumb"].Length.Should().Be(thumbBytes.Length);
        using var response = await GetAsync(
            $"/spark/files/download?type={CarTypeId:D}&id={Uri.EscapeDataString(id)}&attribute=Photos&file={photo.FileId}&variant=thumb", Alice);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/webp");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(thumbBytes);
    }

    [Fact]
    public async Task Clearing_an_image_removes_its_variants_too()
    {
        var (token, _) = await StageBytesAsync("car.png", ImageFixtures.Png(400, 200));
        var (_, created) = await SaveCarAsync(Alice, null, ("Name", "Golf"), ("Photos", new[] { new { uploadToken = token } }));
        var id = created.GetProperty("result").GetProperty("id").GetString()!;
        (await CarAttachmentsAsync(id)).Should().HaveCount(3);

        var (status, body) = await SaveCarAsync(Alice, id, ("Photos", Array.Empty<object>()));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        (await CarAttachmentsAsync(id)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_gif_is_stored_as_a_png_named_png()
    {
        var (token, committed) = await StageBytesAsync("wave.gif", ImageFixtures.AnimatedGif());

        committed.GetProperty("contentType").GetString().Should().Be("image/png");
        committed.GetProperty("fileName").GetString().Should().Be("wave.png");
        var stored = (await StagedBytesAsync(token))!;
        stored[..4].Should().Equal(0x89, 0x50, 0x4E, 0x47);
    }

    [Fact]
    public async Task A_bomb_is_refused_at_commit_and_its_bytes_deleted()
    {
        var (token, committed) = await StageBytesAsync("bomb.png", ImageFixtures.Bomb);

        committed.GetProperty("error").GetString().Should().Contain("30000×30000");
        var staged = (await StagedAsync(token))!;
        staged.State.Should().Be(SparkFileUploadState.Refused);
        (await StagedAttachmentCountAsync(token)).Should().Be(0);
    }

    [Fact]
    public async Task An_attributes_maxPixels_tightens_the_global_cap()
    {
        ExtraModel = model => model.PersistentObject.Attributes.Single(a => a.Name == "Photos").FileOptions!.MaxPixels = 100 * 100;

        var (_, small) = await StageBytesAsync("small.png", ImageFixtures.Png(100, 100));
        var (_, large) = await StageBytesAsync("large.png", ImageFixtures.Png(101, 100));

        small.GetProperty("error").ValueKind.Should().Be(JsonValueKind.Null, small.ToString());
        large.GetProperty("error").GetString().Should().Contain("101×100");
    }

    [Fact]
    public async Task A_processor_that_throws_refuses_the_upload_with_a_generic_message()
    {
        ConfigureFiles = files => files.UseImageProcessor<ThrowingProcessor>();

        var (token, committed) = await StageBytesAsync("car.png", ImageFixtures.Png(40, 20));

        committed.GetProperty("error").GetString().Should().Be("The image could not be read.");
        (await StagedAsync(token))!.State.Should().Be(SparkFileUploadState.Refused);
        (await StagedAttachmentCountAsync(token)).Should().Be(0);
    }

    [Fact]
    public async Task Regenerating_skips_images_that_match_remakes_missing_variants_and_removes_obsolete_ones()
    {
        var (token, _) = await StageBytesAsync("car.png", ImageFixtures.Png(400, 200));
        var (_, created) = await SaveCarAsync(Alice, null, ("Name", "Golf"), ("Photos", new[] { new { uploadToken = token } }));
        var id = created.GetProperty("result").GetProperty("id").GetString()!;
        var regenerator = Factory.GetService<ImageVariantRegenerator>();

        (await regenerator.RunAsync(force: false, CancellationToken.None)).Should().Be(new ImageVariantRegeneration(1, 0, 0));

        // As if stored before a processor was registered, plus a variant the configuration no longer names.
        string fileId;
        using (var session = Store.OpenAsyncSession())
        {
            var car = (await session.LoadAsync<FiCar>(id))!;
            var photo = car.Photos.Single();
            fileId = photo.FileId;
            photo.Variants.Clear();
            photo.Variants["old"] = new SparkImageVariant { FileId = fileId + ".old", ContentType = "image/webp", Width = 1, Height = 1, Length = 1 };
            session.Advanced.Attachments.Delete(id, fileId + ".thumb");
            session.Advanced.Attachments.Store(id, fileId + ".old", new MemoryStream([1]), "image/webp");
            await session.SaveChangesAsync();
        }

        (await regenerator.RunAsync(force: false, CancellationToken.None)).Should().Be(new ImageVariantRegeneration(1, 1, 0));

        var regenerated = (await LoadCarAsync(id)).Photos.Single();
        regenerated.Variants.Keys.Order(StringComparer.Ordinal).Should().Equal("medium", "thumb");
        (regenerated.Variants["thumb"].Width, regenerated.Variants["thumb"].Height).Should().Be((160, 80));
        (await CarAttachmentsAsync(id)).Should().Equal(fileId, fileId + ".medium", fileId + ".thumb");
        (await CarAttachmentBytesAsync(id, fileId + ".thumb"))!.Length.Should().Be((int)regenerated.Variants["thumb"].Length);

        (await regenerator.RunAsync(force: true, CancellationToken.None)).Should().Be(new ImageVariantRegeneration(1, 1, 0));
    }

    [Fact]
    public void Variants_are_up_to_date_only_when_names_and_fitted_sizes_match()
    {
        var image = new SparkImage { FileId = "f", Width = 1000, Height = 500 };
        image.Variants["thumb"] = new SparkImageVariant { FileId = "f.thumb", Width = 160, Height = 80 };
        var wanted = new Dictionary<string, int> { ["thumb"] = 160 };

        ImageVariantRegenerator.UpToDate(image, wanted).Should().BeTrue();
        ImageVariantRegenerator.UpToDate(image, new Dictionary<string, int> { ["thumb"] = 200 }).Should().BeFalse("the edge changed");
        ImageVariantRegenerator.UpToDate(image, new Dictionary<string, int> { ["thumb"] = 160, ["medium"] = 800 }).Should().BeFalse("a variant was added");
        ImageVariantRegenerator.UpToDate(image, new Dictionary<string, int>()).Should().BeFalse("variants were turned off");
        ImageVariantRegenerator.UpToDate(new SparkImage { FileId = "g" }, new Dictionary<string, int>()).Should().BeFalse("an image stored without a processor has no size yet");
    }

    private sealed class ThrowingProcessor : IImageProcessor
    {
        public Task<ProcessedImage> ProcessAsync(Stream source, ImageProcessingRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("native crash stand-in: /secret/path/in/message");
    }

    private async Task<byte[]?> StagedBytesAsync(string token)
    {
        using var session = Store.OpenAsyncSession();
        using var attachment = await session.Advanced.Attachments.GetAsync(SparkFileUpload.IdFor(token).Value, "original");
        if (attachment is null)
            return null;
        using var buffer = new MemoryStream();
        await attachment.Stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private async Task<HttpResponseMessage> GetAsync(string url, string userId)
    {
        var client = Factory.CreateClient();
        var (cookie, _) = await OrganizationsTestHost.MintAsync(client, userId);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add(OgPrincipalStartupFilter.Header, userId);
        return await client.SendAsync(request);
    }
}
