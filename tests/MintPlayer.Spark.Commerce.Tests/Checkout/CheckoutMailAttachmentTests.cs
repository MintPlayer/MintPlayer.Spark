using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Refunds;
using MintPlayer.Spark.Commerce.Checkout.Services;
using MintPlayer.Spark.Commerce.Invoicing.Pdf;
using MintPlayer.Spark.Commerce.Invoicing.Services;
using MintPlayer.Spark.Commerce.Models;
using MintPlayer.Spark.MailManager;

namespace MintPlayer.Spark.Commerce.Tests.Checkout;

/// <summary>
/// Item 3 — Checkout's durable-medium mails attach files now that MailManager has attachments: the accepted legal
/// documents' PDFs on the confirmation (GA2, CRD Art. 8(7)), the withdrawal receipt as a PDF (GA1, CRD Art. 11a(3)) through
/// Invoicing's <see cref="ICheckoutMailAttachments"/> contributor, and the invoice PDF on the invoice mail.
/// </summary>
public class CheckoutMailAttachmentTests
{
    private static LegalDocument Doc(string id, string kind, int version, bool published, bool hasPdf) => new()
    {
        Id = id, Kind = kind, Version = version, HasPdf = hasPdf,
        PublishedAt = published ? DateTimeOffset.Parse("2026-10-01T00:00:00Z") : null,
    };

    [Fact]
    public void Each_published_accepted_version_is_attached_required_when_it_has_its_PDF_and_optional_while_it_has_not()
    {
        var attachments = CheckoutMails.LegalDocumentAttachments(
        [
            Doc("LegalDocuments/terms", "terms-of-sale", 3, published: true, hasPdf: true),
            Doc("LegalDocuments/withdrawal", "withdrawal-information", 1, published: true, hasPdf: false),
            Doc("LegalDocuments/draft", "privacy-policy", 2, published: false, hasPdf: true),
            null,
        ]).ToList();

        attachments.Should().HaveCount(2, "an unpublished version and a missing document are not attached");
        attachments[0].DocumentId.Should().Be("LegalDocuments/terms");
        attachments[0].AttachmentName.Should().Be(LegalDocument.PdfAttachment);
        attachments[0].FileName.Should().Be("terms-of-sale-v3.pdf");
        attachments[0].ContentType.Should().Be("application/pdf");
        attachments[0].Optional.Should().BeFalse("its PDF exists: the send retries until it can be read");
        attachments[1].Optional.Should().BeTrue("still being rendered: left out if it never appears, the body has the text");
        attachments.Should().OnlyContain(a => a.Content == null, "stored PDFs are read at send, not copied into the queue");
    }

    [Theory]
    [InlineData("terms-of-sale", 3, "terms-of-sale-v3.pdf")]
    [InlineData("../x\r\ny", 1, "x--y-v1.pdf")]
    [InlineData("", 2, "document-v2.pdf")]
    public void The_PDF_file_name_is_plain(string kind, int version, string expected)
        => CheckoutMails.PdfFileName(kind, version).Should().Be(expected);

    [Fact]
    public async Task A_failing_contributor_is_skipped_and_the_others_still_attach()
    {
        var services = new ServiceCollection();
        services.AddScoped<ICheckoutMailAttachments, Failing>();
        services.AddScoped<ICheckoutMailAttachments, Fixed>();
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var mails = new CheckoutMails(scope.ServiceProvider, null!, new StaticOptions(), NullLogger<CheckoutMails>.Instance);

        var result = await mails.ContributedAttachmentsAsync(new CheckoutMailContext(CheckoutMailKind.Confirmation, new CheckoutOrder { Number = "O-1" }, "en"), default);

        result.Select(a => a.FileName).Should().Equal(["fixed.pdf"]);
    }

    [Fact]
    public async Task Invoicing_attaches_the_withdrawal_receipt_as_a_PDF_with_the_date_and_time_of_receipt()
    {
        var order = new CheckoutOrder
        {
            Number = "2026/0042",
            Lines = [new CheckoutOrderLine { LineId = "l1", ItemId = "products/a", Quantity = 2, Title = TranslatedString.Of("nl", "Wandelschoen") }],
        };
        var withdrawal = new CheckoutReturn
        {
            ReturnId = "r1", Source = ReturnSource.Online, Name = "Ann",
            ReceivedAt = DateTimeOffset.Parse("2026-10-19T14:05:09+02:00"),
            Lines = [new CheckoutReturnLine { LineId = "l1", Quantity = 1, Eligible = true }],
        };
        var contributor = new InvoicingCheckoutMailAttachments(null!);

        var files = await contributor.GetAsync(new CheckoutMailContext(CheckoutMailKind.WithdrawalReceipt, order, "nl", withdrawal), default);

        var file = files.Should().ContainSingle().Which;
        file.FileName.Should().Be("withdrawal-receipt-2026-0042.pdf");
        file.ContentType.Should().Be("application/pdf");
        var text = Encoding.Latin1.GetString(file.Content!);
        text.Should().StartWith("%PDF-");
        text.Should().Contain("Ontvangstbevestiging herroeping");
        text.Should().Contain("2026-10-19 14:05:09 UTC+02:00", "the receipt states the date and time of receipt (CRD Art. 11a(3))");
        text.Should().Contain("Wandelschoen");
    }

    [Fact]
    public async Task Invoicing_adds_nothing_itself_to_a_confirmation_without_accepted_documents()
    {
        var contributor = new InvoicingCheckoutMailAttachments(new LegalDocumentPdfs(null!, NullLogger<LegalDocumentPdfs>.Instance));

        var files = await contributor.GetAsync(new CheckoutMailContext(CheckoutMailKind.Confirmation, new CheckoutOrder { Number = "O-1" }, "en"), default);

        files.Should().BeEmpty("Checkout attaches the stored PDFs; Invoicing only renders the missing ones first");
    }

    [Theory]
    [InlineData("INV-2026-0001", "INV-2026-0001.pdf")]
    [InlineData("2026/17", "2026-17.pdf")]
    public void The_invoice_PDF_file_name_is_plain(string number, string expected)
        => InvoiceAfterIssue.PdfFileName(number).Should().Be(expected);

    private sealed class Failing : ICheckoutMailAttachments
    {
        public Task<IReadOnlyList<SparkMailAttachment>> GetAsync(CheckoutMailContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("renderer down");
    }

    private sealed class Fixed : ICheckoutMailAttachments
    {
        public Task<IReadOnlyList<SparkMailAttachment>> GetAsync(CheckoutMailContext context, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SparkMailAttachment>>([SparkMailAttachment.FromBytes("fixed.pdf", "application/pdf", [1])]);
    }

    private sealed class StaticOptions : IOptionsMonitor<SparkCommerceCheckoutOptions>
    {
        public SparkCommerceCheckoutOptions CurrentValue { get; } = new();
        public SparkCommerceCheckoutOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<SparkCommerceCheckoutOptions, string?> listener) => null;
    }
}
