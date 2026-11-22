using Microsoft.Playwright;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.E2E.Tests._Infrastructure;

namespace MintPlayer.Spark.E2E.Tests.Files;

/// <summary>
/// Image and BinaryFile attributes in a real browser, on Fleet's <c>Car.Photos</c> and <c>Car.RegistrationDocument</c>
/// (#700 M10, PRD §8b "Browser E2E").
/// </summary>
/// <remarks>
/// <para>
/// The ng-spark specs prove the renderers against a mocked service, and the SparkTestDriver tests prove the endpoints.
/// Only this proves the loop a user sees: a file set on the real file input is prepared, uploaded and committed by the
/// browser, the save claims it, and the detail page serves it back with the safe headers.
/// </para>
/// <para>
/// SP7: a refused HTML file shows the server's message and its script never runs; nothing the page renders comes from
/// the file's bytes.
/// </para>
/// <para>
/// ⚠️ Kept to one test on purpose: every browser boot spends a dozen <c>/spark</c> calls from the rate-limit bucket all
/// tests in this collection share. The grid-thumbnail case of §8b has no subject on Fleet (Photos is not a query
/// column); the column renderer's variant URL is proven in <c>spark-files.spec.ts</c>.
/// </para>
/// </remarks>
[Collection(FleetE2ECollection.Name)]
public class FileAttributeBrowserTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Files", "Fixtures");

    private readonly FleetE2ECollectionFixture _fixture;
    public FileAttributeBrowserTests(FleetE2ECollectionFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_photo_and_a_pdf_upload_save_and_download_and_a_refused_file_shows_the_server_message()
    {
        using var client = await SparkClientFactory.ForFleetAsAdminAsync(_fixture.Host);
        var created = await client.CreatePersistentObjectAsync(CarFixture.New(CarFixture.RandomLicensePlate("FI"), model: "Files", year: 2024));
        created.Id.Should().NotBeNullOrEmpty($"car create must succeed\n--- Fleet log tail ---\n{_fixture.Host.RecentLog()}");
        var id = created.Id!;

        await using var pages = new PageFactory(_fixture);
        var page = await pages.NewPageAsync();
        var dialogs = new List<string>();
        page.Dialog += (_, dialog) => { dialogs.Add(dialog.Message); _ = dialog.DismissAsync(); };
        await BrowserSignIn.SignInAsync(page, _fixture.Host.FleetUrl, _fixture.Host.AdminEmailAddress, _fixture.Host.AdminPass);

        // ---- 1. SP7: an HTML file is refused with the server's message, and its script never runs.
        await OpenEditAsync(page, id);
        var document = FileField(page, CarFixture.AttributeNames.RegistrationDocument);
        await document.Locator("input[type='file']").SetInputFilesAsync(Path.Combine(Fixtures, "page.html"));
        var refused = document.Locator(".spark-file-row[data-phase='refused'] .spark-file-error, .spark-file-row[data-phase='failed'] .spark-file-error");
        await refused.WaitForAsync(new() { Timeout = 15_000 });
        (await refused.InnerTextAsync()).Should().NotBeNullOrWhiteSpace("the row says why the server refused the file");
        dialogs.Should().BeEmpty("the HTML file's alert(1) must never run");
        await document.Locator(".spark-file-remove").ClickAsync();

        // ---- 2. The demo scanner refuses an infected PDF (EICAR inside a real PDF's bytes).
        var infected = (await File.ReadAllBytesAsync(Path.Combine(Fixtures, "scan.pdf")))
            .Concat("\nX5O!P%@AP[4\\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*\n"u8.ToArray()).ToArray();
        await document.Locator("input[type='file']").SetInputFilesAsync(new FilePayload { Name = "infected.pdf", MimeType = "application/pdf", Buffer = infected });
        await refused.WaitForAsync(new() { Timeout = 15_000 });
        await document.Locator(".spark-file-remove").ClickAsync();

        // ---- 3. A photo and a PDF upload, the save claims them, and the detail page serves them.
        var photos = FileField(page, CarFixture.AttributeNames.Photos);
        await photos.Locator("input[type='file']").SetInputFilesAsync(Path.Combine(Fixtures, "photo.png"));
        await document.Locator("input[type='file']").SetInputFilesAsync(Path.Combine(Fixtures, "scan.pdf"));
        await WaitReadyAsync(photos, "photo.png");
        await WaitReadyAsync(document, "scan.pdf");
        await SaveAndWaitForDetailAsync(page, "the save with two committed uploads must succeed");

        var image = page.Locator("spark-file-detail img.spark-file-image");
        await image.First.WaitForAsync(new() { Timeout = 15_000 });
        var download = page.Locator("spark-file-detail a.spark-file-download", new() { HasText = "scan.pdf" });
        await download.WaitForAsync(new() { Timeout = 15_000 });

        var pdf = await page.APIRequest.GetAsync((await download.GetAttributeAsync("href"))!);
        pdf.Status.Should().Be(200, "the owner downloads the stored PDF");
        pdf.Headers["content-disposition"].Should().StartWith("attachment", "a BinaryFile is never rendered inline");
        pdf.Headers["x-content-type-options"].Should().Be("nosniff");
        (await pdf.BodyAsync()).Should().Equal(await File.ReadAllBytesAsync(Path.Combine(Fixtures, "scan.pdf")));

        var photo = await page.APIRequest.GetAsync((await image.First.GetAttributeAsync("src"))!);
        photo.Status.Should().Be(200, "the image the detail page shows is served");
        photo.Headers["content-type"].Should().StartWith("image/");
        photo.Headers["x-content-type-options"].Should().Be("nosniff");

        // ---- 4. Replace the PDF, then clear it.
        await OpenEditAsync(page, id);
        document = FileField(page, CarFixture.AttributeNames.RegistrationDocument);
        await document.Locator(".spark-file-row[data-file='scan.pdf']").WaitForAsync(new() { Timeout = 15_000 });
        await document.Locator(".spark-file-remove").ClickAsync();
        await document.Locator("input[type='file']").SetInputFilesAsync(new FilePayload
        {
            Name = "renewed.pdf", MimeType = "application/pdf", Buffer = await File.ReadAllBytesAsync(Path.Combine(Fixtures, "scan.pdf")),
        });
        await WaitReadyAsync(document, "renewed.pdf");
        await SaveAndWaitForDetailAsync(page, "the save replacing the PDF must succeed");
        await page.Locator("spark-file-detail a.spark-file-download", new() { HasText = "renewed.pdf" }).WaitForAsync(new() { Timeout = 15_000 });
        FileName(await ReloadAsync(client, id), CarFixture.AttributeNames.RegistrationDocument).Should().Be("renewed.pdf");

        await OpenEditAsync(page, id);
        document = FileField(page, CarFixture.AttributeNames.RegistrationDocument);
        await document.Locator(".spark-file-row[data-file='renewed.pdf']").WaitForAsync(new() { Timeout = 15_000 });
        await document.Locator(".spark-file-remove").ClickAsync();
        await SaveAndWaitForDetailAsync(page, "the save clearing the PDF must succeed");
        var cleared = await ReloadAsync(client, id);
        FileName(cleared, CarFixture.AttributeNames.RegistrationDocument).Should().BeNull("the cleared file is gone");
        cleared.Attributes.First(a => a.Name == CarFixture.AttributeNames.Photos).Value?.ToString().Should().Contain("photo.png", "the photo is untouched");

        dialogs.Should().BeEmpty("no dialog opened at any point");
    }

    // ---------------------------------------------------------------------------------

    /// <summary>The form row of one attribute: the label is the only per-attribute marker the form renders.</summary>
    private static ILocator FileField(IPage page, string attribute)
        => page.Locator($"label[for='{attribute}']").Locator("xpath=..").Locator("spark-file-edit");

    private static async Task OpenEditAsync(IPage page, string id)
    {
        await page.GotoAsync($"/po/{CarFixture.TypeId}/{Uri.EscapeDataString(id)}/edit");
        await page.Locator("input#Model").WaitForAsync(new() { Timeout = 15_000 });
        await FileField(page, CarFixture.AttributeNames.Photos).WaitForAsync(new() { Timeout = 15_000 });
    }

    private static async Task WaitReadyAsync(ILocator field, string fileName)
        => await field.Locator($".spark-file-row[data-file='{fileName}'][data-phase='ready']").WaitForAsync(new() { Timeout = 15_000 });

    private static async Task SaveAndWaitForDetailAsync(IPage page, string because)
    {
        await page.Locator("button[type='submit']").ClickAsync();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (page.Url.Contains("/edit", StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline)
                false.Should().BeTrue($"{because}, but the page is still at {page.Url}");
            await Task.Delay(100);
        }
    }

    private static async Task<PersistentObject> ReloadAsync(MintPlayer.Spark.Client.SparkClient client, string id)
        => await client.GetPersistentObjectAsync(CarFixture.TypeId, id)
            ?? throw new InvalidOperationException($"Car {id} not re-fetchable");

    private static string? FileName(PersistentObject po, string name)
    {
        var value = po.Attributes.First(a => a.Name == name).Value;
        if (value is null) return null;
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(value));
        return json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
            && json.RootElement.TryGetProperty("fileName", out var fileName) ? fileName.GetString() : null;
    }
}
