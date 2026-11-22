using System.Net;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Files.Staging;
using MintPlayer.Spark.Tests.Organizations;

namespace MintPlayer.Spark.Tests.Files;

/// <summary>
/// #700 M10: a file inside an AsDetail row. The upload is prepared for the document and the path <c>Services.Invoice</c>
/// (a row has no id, a new row not even a key), claimed by the row the save writes it to, kept only if a row under that path
/// held it when the save began, and removed once no row holds it.
/// </summary>
public class FileAsDetailTests : FilesTestHost
{
    private const string Alice = "users/alice";
    private const string Bob = "users/bob";
    private const string Path = "Services.Invoice";

    [Fact]
    public async Task A_create_with_a_new_row_claims_the_rows_upload_onto_the_document()
    {
        var (token, _) = await StageAsync(Alice, Path, "scan.pdf");

        var (status, body) = await SaveCarRawAsync(Alice, null, Name("Golf"), ServicesAttribute(ServiceRow(null, "Oil", new { uploadToken = token })));

        status.Should().Be(HttpStatusCode.Created, body.ToString());
        var id = body.GetProperty("result").GetProperty("id").GetString()!;
        var invoice = (await LoadCarAsync(id)).Services.Single().Invoice!;
        invoice.FileName.Should().Be("scan.pdf");
        (await CarAttachmentsAsync(id)).Should().Equal(invoice.FileId);
        (await CarAttachmentBytesAsync(id, invoice.FileId)).Should().Equal(FileSnifferTests.Fixture("scan.pdf"));
        (await StagedAsync(token))!.State.Should().Be(SparkFileUploadState.Claimed);
    }

    [Fact]
    public async Task An_edit_keeps_a_rows_file_by_echo_and_takes_nothing_from_the_echo()
    {
        var (id, row, invoice) = await CarWithInvoiceAsync();

        var (status, body) = await SaveCarRawAsync(Alice, id, ServicesAttribute(ServiceRow(row, "Oil and filter", new { fileId = invoice.FileId, fileName = "evil.exe" })));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var service = (await LoadCarAsync(id)).Services.Single();
        service.Description.Should().Be("Oil and filter");
        service.Invoice!.FileName.Should().Be("scan.pdf");
        (await CarAttachmentsAsync(id)).Should().Equal(invoice.FileId);
    }

    [Fact]
    public async Task Dropping_the_row_deletes_its_file()
    {
        var (id, _, invoice) = await CarWithInvoiceAsync();

        var (status, body) = await SaveCarRawAsync(Alice, id, ServicesAttribute());

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        (await LoadCarAsync(id)).Services.Should().BeEmpty();
        (await CarAttachmentBytesAsync(id, invoice.FileId)).Should().BeNull();
    }

    [Fact]
    public async Task Clearing_and_replacing_a_rows_file_deletes_the_old_one()
    {
        var (id, row, old) = await CarWithInvoiceAsync();
        var (token, _) = await StageAsync(Alice, Path, "scan.pdf", asName: "v2.pdf", id: id);

        var (status, body) = await SaveCarRawAsync(Alice, id, ServicesAttribute(ServiceRow(row, "Oil", new { uploadToken = token })));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var invoice = (await LoadCarAsync(id)).Services.Single().Invoice!;
        invoice.FileName.Should().Be("v2.pdf");
        (await CarAttachmentsAsync(id)).Should().Equal(invoice.FileId);
        (await CarAttachmentBytesAsync(id, old.FileId)).Should().BeNull();
    }

    [Fact]
    public async Task A_save_that_does_not_post_the_rows_keeps_their_files()
    {
        var (id, _, invoice) = await CarWithInvoiceAsync();

        var (status, body) = await SaveCarRawAsync(Alice, id, Name("Polo"));

        status.Should().Be(HttpStatusCode.OK, body.ToString());
        (await LoadCarAsync(id)).Services.Single().Invoice!.FileId.Should().Be(invoice.FileId);
        (await CarAttachmentsAsync(id)).Should().Equal(invoice.FileId);
    }

    [Fact]
    public async Task A_file_moves_to_another_row_once_but_two_rows_cannot_keep_it()
    {
        var (id, row, invoice) = await CarWithInvoiceAsync();

        (await SaveCarRawAsync(Alice, id, ServicesAttribute(
                ServiceRow(row, "Oil", new { fileId = invoice.FileId }),
                ServiceRow(null, "Tyres", new { fileId = invoice.FileId }))))
            .Status.Should().Be(HttpStatusCode.BadRequest);

        var (status, body) = await SaveCarRawAsync(Alice, id, ServicesAttribute(
            ServiceRow(row, "Oil", null),
            ServiceRow(null, "Tyres", new { fileId = invoice.FileId })));
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        var services = (await LoadCarAsync(id)).Services;
        services.Single(s => s.Description == "Tyres").Invoice!.FileId.Should().Be(invoice.FileId);
        services.Single(s => s.Description == "Oil").Invoice.Should().BeNull();
        (await CarAttachmentsAsync(id)).Should().Equal(invoice.FileId);
    }

    [Fact]
    public async Task A_row_cannot_keep_another_documents_file_nor_a_top_level_one()
    {
        var (other, _, stolen) = await CarWithInvoiceAsync();
        var golf = await CreateCarWithAsync(Alice, "Document", "scan.pdf");
        var document = (await LoadCarAsync(golf)).Document!;

        (await SaveCarRawAsync(Bob, golf, ServicesAttribute(ServiceRow(null, "Oil", new { fileId = stolen.FileId }))))
            .Status.Should().Be(HttpStatusCode.BadRequest);
        (await SaveCarRawAsync(Alice, golf, ServicesAttribute(ServiceRow(null, "Oil", new { fileId = document.FileId }))))
            .Status.Should().Be(HttpStatusCode.BadRequest);
        (await LoadCarAsync(other)).Services.Single().Invoice!.FileId.Should().Be(stolen.FileId);
    }

    [Fact]
    public async Task A_token_is_bound_to_its_path_and_document()
    {
        var polo = await SeedCarAsync("Polo");
        var (documentToken, _) = await StageAsync(Alice, "Document", "scan.pdf");
        var (rowToken, _) = await StageAsync(Alice, Path, "scan.pdf");
        var (poloToken, _) = await StageAsync(Alice, Path, "scan.pdf", id: polo);

        (await SaveCarRawAsync(Alice, null, ServicesAttribute(ServiceRow(null, "Oil", new { uploadToken = documentToken }))))
            .Status.Should().Be(HttpStatusCode.BadRequest, "a top-level slot is not a row's");
        (await SaveCarAsync(Alice, null, ("Document", new { uploadToken = rowToken })))
            .Status.Should().Be(HttpStatusCode.BadRequest, "a row's slot is not a top-level attribute's");
        (await SaveCarRawAsync(Alice, null, ServicesAttribute(ServiceRow(null, "Oil", new { uploadToken = poloToken }))))
            .Status.Should().Be(HttpStatusCode.BadRequest, "an edit slot is not a create's");
        (await SaveCarRawAsync(Bob, polo, ServicesAttribute(ServiceRow(null, "Oil", new { uploadToken = poloToken }))))
            .Status.Should().Be(HttpStatusCode.BadRequest, "another user's slot");
        (await StagedAsync(poloToken))!.State.Should().Be(SparkFileUploadState.Committed);
    }

    [Theory]
    [InlineData("Services.Description")]
    [InlineData("Services.Invoice.FileName")]
    [InlineData("Name.Invoice")]
    [InlineData("Services")]
    [InlineData(".Invoice")]
    public async Task Prepare_refuses_a_path_that_names_no_row_file_attribute(string path)
    {
        var (status, body) = await PrepareAsync(Alice, path, null, ("scan.pdf", 100));

        status.Should().Be(HttpStatusCode.NotFound, body.ToString());
    }

    [Fact]
    public async Task Prepare_applies_the_row_attributes_fileOptions()
    {
        var (status, _) = await PrepareAsync(Alice, Path, null, ("big.pdf", DocumentMaxBytes + 1));

        status.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_rows_file_downloads_through_the_documents_read_path_and_only_one_it_holds()
    {
        var (id, _, invoice) = await CarWithInvoiceAsync();
        var (other, _, foreign) = await CarWithInvoiceAsync();

        using (var response = await GetAsync(DownloadUrl(id, Path, invoice.FileId), Alice))
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsByteArrayAsync()).Should().Equal(FileSnifferTests.Fixture("scan.pdf"));
            response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        }

        using (var response = await GetAsync(DownloadUrl(id, Path, foreign.FileId), Alice))
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the file is another document's");
        using (var response = await GetAsync(DownloadUrl(id, "Services.Description", invoice.FileId), Alice))
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        other.Should().NotBe(id);
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static object Name(string value) => new { name = "Name", value, isValueChanged = true };

    /// <summary>A car created with one Services row holding an invoice; returns its id, the row's key and the file.</summary>
    private async Task<(string Id, string Row, SparkFile Invoice)> CarWithInvoiceAsync()
    {
        var (token, _) = await StageAsync(Alice, Path, "scan.pdf");
        var (status, body) = await SaveCarRawAsync(Alice, null, Name("Golf"), ServicesAttribute(ServiceRow(null, "Oil", new { uploadToken = token })));
        status.Should().Be(HttpStatusCode.Created, body.ToString());
        var id = body.GetProperty("result").GetProperty("id").GetString()!;
        var service = (await LoadCarAsync(id)).Services.Single();
        return (id, service.Id, service.Invoice!);
    }

    private static string DownloadUrl(string id, string attribute, string fileId)
        => $"/spark/files/download?type={CarTypeId:D}&id={Uri.EscapeDataString(id)}&attribute={Uri.EscapeDataString(attribute)}&file={Uri.EscapeDataString(fileId)}";

    private async Task<HttpResponseMessage> GetAsync(string url, string? userId)
    {
        var client = Factory.CreateClient();
        var (cookie, _) = await OrganizationsTestHost.MintAsync(client, userId);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", cookie);
        if (userId is not null)
            request.Headers.Add(OgPrincipalStartupFilter.Header, userId);
        return await client.SendAsync(request);
    }
}
