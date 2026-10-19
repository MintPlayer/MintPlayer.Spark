using MintPlayer.Spark.Abstractions;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MintPlayer.Spark.Commerce.Endpoints;
using MintPlayer.Spark.Commerce.Contracts;
using MintPlayer.Spark.Commerce.Models;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Services;

/// <summary>
/// Seller exports of buyers and attendees (GB14). In connected mode the seller is the controller for its own sales
/// (GDPR Art. 4(7)); the platform processes for it (Art. 28) and keeps an audit entry of every export (Art. 5(2)).
/// </summary>
internal sealed class SellerExports(IServiceProvider services, CommerceAccess access, IDocumentStore store, ICommerceAudit audit)
{
    /// <summary>The CSV, or null when the caller may not export or no source has that kind.</summary>
    public async Task<string?> ExportCsvAsync(string organizationId, string kind, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        if (!await access.MayExportAsync(organizationId, ct)) return null;
        var source = services.GetServices<ISellerExportSource>().FirstOrDefault(s => string.Equals(s.Kind, kind, StringComparison.Ordinal));
        if (source is null) return null;

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', source.Columns.Select(Escape)));
        var rows = 0;
        await foreach (var row in source.GetRowsAsync(organizationId, from, to, ct))
        {
            csv.AppendLine(string.Join(',', row.Select(Escape)));
            rows++;
        }

        using var session = store.OpenAsyncSession();
        await audit.RecordAsync(session, new CommerceAuditEntry
        {
            Action = CommerceAuditActions.SellerExport,
            SubjectId = SellerProfile.IdFor(organizationId),
            OrganizationId = organizationId,
            Reason = kind,
            Data = new Dictionary<string, string>
            {
                ["kind"] = kind,
                ["rows"] = rows.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["from"] = from?.ToString("O") ?? "",
                ["to"] = to?.ToString("O") ?? "",
            },
        }, ct);
        await session.SaveChangesAsync(ct);
        return csv.ToString();
    }

    /// <summary>RFC 4180 quoting, plus a leading quote for cells a spreadsheet would run as a formula (CSV injection).</summary>
    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}

/// <summary>Saves and publishes legal documents (GA2) for the caller's organization or, for platform admins, the platform.</summary>
internal sealed class LegalDocumentEditor(IDocumentStore store, CommerceAccess access, SparkLegalDocuments legal, TimeProvider clock)
{
    public async Task<CommerceLegalDocumentView?> SaveAsync(LegalDocumentBody body, CancellationToken ct)
    {
        using var session = store.OpenAsyncSession();
        LegalDocument doc;
        if (body.Id is { Length: > 0 } id)
        {
            doc = await session.LoadAsync<LegalDocument>(id, ct) ?? throw new SparkAccessDeniedRefusal();
            if (!await MayEditAsync(doc.OrganizationId, ct)) return null;
            SparkLegalDocuments.EnsureEditable(doc);
        }
        else
        {
            if (!await MayEditAsync(body.OrganizationId, ct)) return null;
            doc = new LegalDocument { Id = "LegalDocuments/" + Guid.NewGuid().ToString("N"), Kind = body.Kind!, OrganizationId = body.OrganizationId };
            await session.StoreAsync(doc, doc.Id, ct);
        }
        if (body.Title is not null) doc.Title = body.Title;
        if (body.Body is not null) doc.Body = body.Body;
        doc.EffectiveFrom = body.EffectiveFrom ?? clock.GetUtcNow();
        if (body.PdfBase64 is { Length: > 0 } pdf)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(pdf); }
            catch (FormatException) { throw new SparkValidationException("The PDF is not valid base64.", "PdfBase64"); }
            if (bytes.Length < 5 || Encoding.ASCII.GetString(bytes, 0, 5) != "%PDF-")
                throw new SparkValidationException("The file is not a PDF.", "PdfBase64");
            session.Advanced.Attachments.Store(doc, LegalDocument.PdfAttachment, new MemoryStream(bytes), "application/pdf");
            doc.HasPdf = true;
        }
        if (body.Publish)
            await legal.PublishAsync(session, doc.Id!, access.UserId, ct);
        await session.SaveChangesAsync(ct);
        return CommerceViews.Legal(doc);
    }

    private async Task<bool> MayEditAsync(string? organizationId, CancellationToken ct)
        => organizationId is null ? await access.IsPlatformAdminAsync(ct) : await access.MayManageSellerAsync(organizationId, ct);
}
