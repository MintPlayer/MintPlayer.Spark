using System.Globalization;
using Microsoft.Extensions.Logging;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Models;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

namespace MintPlayer.Spark.Commerce.Invoicing.Pdf;

/// <summary>
/// GA2 (CRD Art. 8(7): the confirmation on a durable medium): every legal-document version a buyer accepted gets a PDF
/// rendering. A version published without an uploaded PDF is rendered here (title and text, every language it has,
/// version and effective date), attached as Commerce's <c>document.pdf</c> and marked <c>HasPdf</c>, so Checkout's
/// confirmation mail attaches it (and still links <c>/spark/commerce/legal/pdf</c>). Rendered once per version, when an
/// order that accepted it is placed, and again (a no-op when done) right before the confirmation is queued, through
/// <see cref="InvoicingCheckoutMailAttachments"/>. The text itself is never changed: a published version stays as it
/// was (§4.20); the rendering only adds its PDF.
/// </summary>
internal sealed class LegalDocumentPdfs(IDocumentStore store, ILogger<LegalDocumentPdfs> logger)
{
    public async Task EnsureForOrderAsync(CheckoutOrder order, CancellationToken cancellationToken)
    {
        foreach (var documentId in order.Acceptances.Select(a => a.DocumentId).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal))
        {
            try { await EnsureAsync(documentId, cancellationToken); }
            catch (ConcurrencyException) { } // rendered by a concurrent order
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not render the PDF of legal document {Document}.", documentId);
            }
        }
    }

    public async Task EnsureAsync(string documentId, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = Raven.Client.Documents.Session.OptimisticConcurrencyMode.Writes;
        var document = await session.LoadAsync<LegalDocument>(documentId, cancellationToken);
        if (document is null || document.PublishedAt is null || document.HasPdf)
            return;
        var bytes = Render(document);
        session.Advanced.Attachments.Store(document, LegalDocument.PdfAttachment, new MemoryStream(bytes), "application/pdf");
        document.HasPdf = true;
        await session.SaveChangesAsync(cancellationToken);
    }

    internal static byte[] Render(LegalDocument document)
    {
        var pdf = new MinimalPdf();
        var languages = (document.Title?.Tags ?? []).Concat(document.Body?.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (languages.Count == 0)
            languages.Add(TranslatedString.DefaultLanguage);
        var first = true;
        foreach (var language in languages)
        {
            if (!first)
                pdf.NewPage();
            first = false;
            float y = 60;
            pdf.Text(50, y, document.Title?.GetValue(language) ?? document.Kind, 16, bold: true);
            y += 18;
            pdf.Text(50, y, $"{document.Kind} · v{document.Version.ToString(CultureInfo.InvariantCulture)} · {document.EffectiveFrom.UtcDateTime:yyyy-MM-dd} · {language}", 8);
            y += 20;
            foreach (var line in MinimalPdf.Wrap(document.Body?.GetValue(language) ?? "", 495, 10))
            {
                if (y > 790)
                {
                    pdf.NewPage();
                    y = 60;
                }
                pdf.Text(50, y, line, 10);
                y += 13;
            }
        }
        return pdf.Build();
    }
}
