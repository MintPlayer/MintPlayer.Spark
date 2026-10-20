using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Commerce.Payments.Contracts;
using MintPlayer.Spark.Commerce.Payments.Models;
using MintPlayer.Spark.Commerce.Payments.Services;
using MintPlayer.Spark.Endpoints;
using Raven.Client.Documents;

namespace MintPlayer.Spark.Commerce.Payments.Endpoints;

internal class PaymentsGroup : IEndpointGroup
{
    public static string Prefix => "/spark/commerce/payments";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) { }
}

/// <summary>
/// <c>GET /spark/commerce/payments/methods?channel=…&amp;provider=…</c>: the methods a basket of one channel may offer
/// (D3 cap from the channel's allocation mode and <c>MaxPaymentWindow</c>). Anonymous: the storefront shows them before
/// sign-in. Never filtered by the buyer's country (GA11).
/// </summary>
[MemberOf<PaymentsGroup>]
internal sealed partial class GetPaymentMethods : IGetEndpoint
{
    public static string Path => "/methods";

    [Inject] private readonly ISparkPayments payments;
    [Inject] private readonly IOptions<SparkCommerceOptions> commerce;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var query = new PaymentMethodQuery();
        if (httpContext.Request.Query["channel"].ToString() is { Length: > 0 } name)
        {
            if (!commerce.Value.Channels.TryGetValue(name, out var channel))
                return Task.FromResult(addOn.Refusal(httpContext));
            query = query with { Held = channel.Allocation != AllocationMode.OnPayment, MaxPaymentWindow = channel.MaxPaymentWindow };
        }

        IReadOnlyList<PaymentMethodInfo> methods;
        try { methods = payments.GetOfferableMethods(query, httpContext.Request.Query["provider"].ToString() is { Length: > 0 } p ? p : null); }
        catch (InvalidOperationException) { return Task.FromResult(addOn.Refusal(httpContext)); }

        return Task.FromResult(addOn.Envelope(methods
            .Select(m => new SparkPaymentMethod(m.Id, m.Provider, m.MaxExpiry is { } e ? (int)Math.Ceiling(e.TotalMinutes) : null, m.SupportsAuthorization, m.SupportsSplit))
            .ToList(), StatusCodes.Status200OK));
    }
}

public sealed record ConnectStartBody(string? OrganizationId, string? Provider);

/// <summary><c>POST /spark/commerce/payments/connect/start</c>: a seller's Owner or Admin starts linking its account (D5).</summary>
[MemberOf<PaymentsGroup>]
internal sealed partial class StartConnectOnboarding : IPostEndpoint<ConnectStartBody>
{
    public static string Path => "/connect/start";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly SellerOnboarding onboarding;
    [Inject] private readonly FinanceAccess access;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(ConnectStartBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        if (request?.OrganizationId is not { Length: > 0 } organizationId || access.CallerId is not { } me)
            return addOn.Refusal(http);
        try
        {
            var url = await onboarding.StartAsync(request.Provider, organizationId, me, cancellationToken);
            return addOn.Envelope(new SparkConnectStartResponse(url), StatusCodes.Status200OK);
        }
        catch (SparkAccessDeniedException) { return addOn.Refusal(http); }
        catch (SparkValidationException ex) { return addOn.ValidationFailed(ex); }
    }
}

/// <summary>
/// <c>GET /spark/commerce/payments/connect/callback?state=…&amp;code=…</c>: the provider sends the seller back here.
/// Redirects to the configured return page (with <c>?connect=done</c> or <c>?connect=failed</c>).
/// </summary>
[MemberOf<PaymentsGroup>]
internal sealed partial class CompleteConnectOnboarding : IGetEndpoint
{
    public static string Path => "/connect/callback";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization();

    [Inject] private readonly SellerOnboarding onboarding;
    [Inject] private readonly FinanceAccess access;
    [Inject] private readonly IOptionsMonitor<SparkCommercePaymentsOptions> options;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        var query = httpContext.Request.Query;
        if (query["state"].ToString() is not { Length: > 0 } state || access.CallerId is not { } me)
            return addOn.Refusal(httpContext);

        var returnPath = options.CurrentValue.Connect.ReturnPath;
        // The seller declined at the provider (OAuth error=access_denied): nothing is linked.
        if (query["error"].ToString() is { Length: > 0 })
            return Results.LocalRedirect(Append(returnPath, "connect=failed"));
        try
        {
            var target = await onboarding.CompleteAsync(state, query["code"].ToString() is { Length: > 0 } code ? code : null,
                query["refresh"].ToString() == "1", me, httpContext.RequestAborted);
            // A refresh answers the provider's new URL (absolute); a completion the app's page (local).
            return Uri.TryCreate(target, UriKind.Absolute, out var absolute) && absolute.Scheme == Uri.UriSchemeHttps
                ? Results.Redirect(target)
                : Results.LocalRedirect(Append(target, "connect=done"));
        }
        catch (SparkAccessDeniedException) { return addOn.Refusal(httpContext); }
        catch (SparkValidationException) { return Results.LocalRedirect(Append(returnPath, "connect=failed")); }
    }

    private static string Append(string path, string parameter) => path + (path.Contains('?') ? "&" : "?") + parameter;
}

/// <summary><c>GET /spark/commerce/payments/connect/status?organizationId=…</c>: the seller's link and KYC state (its Admins and finance).</summary>
[MemberOf<PaymentsGroup>]
internal sealed partial class GetSellerPaymentStatus : IGetEndpoint
{
    public static string Path => "/connect/status";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization();

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly FinanceAccess access;
    [Inject] private readonly PaymentProviderRegistry registry;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (httpContext.Request.Query["organizationId"].ToString() is not { Length: > 0 } organizationId
            || !await access.MaySeeAsync(organizationId, httpContext.RequestAborted))
            return addOn.Refusal(httpContext);

        var provider = registry.DefaultName;
        using var session = store.OpenAsyncSession();
        var account = await session.LoadAsync<SellerPaymentAccount>(SellerPaymentAccount.IdFor(provider, organizationId), httpContext.RequestAborted);
        return addOn.Envelope(new SparkSellerPaymentStatus(
            provider,
            account?.AccountId is not null,
            (SparkKycStatus)(int)(account?.Kyc ?? KycStatus.NotStarted),
            account?.ChargesEnabled ?? false,
            account?.PayoutsEnabled ?? false,
            account?.Requirements ?? []), StatusCodes.Status200OK);
    }
}

/// <summary><c>GET /spark/commerce/payments/disputes/evidence?id=…</c>: the evidence pack as a text download (GB1; Mollie's dashboard upload).</summary>
[MemberOf<PaymentsGroup>]
internal sealed partial class DownloadDisputeEvidence : IGetEndpoint
{
    public static string Path => "/disputes/evidence";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization();

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly FinanceAccess access;
    [Inject] private readonly ISparkPayments payments;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (httpContext.Request.Query["id"].ToString() is not { Length: > 0 } id)
            return addOn.Refusal(httpContext);
        using (var session = store.OpenAsyncSession())
        {
            var dispute = await session.LoadAsync<PaymentDispute>(id, httpContext.RequestAborted);
            if (dispute is null || !await access.MaySeeAsync(dispute.SellerOrganizationId, httpContext.RequestAborted))
                return addOn.Refusal(httpContext);
        }
        var pack = await payments.AssembleEvidenceAsync(id, httpContext.RequestAborted);
        var fileName = "dispute-" + new string(id.Where(char.IsLetterOrDigit).ToArray()) + ".txt";
        return Results.File(Encoding.UTF8.GetBytes(pack.ToText()), "text/plain; charset=utf-8", fileName);
    }
}

public sealed record SubmitEvidenceBody(string? Id);

/// <summary><c>POST /spark/commerce/payments/disputes/submit-evidence</c>: submits the pack where the provider takes it by API (Stripe).</summary>
[MemberOf<PaymentsGroup>]
internal sealed partial class SubmitDisputeEvidence : IPostEndpoint<SubmitEvidenceBody>
{
    public static string Path => "/disputes/submit-evidence";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly IDocumentStore store;
    [Inject] private readonly FinanceAccess access;
    [Inject] private readonly ISparkPayments payments;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override async Task<IResult> HandleAsync(SubmitEvidenceBody request, CancellationToken cancellationToken)
    {
        var http = httpContextAccessor.HttpContext!;
        if (request?.Id is not { Length: > 0 } id || access.CallerId is not { } me)
            return addOn.Refusal(http);
        using (var session = store.OpenAsyncSession())
        {
            var dispute = await session.LoadAsync<PaymentDispute>(id, cancellationToken);
            if (dispute is null || !await access.MaySeeAsync(dispute.SellerOrganizationId, cancellationToken))
                return addOn.Refusal(http);
        }
        var result = await payments.SubmitEvidenceAsync(id, me, cancellationToken);
        return addOn.Envelope(new SparkEvidenceSubmissionResponse((SparkEvidenceSubmission)(int)result), StatusCodes.Status200OK);
    }
}
