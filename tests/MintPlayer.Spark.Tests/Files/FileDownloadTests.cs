using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Files.Storage;
using MintPlayer.Spark.Testing;
using MintPlayer.Spark.Tests.Organizations;
using MintPlayer.Spark.Tests._Infrastructure;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Tests.Files;

/// <summary>
/// #700 M6, downloads (D14, D15, §3.5): a download runs the object's read path and then serves with the safe headers (or
/// redirects, for a store with presigned reads); a read response embeds a URL for each file the caller may read, and only
/// for those; a content ticket serves its one object until it is changed or expires.
/// </summary>
public class FileDownloadTests : FilesTestHost
{
    private const string Alice = "users/alice";

    [Fact]
    public async Task A_download_serves_the_bytes_with_the_safe_headers()
    {
        var id = await CreateCarWithAsync(Alice, "Document", "scan.pdf");
        var file = (await LoadCarAsync(id)).Document!;

        using var response = await GetAsync(DownloadUrl(id, "Document", file.FileId), Alice);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(FileSnifferTests.Fixture("scan.pdf"));
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.GetValues("Content-Security-Policy").Should().Equal(SparkFileServing.ContentSecurityPolicy);
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileNameStar.Should().Be("scan.pdf");
        NeverCached(response);
        response.Headers.ETag!.Tag.Should().Be("\"" + file.Sha256 + "\"");
    }

    [Fact]
    public async Task A_download_answers_a_range_and_a_matching_etag()
    {
        var id = await CreateCarWithAsync(Alice, "Document", "scan.pdf");
        var file = (await LoadCarAsync(id)).Document!;
        var url = DownloadUrl(id, "Document", file.FileId);

        using (var range = await GetAsync(url, Alice, ("Range", "bytes=0-9")))
        {
            range.StatusCode.Should().Be(HttpStatusCode.PartialContent);
            (await range.Content.ReadAsByteArrayAsync()).Should().Equal(FileSnifferTests.Fixture("scan.pdf")[..10]);
        }
        using (var cached = await GetAsync(url, Alice, ("If-None-Match", "\"" + file.Sha256 + "\"")))
            cached.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task A_load_embeds_a_download_url_for_a_file_and_a_content_url_for_an_image()
    {
        var id = await CreateCarWithAsync(Alice, "Photos", "photo.png");
        var (token, _) = await StageAsync(Alice, "Document", "scan.pdf", id: id);
        (await SaveCarAsync(Alice, id, ("Document", new { uploadToken = token }))).Status.Should().Be(HttpStatusCode.OK);
        var car = await LoadCarAsync(id);

        var loaded = await LoadAsync(id, Alice);

        Value(loaded, "Document").GetProperty("url").GetString().Should().Be(DownloadUrl(id, "Document", car.Document!.FileId));
        var photoUrl = Value(loaded, "Photos")[0].GetProperty("url").GetString()!;
        photoUrl.Should().StartWith("/spark/files/content/");

        // The ticket is the credential: no cookie, no user.
        using var client = Factory.CreateClient();
        using var photo = await client.GetAsync(photoUrl);
        photo.StatusCode.Should().Be(HttpStatusCode.OK);
        (await photo.Content.ReadAsByteArrayAsync()).Should().Equal(await CarAttachmentBytesAsync(id, car.Photos[0].FileId));
        photo.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        photo.Content.Headers.ContentDisposition!.DispositionType.Should().Be("inline", "a raster image of an Image attribute shows inline");
        photo.Headers.GetValues("Content-Security-Policy").Should().Equal(SparkFileServing.ContentSecurityPolicy);

        // The stored document never holds a URL.
        var raw = await LoadRawAsync(id);
        raw.Should().NotContain("\"url\"").And.NotContain("Url");
    }

    [Fact]
    public async Task A_query_cell_embeds_the_same_urls_as_a_load()
    {
        var queryId = Guid.NewGuid();
        ExtraModel = model => model.Queries = [new SparkQuery { Id = queryId, Name = "FiCars", Source = "Database.Cars", EntityType = "FiCar" }];
        var id = await CreateCarWithAsync(Alice, "Photos", "photo.png");
        var (token, _) = await StageAsync(Alice, "Document", "scan.pdf", id: id);
        (await SaveCarAsync(Alice, id, ("Document", new { uploadToken = token }))).Status.Should().Be(HttpStatusCode.OK);
        var car = await LoadCarAsync(id);

        var (status, result) = await PostJsonAsync("/spark/queries/execute", Wire.Query(queryId), Alice);

        status.Should().Be(HttpStatusCode.OK, result.ToString());
        var cells = result.GetProperty("items")[0].GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("key").GetString()!, v => v.GetProperty("value"));
        cells["Document"].GetProperty("url").GetString().Should().Be(DownloadUrl(id, "Document", car.Document!.FileId));
        var photoUrl = cells["Photos"][0].GetProperty("url").GetString()!;
        photoUrl.Should().StartWith("/spark/files/content/");
        cells["Name"].ValueKind.Should().NotBe(JsonValueKind.Object, "a cell without files is not touched");
        result.ToString().Should().NotContain("fileOwner", "the owner tag is never written");

        using var client = Factory.CreateClient();
        using var photo = await client.GetAsync(photoUrl);
        (await photo.Content.ReadAsByteArrayAsync()).Should().Equal(await CarAttachmentBytesAsync(id, car.Photos[0].FileId));
    }

    [Fact]
    public async Task The_type_tells_the_client_which_file_attributes_are_uploaded_and_which_hold_a_url()
    {
        // Name as a string Image: a URL, nothing to upload.
        ExtraModel = model => model.PersistentObject.Attributes.Single(a => a.Name == "Name").DataType = "Image";

        using var response = await GetAsync($"/spark/types/{CarTypeId}", Alice);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var attributes = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("attributes")
            .EnumerateArray().ToDictionary(a => a.GetProperty("name").GetString()!);
        attributes["Photos"].GetProperty("storesFile").GetBoolean().Should().BeTrue();
        attributes["Document"].GetProperty("storesFile").GetBoolean().Should().BeTrue();
        attributes["Name"].TryGetProperty("storesFile", out _).Should().BeFalse("a string holds a URL");
        Factory.GetService<MintPlayer.Spark.Services.IModelLoader>().GetEntityType(CarTypeId)!.Attributes.Should().OnlyContain(a => !a.StoresFile, "the loader's shared definitions are never marked");
    }

    [Fact]
    public async Task A_changed_ticket_serves_nothing()
    {
        var id = await CreateCarWithAsync(Alice, "Photos", "photo.png");
        var url = Value(await LoadAsync(id, Alice), "Photos")[0].GetProperty("url").GetString()!;
        var changed = url[..^3] + (url[^3] == 'A' ? 'B' : 'A') + url[^2..];

        using var client = Factory.CreateClient();
        (await client.GetAsync(changed)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/spark/files/content/not-a-ticket")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_ticket_expires()
    {
        ConfigureFiles = files => files.ConfigureOptions(o => o.EmbeddedUrlLifetime = TimeSpan.FromSeconds(1));
        var id = await CreateCarWithAsync(Alice, "Photos", "photo.png");
        var url = Value(await LoadAsync(id, Alice), "Photos")[0].GetProperty("url").GetString()!;

        await Task.Delay(TimeSpan.FromSeconds(2));

        using var client = Factory.CreateClient();
        (await client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_attribute_the_caller_may_not_read_has_no_url_and_no_download()
    {
        // Signed-in users read the type but not the attribute. Granting the type to anonymous visitors would need a row rule,
        // so the anonymous half of the grant is dropped from the file.
        var json = System.Text.Json.Nodes.JsonNode.Parse(SparkTestSecurity.Empty.Granting("QueryReadEditNew/FiCar").Denying("QueryRead/FiCar/Document").Build())!;
        var rights = json["Rights"]!.AsArray();
        foreach (var grant in rights.Where(r => r!["GroupId"]!.GetValue<string>() == SparkTestSecurity.AnonymousGroupId.ToString() && r["IsDenied"]?.GetValue<bool>() != true).ToList())
            rights.Remove(grant);
        Security = SparkTestSecurity.FromJson(json.ToJsonString());
        var id = await SeedCarAsync();
        var file = await PutDocumentAsync(id, SparkFileScanStatus.Clean);

        var loaded = await LoadAsync(id, Alice);
        loaded.GetProperty("attributes").EnumerateArray().Select(a => a.GetProperty("name").GetString()).Should().NotContain("Document");

        using var denied = await GetAsync(DownloadUrl(id, "Document", file.FileId), Alice);
        using var unknown = await GetAsync(DownloadUrl(id, "Document", "no-such-file"), Alice);
        denied.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.StatusCode.Should().Be(denied.StatusCode);
        (await denied.Content.ReadAsStringAsync()).Should().Be(await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_hidden_row_refuses_the_download_as_the_read_path_refuses_the_load()
    {
        Security = MintPlayer.Spark.Testing.SparkTestSecurity.Permissive.Without("FiCar");
        var id = await SeedCarAsync();
        var file = await PutDocumentAsync(id, SparkFileScanStatus.Clean);

        foreach (var user in new[] { Alice, null })
        {
            using var download = await GetAsync(DownloadUrl(id, "Document", file.FileId), user);
            var (loadStatus, _) = await PostJsonAsync("/spark/po/load", Wire.Typed(CarTypeId, id: id), user);
            download.StatusCode.Should().Be(loadStatus, $"the download refuses {(user ?? "an anonymous caller")} exactly as the load does");
            download.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);
        }
    }

    [Theory]
    [InlineData(SparkFileScanStatus.Pending)]
    [InlineData(SparkFileScanStatus.Infected)]
    [InlineData(SparkFileScanStatus.ScanFailed)]
    public async Task A_file_that_is_not_clean_is_neither_linked_nor_served(SparkFileScanStatus status)
    {
        var id = await SeedCarAsync();
        var file = await PutDocumentAsync(id, status);

        Value(await LoadAsync(id, Alice), "Document").TryGetProperty("url", out _).Should().BeFalse();
        using var download = await GetAsync(DownloadUrl(id, "Document", file.FileId), Alice);
        download.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_store_with_presigned_reads_gets_a_redirect_that_is_not_cached()
    {
        ConfigureFiles = files => files.UseStore(sp => new PresigningStore(new RavenAttachmentFileStore(sp.GetRequiredService<IDocumentStore>())));
        var id = await CreateCarWithAsync(Alice, "Document", "scan.pdf");
        var file = (await LoadCarAsync(id)).Document!;

        using var response = await GetAsync(DownloadUrl(id, "Document", file.FileId), Alice);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.AbsoluteUri;
        location.Should().StartWith("https://files.example.test/" + Uri.EscapeDataString(id + "/" + file.FileId));
        location.Should().Contain("disposition=" + Uri.EscapeDataString(SparkFileServing.ContentDisposition("scan.pdf", inline: false)));
        NeverCached(response);
    }

    /// <summary><c>private, no-store</c>, in whatever order the client re-serialises it.</summary>
    internal static void NeverCached(HttpResponseMessage response)
    {
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.NoStore.Should().BeTrue();
    }

    [Fact]
    public void Only_a_raster_image_of_an_image_attribute_is_inline()
    {
        SparkFileServing.IsInline(new SparkImage { ContentType = "image/png" }, null).Should().BeTrue();
        SparkFileServing.IsInline(new SparkFile { ContentType = "image/png" }, null).Should().BeFalse("a BinaryFile that happens to be a PNG still downloads");
        SparkFileServing.IsInline(new SparkImage { ContentType = "image/svg+xml" }, null).Should().BeFalse();
        SparkFileServing.IsInline(new SparkImage { ContentType = "application/pdf" }, null).Should().BeFalse();
        SparkFileServing.ContentDisposition("a\"b\r\n.pdf", inline: false).Should().NotContain("\r").And.NotContain("\n");
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static string DownloadUrl(string id, string attribute, string fileId)
        => $"/spark/files/download?type={CarTypeId:D}&id={Uri.EscapeDataString(id)}&attribute={attribute}&file={Uri.EscapeDataString(fileId)}";

    private async Task<HttpResponseMessage> GetAsync(string url, string? userId, params (string Name, string Value)[] headers)
    {
        // A TestServer client never follows a redirect, so a 302 arrives as it is.
        var client = Factory.CreateClient();
        var (cookie, _) = await OrganizationsTestHost.MintAsync(client, userId);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        if (userId is not null)
            request.Headers.Add(OgPrincipalStartupFilter.Header, userId);
        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
        return await client.SendAsync(request);
    }

    private async Task<JsonElement> LoadAsync(string id, string userId)
    {
        var (status, body) = await PostJsonAsync("/spark/po/load", Wire.Typed(CarTypeId, id: id), userId);
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        return body;
    }

    private static JsonElement Value(JsonElement po, string attribute)
        => po.GetProperty("attributes").EnumerateArray().Single(a => a.GetProperty("name").GetString() == attribute).GetProperty("value");

    private async Task<string> LoadRawAsync(string id)
    {
        using var session = Store.OpenAsyncSession();
        using var stream = new MemoryStream();
        await session.Advanced.LoadIntoStreamAsync([id], stream);
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>A document file written straight into the entity, with the given scan state and an attachment holding its bytes.</summary>
    private async Task<SparkFile> PutDocumentAsync(string id, SparkFileScanStatus status)
    {
        var file = new SparkFile
        {
            FileId = "f" + Guid.NewGuid().ToString("N"),
            FileName = "scan.pdf",
            ContentType = "application/pdf",
            Length = FileSnifferTests.Fixture("scan.pdf").Length,
            Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(FileSnifferTests.Fixture("scan.pdf"))),
            ScanStatus = status,
            UploadedAt = DateTimeOffset.UtcNow,
        };
        using var session = Store.OpenAsyncSession();
        var car = await session.LoadAsync<FiCar>(id);
        car.Document = file;
        session.Advanced.Attachments.Store(id, file.FileId, new MemoryStream(FileSnifferTests.Fixture("scan.pdf")), "application/pdf");
        await session.SaveChangesAsync();
        return file;
    }

    /// <summary>The RavenDB store, presigning reads as an external store does: the URL spells out what it was asked.</summary>
    private sealed class PresigningStore(RavenAttachmentFileStore inner) : ISessionFileStore
    {
        public Uri? CreateReadUrl(string key, SparkFileReadRequest request)
            => new($"https://files.example.test/{Uri.EscapeDataString(key)}?type={Uri.EscapeDataString(request.ContentType)}&disposition={Uri.EscapeDataString(request.ContentDisposition)}&expires={request.Expires.ToUnixTimeSeconds()}");

        public Task<SparkFileUploadTarget?> CreateUploadTargetAsync(string key, long maxBytes, DateTimeOffset expires, CancellationToken cancellationToken) => inner.CreateUploadTargetAsync(key, maxBytes, expires, cancellationToken);
        public Task WriteAsync(string key, Stream content, string? contentType, CancellationToken cancellationToken) => inner.WriteAsync(key, content, contentType, cancellationToken);
        public Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken) => inner.OpenReadAsync(key, cancellationToken);
        public Task DeleteAsync(string key, CancellationToken cancellationToken) => inner.DeleteAsync(key, cancellationToken);
        public Task CopyAsync(string sourceKey, string destinationKey, CancellationToken cancellationToken) => inner.CopyAsync(sourceKey, destinationKey, cancellationToken);
        public IAsyncEnumerable<SparkStoredObject> ListAsync(string prefix, CancellationToken cancellationToken) => inner.ListAsync(prefix, cancellationToken);
        public string StagingKey(string token) => inner.StagingKey(token);
        public string StagingVariantKey(string token, string variant) => inner.StagingVariantKey(token, variant);
        public string EntityKey(string entityId, string fileId) => inner.EntityKey(entityId, fileId);
        public string EntityPrefix(string entityId) => inner.EntityPrefix(entityId);
        public Task EraseAsync(string entityId, string fileId, CancellationToken cancellationToken) => inner.EraseAsync(entityId, fileId, cancellationToken);
        public Task AttachAsync(SparkFileTransfer transfer, CancellationToken cancellationToken) => inner.AttachAsync(transfer, cancellationToken);
        public Task DetachAsync(SparkFileTransfer transfer, CancellationToken cancellationToken) => inner.DetachAsync(transfer, cancellationToken);
    }
}
