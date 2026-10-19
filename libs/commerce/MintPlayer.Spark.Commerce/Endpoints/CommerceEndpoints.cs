using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Commerce.Contracts;
using MintPlayer.Spark.Commerce.Models;
using MintPlayer.Spark.Commerce.Pricing;
using MintPlayer.Spark.Commerce.Services;
using MintPlayer.Spark.Endpoints;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace MintPlayer.Spark.Commerce.Endpoints;

/// <summary>Public commerce endpoints: anyone, signed in or not (prices, trader pages, ranking parameters, legal
/// documents, DSA notices).</summary>
internal class CommercePublicGroup : IEndpointGroup
{
    public static string Prefix => "/spark/commerce";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) => group.AllowAnonymous();
}

/// <summary>Seller and platform endpoints: signed-in callers; each endpoint checks the caller's organization role or
/// platform right and answers the standard refusal otherwise.</summary>
internal class CommerceSellersGroup : IEndpointGroup
{
    public static string Prefix => "/spark/commerce/sellers";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) => group.RequireAuthorization();
}

/// <summary>Platform endpoints (DSA decisions, transparency, suspensions).</summary>
internal class CommerceAdminGroup : IEndpointGroup
{
    public static string Prefix => "/spark/commerce/admin";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) => group.RequireAuthorization();
}

// ---------------------------------------------------------------- public

/// <summary><c>GET /spark/commerce/price?channel=&amp;amount=&amp;currency=&amp;basis=&amp;vat=&amp;quantity=</c>: the
/// final price of a raw price in a channel (default 4). A calculator: it loads nothing.</summary>
[MemberOf<CommercePublicGroup>]
internal sealed partial class QuotePrice : IGetEndpoint
{
    public static string Path => "/price";

    [Inject] private readonly ICommercePriceDisplay display;
    [Inject] private readonly IOptions<SparkCommerceOptions> options;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var q = httpContext.Request.Query;
        var channel = q["channel"].ToString();
        if (channel.Length == 0 || !options.Value.Channels.ContainsKey(channel)
            || !decimal.TryParse(q["amount"], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
            return Task.FromResult(addOn.Refusal(httpContext));
        var currency = q["currency"].ToString() is { Length: > 0 } c ? c : options.Value.DefaultCurrency;
        if (!Money.IsKnownCurrency(currency))
            return Task.FromResult(addOn.Refusal(httpContext));
        var basis = string.Equals(q["basis"], "net", StringComparison.OrdinalIgnoreCase) ? PriceBasis.Net : PriceBasis.Gross;
        int? vat = int.TryParse(q["vat"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 10_000 ? v : null;
        var quantity = long.TryParse(q["quantity"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 10_000 ? n : 1;
        try
        {
            var price = new Money(amount, currency);
            return Task.FromResult(addOn.Envelope(display.Quote(new FinalPriceRequest(channel, price, basis, vat, quantity)), StatusCodes.Status200OK));
        }
        catch (ArgumentException)
        {
            return Task.FromResult(addOn.Refusal(httpContext));
        }
    }
}

/// <summary><c>GET /spark/commerce/traders?organizationId=</c>: the public trader page (DSA Art. 30(7)).</summary>
[MemberOf<CommercePublicGroup>]
internal sealed partial class GetTraderPage : IGetEndpoint
{
    public static string Path => "/traders";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (httpContext.Request.Query["organizationId"].ToString() is not { Length: > 0 } organizationId)
            return addOn.Refusal(httpContext);
        using var session = store.OpenAsyncSession();
        var profile = await session.LoadAsync<SellerProfile>(SellerProfile.IdFor(organizationId), httpContext.RequestAborted);
        if (profile is null || profile.Status != SellerRegistrationStatus.Approved)
            return addOn.Refusal(httpContext);
        return addOn.Envelope(CommerceViews.TraderPage(profile), StatusCodes.Status200OK);
    }
}

/// <summary><c>GET /spark/commerce/ranking-parameters</c>: the main ranking parameters of every storefront list (GA5),
/// generated from the registered sources.</summary>
[MemberOf<CommercePublicGroup>]
internal sealed partial class GetRankingParameters : IGetEndpoint
{
    public static string Path => "/ranking-parameters";

    [Inject] private readonly IServiceProvider services;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var parameters = services.GetServices<IRankingParameterSource>()
            .SelectMany(s => s.GetParameters())
            .OrderBy(p => p.List, StringComparer.Ordinal).ThenBy(p => p.Order)
            .Select(p => new CommerceRankingParameter(p.List, p.Parameter, p.DescriptionKey, p.Order, p.PaidPlacement))
            .ToArray();
        return Task.FromResult(addOn.Envelope(parameters, StatusCodes.Status200OK));
    }
}

/// <summary><c>GET /spark/commerce/legal?organizationId=</c>: the legal documents in force for a seller (its own, then
/// the platform's); without organizationId the platform's. <c>&amp;id=</c> returns one version (current or past: an
/// order links to the version the buyer accepted).</summary>
[MemberOf<CommercePublicGroup>]
internal sealed partial class GetLegalDocuments : IGetEndpoint
{
    public static string Path => "/legal";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ISparkLegalDocuments legal;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var ct = httpContext.RequestAborted;
        using var session = store.OpenAsyncSession();
        if (httpContext.Request.Query["id"].ToString() is { Length: > 0 } id)
        {
            var doc = await session.LoadAsync<LegalDocument>(id, ct);
            return doc?.PublishedAt is null ? addOn.Refusal(httpContext) : addOn.Envelope(CommerceViews.Legal(doc), StatusCodes.Status200OK);
        }
        var organizationId = httpContext.Request.Query["organizationId"].ToString() is { Length: > 0 } o ? o : null;
        var refs = await legal.GetInForceAsync(organizationId, clock.GetUtcNow(), ct);
        var docs = await session.LoadAsync<LegalDocument>(refs.Select(r => r.Id), ct);
        return addOn.Envelope(docs.Values.Where(d => d is not null).Select(d => CommerceViews.Legal(d!)).ToArray(), StatusCodes.Status200OK);
    }
}

/// <summary><c>GET /spark/commerce/legal/pdf?id=</c>: a published version's PDF (durable medium, CRD Art. 8(7)).</summary>
[MemberOf<CommercePublicGroup>]
internal sealed partial class GetLegalDocumentPdf : IGetEndpoint
{
    public static string Path => "/legal/pdf";

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (httpContext.Request.Query["id"].ToString() is not { Length: > 0 } id)
            return addOn.Refusal(httpContext);
        using var session = store.OpenAsyncSession();
        var doc = await session.LoadAsync<LegalDocument>(id, httpContext.RequestAborted);
        if (doc?.PublishedAt is null || !doc.HasPdf)
            return addOn.Refusal(httpContext);
        using var attachment = await session.Advanced.Attachments.GetAsync(id, LegalDocument.PdfAttachment, httpContext.RequestAborted);
        if (attachment is null)
            return addOn.Refusal(httpContext);
        using var buffer = new MemoryStream();
        await attachment.Stream.CopyToAsync(buffer, httpContext.RequestAborted);
        return Results.File(buffer.ToArray(), "application/pdf", $"{doc.Kind}-v{doc.Version}.pdf");
    }
}

/// <param name="TargetKind"><c>listing</c>, <c>event</c>, <c>review</c>.</param>
public sealed record DsaNoticeBody(string? TargetKind, string? TargetId, string? Reason, string? Explanation, string? Location, string? ContactName, string? ContactEmail, bool GoodFaith);

/// <summary><c>POST /spark/commerce/notices</c>: file a DSA notice (Art. 16), signed in or as a visitor.</summary>
[MemberOf<CommercePublicGroup>]
internal sealed partial class SubmitDsaNotice : IPostEndpoint<DsaNoticeBody>
{
    public static string Path => "/notices";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly DsaNotices notices;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly TimeProvider clock;

    public override async Task<IResult> HandleAsync(DsaNoticeBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        if (request?.TargetKind is not { Length: > 0 } kind || request.TargetId is not { Length: > 0 } targetId)
            return addOn.Refusal(http);
        try
        {
            var id = await notices.SubmitAsync(new DsaNoticeInput(kind, targetId, request.Reason ?? "", request.Explanation ?? "",
                request.Location, request.ContactName, request.ContactEmail, request.GoodFaith), cancellationToken);
            return addOn.Envelope(new CommerceNoticeReceipt(id, clock.GetUtcNow()), StatusCodes.Status201Created);
        }
        catch (SparkValidationException ex)
        {
            return addOn.ValidationFailed(ex);
        }
    }
}

// ---------------------------------------------------------------- sellers

/// <summary><c>GET /spark/commerce/sellers/profile?organizationId=</c>: the seller's own profile (Admin/Owner).</summary>
[MemberOf<CommerceSellersGroup>]
internal sealed partial class GetSellerProfile : IGetEndpoint
{
    public static string Path => "/profile";

    [Inject] private readonly SellerRegistration registration;
    [Inject] private readonly ISparkSellers sellers;
    [Inject] private readonly IOptions<SparkCommerceOptions> options;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (httpContext.Request.Query["organizationId"].ToString() is not { Length: > 0 } organizationId)
            return addOn.Refusal(httpContext);
        var ct = httpContext.RequestAborted;
        var profile = await registration.GetAsync(organizationId, ct);
        if (profile is null)
            return addOn.Refusal(httpContext);
        var standing = await sellers.GetStandingAsync(organizationId, ct);
        var reasons = standing is null ? [] : SparkSellers.SellReasons(standing, options.Value);
        return addOn.Envelope(CommerceViews.Profile(profile, standing?.Kyc ?? profile.Kyc, SellerRegistration.MissingForSubmit(profile, options.Value), reasons), StatusCodes.Status200OK);
    }
}

/// <summary>The seller's edits; null parts stay unchanged.</summary>
public sealed record SellerProfileBody(string? OrganizationId, string? TraderStatus, CommerceTraderDataView? Trader, CommerceDac7View? Dac7);

/// <summary><c>POST /spark/commerce/sellers/profile</c>: save the profile.</summary>
[MemberOf<CommerceSellersGroup>]
internal sealed partial class SaveSellerProfile : IPostEndpoint<SellerProfileBody>
{
    public static string Path => "/profile";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly SellerRegistration registration;
    [Inject] private readonly IOptions<SparkCommerceOptions> options;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(SellerProfileBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        if (request?.OrganizationId is not { Length: > 0 } organizationId)
            return addOn.Refusal(http);
        TraderStatus? status = Enum.TryParse<TraderStatus>(request.TraderStatus, true, out var s) ? s : null;
        return await CommerceViews.GuardAsync(addOn, http, async () =>
        {
            var profile = await registration.SaveAsync(organizationId,
                new SellerProfileInput(status, request.Trader is { } t ? CommerceViews.FromView(t) : null, request.Dac7 is { } d ? CommerceViews.FromView(d) : null),
                cancellationToken);
            return CommerceViews.Profile(profile, profile.Kyc, SellerRegistration.MissingForSubmit(profile, options.Value), []);
        });
    }
}

/// <param name="AcceptedDocumentIds">The platform-terms and DPA versions the seller accepts.</param>
public sealed record SellerSubmitBody(string? OrganizationId, IReadOnlyList<string>? AcceptedDocumentIds);

/// <summary><c>POST /spark/commerce/sellers/submit</c>: submit the registration (D21).</summary>
[MemberOf<CommerceSellersGroup>]
internal sealed partial class SubmitSellerRegistration : IPostEndpoint<SellerSubmitBody>
{
    public static string Path => "/submit";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly SellerRegistration registration;
    [Inject] private readonly IOptions<SparkCommerceOptions> options;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(SellerSubmitBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        if (request?.OrganizationId is not { Length: > 0 } organizationId)
            return addOn.Refusal(http);
        return await CommerceViews.GuardAsync(addOn, http, async () =>
        {
            var profile = await registration.SubmitAsync(organizationId, request.AcceptedDocumentIds ?? [], cancellationToken);
            return CommerceViews.Profile(profile, profile.Kyc, [], []);
        });
    }
}

/// <param name="ContentBase64">The document, base64 (at most 10 MB decoded).</param>
public sealed record IdentityDocumentBody(string? OrganizationId, string? ContentType, string? ContentBase64);

/// <summary><c>POST /spark/commerce/sellers/identity-document</c>: upload the identity document (DSA Art. 30(1)(b)).</summary>
[MemberOf<CommerceSellersGroup>]
internal sealed partial class UploadIdentityDocument : IPostEndpoint<IdentityDocumentBody>
{
    public static string Path => "/identity-document";

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) { "application/pdf", "image/jpeg", "image/png" };

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly SellerRegistration registration;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(IdentityDocumentBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        if (request?.OrganizationId is not { Length: > 0 } organizationId || request.ContentBase64 is not { Length: > 0 } base64
            || request.ContentType is not { } contentType || !Allowed.Contains(contentType) || base64.Length > 14_000_000)
            return addOn.Refusal(http);
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { return addOn.Refusal(http); }
        return await CommerceViews.GuardAsync(addOn, http, async () =>
        {
            using var stream = new MemoryStream(bytes);
            await registration.UploadIdentityDocumentAsync(organizationId, stream, contentType, cancellationToken);
            return (object?)null;
        });
    }
}

/// <summary><c>GET /spark/commerce/sellers/identity-document?organizationId=</c>: download it (ViewTraderData, audited).</summary>
[MemberOf<CommerceSellersGroup>]
internal sealed partial class DownloadIdentityDocument : IGetEndpoint
{
    public static string Path => "/identity-document";

    [Inject] private readonly SellerRegistration registration;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (httpContext.Request.Query["organizationId"].ToString() is not { Length: > 0 } organizationId)
            return addOn.Refusal(httpContext);
        var file = await registration.DownloadIdentityDocumentAsync(organizationId, httpContext.RequestAborted);
        if (file is not { } f)
            return addOn.Refusal(httpContext);
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.File(f.Content, f.ContentType, "identity-document");
    }
}

public sealed record SellerComplaintBody(string? OrganizationId, string? Subject, string? Details);

/// <summary><c>POST /spark/commerce/sellers/complaint</c>: a P2B complaint (Art. 11).</summary>
[MemberOf<CommerceSellersGroup>]
internal sealed partial class FileSellerComplaint : IPostEndpoint<SellerComplaintBody>
{
    public static string Path => "/complaint";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly SellerRegistration registration;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(SellerComplaintBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        if (request?.OrganizationId is not { Length: > 0 } organizationId || string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Details))
            return addOn.Refusal(http);
        return await CommerceViews.GuardAsync(addOn, http, async () =>
            (object?)await registration.ComplainAsync(organizationId, request.Subject, request.Details, cancellationToken));
    }
}

/// <summary><c>GET /spark/commerce/sellers/export?organizationId=&amp;kind=buyers&amp;from=&amp;to=</c>: a seller's
/// export of its own buyers or attendees as CSV (GB14), audited.</summary>
[MemberOf<CommerceSellersGroup>]
internal sealed partial class ExportSellerData : IGetEndpoint
{
    public static string Path => "/export";

    [Inject] private readonly SellerExports exports;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var q = httpContext.Request.Query;
        if (q["organizationId"].ToString() is not { Length: > 0 } organizationId || q["kind"].ToString() is not { Length: > 0 } kind)
            return addOn.Refusal(httpContext);
        DateTimeOffset? from = DateTimeOffset.TryParse(q["from"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var f) ? f : null;
        DateTimeOffset? to = DateTimeOffset.TryParse(q["to"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        var csv = await exports.ExportCsvAsync(organizationId, kind, from, to, httpContext.RequestAborted);
        if (csv is null)
            return addOn.Refusal(httpContext);
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8", $"{kind}.csv");
    }
}

/// <summary>A legal-document draft. <c>Id</c> null creates one; a published id is refused (a new version is a new draft).</summary>
public sealed record LegalDocumentBody(string? Id, string? Kind, string? OrganizationId, TranslatedString? Title, TranslatedString? Body, DateTimeOffset? EffectiveFrom, string? PdfBase64, bool Publish);

/// <summary><c>POST /spark/commerce/sellers/legal</c>: save (and optionally publish) a legal document (GA2). Seller
/// documents need Admin/Owner of the organization; platform documents (no organizationId) need ManageSellers.</summary>
[MemberOf<CommerceSellersGroup>]
internal sealed partial class SaveLegalDocument : IPostEndpoint<LegalDocumentBody>
{
    public static string Path => "/legal";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly LegalDocumentEditor editor;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(LegalDocumentBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        if (request is null || (request.Id is null && string.IsNullOrWhiteSpace(request.Kind)))
            return addOn.Refusal(http);
        return await CommerceViews.GuardAsync(addOn, http, async () => await editor.SaveAsync(request, cancellationToken) is { } view ? view : throw new SparkAccessDeniedRefusal());
    }
}

// ---------------------------------------------------------------- admin

public sealed record DsaDecisionBody(string? NoticeId, bool TakeAction, string? Restriction, DateTimeOffset? RestrictionEndsAt, IReadOnlyList<string>? Territories,
    string? Facts, string? Ground, string? GroundExplanation, bool Automated);

/// <summary><c>POST /spark/commerce/admin/notices/decide</c>: decide a notice; an action issues a statement of reasons.</summary>
[MemberOf<CommerceAdminGroup>]
internal sealed partial class DecideDsaNotice : IPostEndpoint<DsaDecisionBody>
{
    public static string Path => "/notices/decide";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly DsaNotices notices;
    [Inject] private readonly CommerceAccess access;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(DsaDecisionBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        if (request?.NoticeId is not { Length: > 0 } noticeId)
            return addOn.Refusal(http);
        DsaRestriction? restriction = Enum.TryParse<DsaRestriction>(request.Restriction, true, out var r) ? r : null;
        DsaGround? ground = Enum.TryParse<DsaGround>(request.Ground, true, out var g) ? g : null;
        return await CommerceViews.GuardAsync(addOn, http, async () =>
        {
            var notice = await notices.DecideAsync(new DsaDecisionInput(noticeId, request.TakeAction, restriction, request.RestrictionEndsAt, request.Territories,
                request.Facts, ground, request.GroundExplanation, request.Automated), access.UserId, cancellationToken);
            return notice is null ? throw new SparkAccessDeniedRefusal() : (object)new { notice.Id, Status = notice.Status.ToString(), notice.StatementId };
        });
    }
}

/// <summary><c>GET /spark/commerce/admin/notices/report?from=&amp;to=</c>: the DSA transparency figures.</summary>
[MemberOf<CommerceAdminGroup>]
internal sealed partial class GetDsaTransparencyReport : IGetEndpoint
{
    public static string Path => "/notices/report";

    [Inject] private readonly DsaNotices notices;
    [Inject] private readonly TimeProvider clock;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var q = httpContext.Request.Query;
        var now = clock.GetUtcNow();
        var from = DateTimeOffset.TryParse(q["from"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var f) ? f : now.AddYears(-1);
        var to = DateTimeOffset.TryParse(q["to"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : now;
        var report = await notices.ReportAsync(from, to, httpContext.RequestAborted);
        return report is null ? addOn.Refusal(httpContext) : addOn.Envelope(report, StatusCodes.Status200OK);
    }
}

/// <summary>Thrown inside <see cref="CommerceViews.GuardAsync"/> to answer the standard refusal.</summary>
internal sealed class SparkAccessDeniedRefusal : Exception;

/// <summary>Mappings between documents and contracts, and the shared error handling.</summary>
internal static class CommerceViews
{
    public static async Task<IResult> GuardAsync(ISparkAddOnEndpoints addOn, HttpContext http, Func<Task<object?>> action)
    {
        try
        {
            return addOn.Envelope(await action(), StatusCodes.Status200OK);
        }
        catch (SparkValidationException ex)
        {
            return addOn.ValidationFailed(ex);
        }
        catch (Exception ex) when (ex is SparkAccessDeniedRefusal or MintPlayer.Spark.Abstractions.Authorization.SparkAccessDeniedException)
        {
            return addOn.Refusal(http);
        }
    }

    public static CommerceTraderPage TraderPage(SellerProfile p)
    {
        var trader = p.TraderStatus == TraderStatus.Trader;
        return new CommerceTraderPage(p.OrganizationId, p.TraderStatus.ToString(),
            trader ? p.Trader.Name : null, trader ? p.Trader.Address : null, trader ? p.Trader.Country : null,
            trader ? p.Trader.Phone : null, trader ? p.Trader.Email : null, trader ? p.Trader.TradeRegister : null,
            trader ? p.Trader.RegistrationNumber : null, trader ? p.Trader.VatId : null,
            p.Trader.SelfCertification, p.Verification?.Passed == true);
    }

    public static CommerceLegalDocumentView Legal(LegalDocument d)
        => new(d.Id!, d.Kind, d.OrganizationId, d.Version, d.EffectiveFrom, d.Title, d.Body, d.HasPdf);

    public static CommerceSellerProfileView Profile(SellerProfile p, KycStatus kyc, IReadOnlyList<string> missing, IReadOnlyList<string> sellReasons)
        => new(p.OrganizationId, p.Status.ToString(), p.StatusReason, p.TraderStatus.ToString(),
            new CommerceTraderDataView(p.Trader.Name, p.Trader.Address, p.Trader.Country, p.Trader.Phone, p.Trader.Email, p.Trader.HasIdentityDocument,
                p.Trader.PaymentAccount, p.Trader.TradeRegister, p.Trader.RegistrationNumber, p.Trader.VatId, p.Trader.SelfCertification),
            new CommerceDac7View(p.Dac7.IsEntity, p.Dac7.Tin, p.Dac7.TinCountry, p.Dac7.VatId, p.Dac7.BirthDate, p.Dac7.PrimaryAddress,
                p.Dac7.BusinessRegistrationNumber, p.Dac7.Iban, p.Dac7.AccountHolder, p.Dac7.RemindersSent),
            p.Verification?.Passed == true, kyc.ToString(), p.PayoutsBlocked, missing, sellReasons);

    public static TraderData FromView(CommerceTraderDataView v) => new()
    {
        Name = v.Name, Address = v.Address, Country = v.Country, Phone = v.Phone, Email = v.Email,
        PaymentAccount = v.PaymentAccount, TradeRegister = v.TradeRegister, RegistrationNumber = v.RegistrationNumber,
        VatId = v.VatId, SelfCertification = v.SelfCertification,
    };

    public static Dac7Data FromView(CommerceDac7View v) => new()
    {
        IsEntity = v.IsEntity, Tin = v.Tin, TinCountry = v.TinCountry, VatId = v.VatId, BirthDate = v.BirthDate,
        PrimaryAddress = v.PrimaryAddress, BusinessRegistrationNumber = v.BusinessRegistrationNumber, Iban = v.Iban, AccountHolder = v.AccountHolder,
    };
}
