using System.Net;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Files;
using MintPlayer.Spark.Files.Staging;

namespace MintPlayer.Spark.Tests.Files;

/// <summary>
/// #700 D3 over HTTP: prepare → upload → commit, with the checks each step makes. The save-path claim is in FileClaimTests.
/// </summary>
public class FileUploadEndpointTests : FilesTestHost
{
    private const string Alice = "users/alice";
    private const string Bob = "users/bob";

    [Fact]
    public async Task A_valid_file_is_staged_and_committed_clean_with_its_sniffed_metadata()
    {
        var (token, committed) = await StageAsync(Alice, "Document", "scan.pdf", asName: "Scan 2026.pdf");

        committed.GetProperty("error").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        committed.GetProperty("contentType").GetString().Should().Be("application/pdf");
        committed.GetProperty("length").GetInt64().Should().Be(FileSnifferTests.Fixture("scan.pdf").Length);
        committed.GetProperty("fileName").GetString().Should().Be("Scan 2026.pdf");

        var staged = await StagedAsync(token);
        staged!.State.Should().Be(SparkFileUploadState.Committed);
        staged.ScanStatus.Should().Be(SparkFileScanStatus.Clean);
        staged.UserId.Should().Be(Alice);
        staged.Sha256.Should().HaveLength(64);
        staged.ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow.AddHours(23));
        (await StagedAttachmentCountAsync(token)).Should().Be(1);
    }

    [Fact]
    public async Task Prepare_returns_one_single_use_slot_per_file()
    {
        var (status, body) = await PrepareAsync(Alice, "Photos", null, ("a.png", 100), ("b.jpg", 200));

        status.Should().Be(HttpStatusCode.OK);
        var files = body.GetProperty("files").EnumerateArray().ToList();
        files.Should().HaveCount(2);
        files.Select(f => f.GetProperty("token").GetString()).Distinct().Should().HaveCount(2);
        files.Should().AllSatisfy(f =>
        {
            f.GetProperty("url").GetString().Should().Be("/spark/files/upload");
            f.GetProperty("direct").GetBoolean().Should().BeFalse();
        });
    }

    [Fact]
    public async Task Anonymous_prepare_is_401()
        => (await PrepareAsync(null, "Document", null, ("a.pdf", 10))).Status.Should().Be(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Prepare_without_the_xsrf_header_is_400()
        => (await PostJsonAsync("/spark/files/prepare", new { objectTypeId = CarTypeId, attribute = "Document", files = new[] { new { fileName = "a.pdf", length = 10 } } }, Alice, withXsrf: false))
            .Status.Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Upload_without_the_xsrf_header_is_400()
    {
        var token = await PrepareOneAsync(Alice, "Document", "scan.pdf");

        using var response = await UploadAsync(Alice, token, "scan.pdf", FileSnifferTests.Fixture("scan.pdf"), withXsrf: false);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StagedAsync(token))!.State.Should().Be(SparkFileUploadState.Prepared);
    }

    [Fact]
    public async Task A_declared_size_over_the_attribute_cap_is_refused_at_prepare()
        => (await PrepareAsync(Alice, "Document", null, ("big.pdf", DocumentMaxBytes + 1))).Status.Should().Be(HttpStatusCode.RequestEntityTooLarge);

    [Fact]
    public async Task More_files_than_maxCount_are_refused()
        => (await PrepareAsync(Alice, "Photos", null, ("1.png", 10), ("2.png", 10), ("3.png", 10), ("4.png", 10))).Status.Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task A_single_file_attribute_takes_one_file()
        => (await PrepareAsync(Alice, "Document", null, ("1.pdf", 10), ("2.pdf", 10))).Status.Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task A_non_file_or_unknown_attribute_is_not_found()
    {
        (await PrepareAsync(Alice, "Name", null, ("a.pdf", 10))).Status.Should().Be(HttpStatusCode.NotFound);
        (await PrepareAsync(Alice, "Nope", null, ("a.pdf", 10))).Status.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The body is cut off while streaming: the lie in prepare (a small declared size) does not let a large body
    /// through, and the server stops reading near the cap instead of buffering 8 MB.
    /// </summary>
    [Fact]
    public async Task An_upload_over_the_slot_cap_is_413_and_read_no_further_than_the_cap()
    {
        var token = await PrepareOneAsync(Alice, "Document", "scan.pdf");
        var body = new CountingContent(8 * 1024 * 1024);

        using var response = await UploadAsync(Alice, token, "scan.pdf", body);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        body.Yielded.Should().BeLessThan(1024 * 1024, "the server must stop reading near the 4 KB cap, not buffer the whole body");
        (await StagedAsync(token))!.State.Should().Be(SparkFileUploadState.Prepared);
        (await StagedAttachmentCountAsync(token)).Should().Be(0);
    }

    [Fact]
    public async Task Another_users_token_cannot_be_uploaded_to_or_committed()
    {
        var token = await PrepareOneAsync(Alice, "Document", "scan.pdf");

        using (var response = await UploadAsync(Bob, token, "scan.pdf", FileSnifferTests.Fixture("scan.pdf")))
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var (status, body) = await CommitAsync(Bob, token);
        status.Should().Be(HttpStatusCode.OK);
        body.GetProperty("files")[0].GetProperty("error").GetString().Should().Be("No such upload.");
        (await StagedAsync(token))!.State.Should().Be(SparkFileUploadState.Prepared);
    }

    [Fact]
    public async Task A_slot_takes_bytes_once()
    {
        var token = await PrepareOneAsync(Alice, "Document", "scan.pdf");
        using (var first = await UploadAsync(Alice, token, "scan.pdf", FileSnifferTests.Fixture("scan.pdf")))
            first.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var second = await UploadAsync(Alice, token, "scan.pdf", FileSnifferTests.Fixture("scan.pdf"));

        second.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("Photos", "page.html", "page.png", "markup")]
    [InlineData("Photos", "drawing.svg", "drawing.png", "markup")]
    [InlineData("Photos", "polyglot.gif", "polyglot.gif", "markup")]
    [InlineData("Photos", "scan.pdf", "scan.pdf", "not accepted")]
    [InlineData("Document", "photo.png", "photo.pdf", "does not match")]
    [InlineData("Photos", "tool.exe", "tool.png", "not a recognised format")]
    public async Task A_refused_file_is_reported_at_commit_and_its_bytes_deleted(string attribute, string fixture, string asName, string error)
    {
        var (token, committed) = await StageAsync(Alice, attribute, fixture, asName);

        committed.GetProperty("error").GetString().Should().Contain(error);
        (await StagedAsync(token))!.State.Should().Be(SparkFileUploadState.Refused);
        (await StagedAttachmentCountAsync(token)).Should().Be(0);
    }

    [Fact]
    public async Task A_commit_without_bytes_is_refused()
    {
        var token = await PrepareOneAsync(Alice, "Document", "scan.pdf");

        var (_, body) = await CommitAsync(Alice, token);

        body.GetProperty("files")[0].GetProperty("error").GetString().Should().Be("No file was uploaded.");
    }

    [Fact]
    public async Task An_upload_handler_refusal_is_returned_to_the_uploader()
    {
        ConfigureFiles = files => files.AddUploadHandler<FiNoMondaysHandler>();

        var (token, committed) = await StageAsync(Alice, "Document", "scan.pdf");

        committed.GetProperty("error").GetString().Should().Be("Documents are refused by policy.");
        (await StagedAsync(token))!.State.Should().Be(SparkFileUploadState.Refused);
    }

    [Fact]
    public async Task Without_the_New_right_a_create_slot_is_forbidden()
    {
        Security = MintPlayer.Spark.Testing.SparkTestSecurity.Permissive.Denying("New/FiCar");

        (await PrepareAsync(Alice, "Document", null, ("a.pdf", 10))).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Without_the_attribute_right_an_edit_slot_is_forbidden()
    {
        Security = MintPlayer.Spark.Testing.SparkTestSecurity.Permissive.Granting("Edit/FiCar").Denying("Edit/FiCar/Document");
        var carId = await SeedCarAsync();

        (await PrepareAsync(Alice, "Document", carId, ("a.pdf", 10))).Status.Should().Be(HttpStatusCode.Forbidden);
        (await PrepareAsync(Alice, "Photos", carId, ("a.png", 10))).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_edit_slot_for_a_missing_object_is_not_found()
        => (await PrepareAsync(Alice, "Document", "FiCars/404-A", ("a.pdf", 10))).Status.Should().Be(HttpStatusCode.NotFound);

    [Fact]
    public async Task Uploads_are_rate_limited_per_user()
    {
        ConfigureFiles = files => files.ConfigureOptions(o => o.UploadsPerMinute = 2);

        (await PrepareAsync(Alice, "Photos", null, ("1.png", 10), ("2.png", 10))).Status.Should().Be(HttpStatusCode.OK);
        (await PrepareAsync(Alice, "Photos", null, ("3.png", 10))).Status.Should().Be(HttpStatusCode.TooManyRequests);
        (await PrepareAsync(Bob, "Photos", null, ("1.png", 10))).Status.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>A body of <c>length</c> zero bytes produced on demand, counting how much the server pulled.</summary>
    private sealed class CountingContent(long length) : HttpContent
    {
        public long Yielded;

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var chunk = new byte[16 * 1024];
            while (Yielded < length)
            {
                await stream.WriteAsync(chunk);
                Interlocked.Add(ref Yielded, chunk.Length);
            }
        }

        protected override bool TryComputeLength(out long size)
        {
            size = 0;
            return false;
        }
    }
}

internal sealed class FiNoMondaysHandler : IFileUploadHandler
{
    public ValueTask<FileUploadDecision> ValidateAsync(FileUploadContext context, CancellationToken cancellationToken)
        => ValueTask.FromResult(context.Attribute.Name == "Document" ? FileUploadDecision.Refuse("Documents are refused by policy.") : FileUploadDecision.Accept);
}
