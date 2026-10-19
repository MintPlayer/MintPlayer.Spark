using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Commerce.Checkout;
using MintPlayer.Spark.Commerce.Invoicing.Contracts;
using MintPlayer.Spark.Commerce.Invoicing.EInvoicing;
using MintPlayer.Spark.Commerce.Invoicing.Exports;
using MintPlayer.Spark.Commerce.Invoicing.Models;
using MintPlayer.Spark.Commerce.Invoicing.Pdf;
using MintPlayer.Spark.Commerce.Invoicing.Reference;
using MintPlayer.Spark.Commerce.Invoicing.Services;
using MintPlayer.Spark.Commerce.Invoicing.Vat;
using MintPlayer.Spark.Endpoints;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Invoicing.Endpoints;

internal class InvoicingGroup : IEndpointGroup
{
    public static string Prefix => "/spark/commerce/invoicing";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) { }
}

internal static class InvoicingResults
{
    public static SparkInvoiceSummary Summary(Invoice i, InvoiceLinks links) => new(
        i.Id!, i.Number, (SparkInvoiceKind)(int)i.Kind, i.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        i.TotalGross.Amount, i.Currency, i.CreditedInvoiceNumber, links.PdfLink(i.Id!));

    public static int? Int(HttpContext http, string name)
        => int.TryParse(http.Request.Query[name].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static DateOnly? Date(HttpContext http, string name)
        => DateOnly.TryParseExact(http.Request.Query[name].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static IResult File(InvoicingExportFile file) => Results.File(file.Content, file.ContentType, file.FileName);
}

/// <summary>
/// <c>GET /spark/commerce/invoicing/documents?number=</c>: the documents of an order. The buyer sees their own order's
/// (a guest by the visitor that placed it, through Checkout's own-order rule); the seller and the platform through the
/// row rule.
/// </summary>
[MemberOf<InvoicingGroup>]
internal sealed partial class GetOrderDocuments : IGetEndpoint
{
    public static string Path => "/documents";

    [Inject] private readonly ISparkCheckout checkout;
    [Inject] private readonly ISparkInvoicing invoicing;
    [Inject] private readonly InvoicingAccess access;
    [Inject] private readonly InvoiceLinks links;
    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (httpContext.Request.Query["number"].ToString() is not { Length: > 0 and <= 64 } number)
            return addOn.Refusal(httpContext);
        var order = await checkout.GetOwnOrderAsync(number, httpContext.RequestAborted);
        IReadOnlyList<Invoice> documents;
        if (order?.Id is { } orderId)
            documents = await invoicing.GetForOrderAsync(orderId, httpContext.RequestAborted);
        else
        {
            // Not the caller's own order: a seller or the platform may still see its documents (row rule per document).
            documents = [];
            if (access.CallerId is null)
                return addOn.Refusal(httpContext);
            using var session = store.OpenAsyncSession();
            var all = await session.Query<Invoice>().Where(i => i.OrderNumber == number).Take(256).ToListAsync(httpContext.RequestAborted);
            var visible = new List<Invoice>();
            foreach (var i in all)
            {
                if (await access.MaySeeAsync(i, httpContext.RequestAborted))
                    visible.Add(i);
            }
            if (visible.Count == 0)
                return addOn.Refusal(httpContext);
            documents = visible;
        }
        return addOn.Envelope(documents.Select(i => InvoicingResults.Summary(i, links)).ToList(), StatusCodes.Status200OK);
    }
}

/// <summary>
/// <c>GET /spark/commerce/invoicing/pdf?token=</c> (a signed link from the mail, for guests) or <c>?id=</c> (signed in,
/// under the row rule): the document as PDF.
/// </summary>
[MemberOf<InvoicingGroup>]
internal sealed partial class GetInvoicePdf : IGetEndpoint
{
    public static string Path => "/pdf";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly InvoiceLinks links;
    [Inject] private readonly InvoicingAccess access;
    [Inject] private readonly IInvoicePdfRenderer renderer;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var fromToken = links.Read(httpContext.Request.Query["token"].ToString());
        var id = fromToken ?? (httpContext.Request.Query["id"].ToString() is { Length: > 0 and <= 128 } raw ? raw : null);
        if (id is null || !id.StartsWith("Invoices/", StringComparison.Ordinal))
            return addOn.Refusal(httpContext);
        using var session = store.OpenAsyncSession();
        var invoice = await session.LoadAsync<Invoice>(id, httpContext.RequestAborted);
        if (invoice is null || (fromToken is null && !await access.MaySeeAsync(invoice, httpContext.RequestAborted)))
            return addOn.Refusal(httpContext);
        httpContext.Response.Headers.CacheControl = "private, no-store";
        return Results.File(renderer.Render(invoice), "application/pdf", invoice.Number + ".pdf");
    }
}

/// <summary><c>GET /spark/commerce/invoicing/ubl?id=</c>: the EN 16931 UBL of a document (seller and platform only).</summary>
[MemberOf<InvoicingGroup>]
internal sealed partial class GetInvoiceUbl : IGetEndpoint
{
    public static string Path => "/ubl";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.RequireAuthorization();

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly InvoicingAccess access;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (httpContext.Request.Query["id"].ToString() is not { Length: > 0 and <= 128 } id || !id.StartsWith("Invoices/", StringComparison.Ordinal))
            return addOn.Refusal(httpContext);
        using var session = store.OpenAsyncSession();
        var invoice = await session.LoadAsync<Invoice>(id, httpContext.RequestAborted);
        if (invoice is null || !await access.MayExportAsync(invoice.IssuerKey, httpContext.RequestAborted))
            return addOn.Refusal(httpContext);
        return Results.File(UblWriter.Write(invoice), "application/xml", invoice.Number + ".xml");
    }
}

/// <summary><c>POST /spark/commerce/invoicing/billing</c>: the buyer's billing details for their own order, before it is invoiced.</summary>
[MemberOf<InvoicingGroup>]
internal sealed partial class SaveBillingDetails : IPostEndpoint<SparkBillingDetailsRequest>
{
    public static string Path => "/billing";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkCheckout checkout;
    [Inject] private readonly ISparkInvoicing invoicing;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(SparkBillingDetailsRequest request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        if (request?.OrderNumber is not { Length: > 0 and <= 64 } number || await checkout.GetOwnOrderAsync(number, cancellationToken) is not { Id: { } orderId })
            return addOn.Refusal(http);
        if (request.VatId is { Length: > 0 } vat && VatIdFormat.Parse(vat) is null)
            return addOn.Envelope(new SparkBillingDetailsResponse(SparkBillingDetailsOutcome.InvalidVatId), StatusCodes.Status200OK);
        static string? Bounded(string? s, int max) => s is { Length: > 0 } v ? (v.Length <= max ? v.Trim() : v[..max].Trim()) : null;
        var saved = await invoicing.SaveBillingDetailsAsync(new InvoiceBillingDetails
        {
            OrderId = orderId,
            InvoiceRequested = request.InvoiceRequested,
            Name = Bounded(request.Name, 200),
            VatId = Bounded(request.VatId, 32),
            CompanyId = Bounded(request.CompanyId, 32),
            Country = request.Country is { Length: 2 } c ? c.ToUpperInvariant() : null,
            AddressLines = (request.AddressLines ?? []).Take(3).Select(l => Bounded(l, 200)).OfType<string>().ToList(),
            PostalCode = Bounded(request.PostalCode, 16),
            City = Bounded(request.City, 100),
            PeppolId = PeppolParticipant.TryParse(request.PeppolId, out var p) ? p.ToString() : null,
            Language = request.Language is { Length: >= 2 and <= 10 } lang ? lang : null,
        }, cancellationToken);
        return addOn.Envelope(new SparkBillingDetailsResponse(saved ? SparkBillingDetailsOutcome.Saved : SparkBillingDetailsOutcome.AlreadyInvoiced), StatusCodes.Status200OK);
    }
}

/// <summary>
/// <c>GET /spark/commerce/invoicing/vat-rate-suggestions?country=</c> (GC10): the Commission's TEDB rates beside the
/// host's configured ones. Suggestions only (§4.8): nothing here changes a rate. Finance of any seller or the platform.
/// </summary>
[MemberOf<InvoicingGroup>]
internal sealed partial class GetVatRateSuggestions : IGetEndpoint
{
    public static string Path => "/vat-rate-suggestions";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.RequireAuthorization();

    [Inject] private readonly IVatReferenceSource reference;
    [Inject] private readonly InvoicingAccess access;
    [Inject] private readonly IOptionsMonitor<SparkCommerceInvoicingOptions> options;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var (platformWide, sellers) = await access.ScopeAsync(httpContext.RequestAborted);
        if (!platformWide && sellers.Count == 0)
            return addOn.Refusal(httpContext);
        var country = httpContext.Request.Query["country"].ToString() is { Length: 2 } c ? c.ToUpperInvariant() : null;
        var categories = options.CurrentValue.VatCategories;
        var rows = await reference.GetRateSuggestionsAsync(country, httpContext.RequestAborted);
        return addOn.Envelope(rows.Select(r =>
        {
            decimal? configured = categories.TryGetValue(r.Category, out var category) && category.Rates.TryGetValue(r.Country, out var bp) ? bp / 100m : null;
            return new SparkVatRateSuggestion(r.Country, r.Type, r.RateBasisPoints / 100m, r.Category, r.CategoryDescription,
                r.SituationOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), configured);
        }).ToList(), StatusCodes.Status200OK);
    }
}

/// <summary>
/// <c>GET /spark/commerce/invoicing/exports/oss?issuer=&amp;year=&amp;quarter=[&amp;ioss=true]</c> (GA4, GC8) and
/// <c>?month=</c> for the monthly IOSS return: CSV. The seller's finance or the platform.
/// </summary>
[MemberOf<InvoicingGroup>]
internal sealed partial class ExportOss : IGetEndpoint
{
    public static string Path => "/exports/oss";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.RequireAuthorization();

    [Inject] private readonly InvoicingExports exports;
    [Inject] private readonly InvoicingAccess access;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var issuer = httpContext.Request.Query["issuer"].ToString() is { Length: > 0 and <= 128 } i ? i : InvoiceIssuers.PlatformKey;
        if (InvoicingResults.Int(httpContext, "year") is not { } year || year is < 2021 or > 9999 || !await access.MayExportAsync(issuer, httpContext.RequestAborted))
            return addOn.Refusal(httpContext);
        var ioss = string.Equals(httpContext.Request.Query["ioss"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        if (InvoicingResults.Int(httpContext, "month") is { } month and >= 1 and <= 12)
            return InvoicingResults.File(InvoicingExports.OssCsv(await exports.IossMonthAsync(issuer, year, month, httpContext.RequestAborted), $"ioss-{year}-{month:D2}.csv"));
        if (InvoicingResults.Int(httpContext, "quarter") is not { } quarter || quarter is < 1 or > 4)
            return addOn.Refusal(httpContext);
        var rows = await exports.OssReturnAsync(issuer, year, quarter, ioss, httpContext.RequestAborted);
        return InvoicingResults.File(InvoicingExports.OssCsv(rows, $"{(ioss ? "ioss" : "oss")}-{year}-Q{quarter}.csv"));
    }
}

/// <summary><c>GET /spark/commerce/invoicing/exports/dac7?year=&amp;format=xml|csv</c> (GA6): the platform's yearly report.</summary>
[MemberOf<InvoicingGroup>]
internal sealed partial class ExportDac7 : IGetEndpoint
{
    public static string Path => "/exports/dac7";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.RequireAuthorization();

    [Inject] private readonly InvoicingExports exports;
    [Inject] private readonly InvoicingAccess access;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (InvoicingResults.Int(httpContext, "year") is not { } year || year is < 2023 or > 9999 || !await access.IsPlatformAsync(httpContext.RequestAborted))
            return addOn.Refusal(httpContext);
        var rows = await exports.Dac7Async(year, httpContext.RequestAborted);
        return InvoicingResults.File(httpContext.Request.Query["format"].ToString() == "xml" ? exports.Dac7Xml(rows, year) : InvoicingExports.Dac7Csv(rows, year));
    }
}

/// <summary><c>GET /spark/commerce/invoicing/exports/accounting?issuer=&amp;from=yyyy-MM-dd&amp;to=yyyy-MM-dd</c> (GB4): a zip.</summary>
[MemberOf<InvoicingGroup>]
internal sealed partial class ExportAccounting : IGetEndpoint
{
    public static string Path => "/exports/accounting";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.RequireAuthorization();

    [Inject] private readonly InvoicingExports exports;
    [Inject] private readonly InvoicingAccess access;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var issuer = httpContext.Request.Query["issuer"].ToString() is { Length: > 0 and <= 128 } i ? i : InvoiceIssuers.PlatformKey;
        if (InvoicingResults.Date(httpContext, "from") is not { } from || InvoicingResults.Date(httpContext, "to") is not { } to
            || to <= from || to.DayNumber - from.DayNumber > 400 || !await access.MayExportAsync(issuer, httpContext.RequestAborted))
            return addOn.Refusal(httpContext);
        return InvoicingResults.File(await exports.AccountingAsync(issuer, from, to, httpContext.RequestAborted));
    }
}
