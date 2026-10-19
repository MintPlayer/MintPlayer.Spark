using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Commerce.Checkout.Contracts;
using MintPlayer.Spark.Commerce.Checkout.Endpoints;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Endpoints;

namespace MintPlayer.Spark.Commerce.Checkout.Refunds;

// The refund flows' endpoints (PRD §4.7), under /spark/commerce/checkout/refunds. The buyer's own order is found by
// number (account or visitor) or by the signed guest link's token (`access`); staff endpoints need the seller's
// box-office or finance role. Refusals answer 409 with SparkCheckoutRefusal, "not yours" the missing-row 404.

internal static class RefundEndpointSupport
{
    public static T Parse<T>(string? value, string what) where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw CheckoutException.Of(CheckoutRefusals.Conflict, subject: what);

    public static T? ParseOptional<T>(string? value, string what) where T : struct, Enum
        => string.IsNullOrWhiteSpace(value) ? null : Parse<T>(value, what);

    public static async Task<CheckoutOrder> BuyerOrderAsync(RefundService refunds, string? number, string? accessToken, CancellationToken cancellationToken)
        => number is { Length: > 0 } && await refunds.BuyerOrderAsync(number, accessToken, cancellationToken) is { } order
            ? order : throw CheckoutException.Of(CheckoutRefusals.OrderNotFound);

    public static string Actor(RefundService refunds) => refunds.CallerId ?? throw CheckoutException.Of(CheckoutRefusals.Forbidden);
}

/// <summary><c>GET /spark/commerce/checkout/refunds/order?number=&amp;access=</c>: the order's after-sale state, with the withdrawal function (GA1).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class GetOrderAftercare : IGetEndpoint
{
    public static string Path => "/refunds/order";

    [Inject] private readonly RefundService refunds;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => CheckoutResults.RunAsync(addOn, httpContext, async () =>
        {
            var query = httpContext.Request.Query;
            var order = await refunds.BuyerOrderAsync(query["number"].ToString(), query["access"].ToString(), httpContext.RequestAborted);
            return order is null ? null : await refunds.ViewAsync(order, httpContext.RequestAborted);
        });
}

/// <summary><c>POST /spark/commerce/checkout/refunds/withdraw</c>: GA1, preview then confirm; no approval step.</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class WithdrawFromOrder : IPostEndpoint<SparkWithdrawalRequest>
{
    public static string Path => "/refunds/withdraw";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly RefundService refunds;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkWithdrawalRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request is null)
                return null;
            var order = await RefundEndpointSupport.BuyerOrderAsync(refunds, request.Number, request.Access, cancellationToken);
            return await refunds.WithdrawAsync(order, request.Lines, request.Note, request.Name, request.Email, ReturnSource.Online, DateTimeOffset.MinValue,
                refunds.CallerId, request.Confirm, cancellationToken);
        });
}

/// <summary><c>POST /spark/commerce/checkout/refunds/cancel</c>: a cancellation within the seller's policy (D20).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class CancelOrderLines : IPostEndpoint<SparkCancellationRequest>
{
    public static string Path => "/refunds/cancel";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly RefundService refunds;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkCancellationRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request is null)
                return null;
            var order = await RefundEndpointSupport.BuyerOrderAsync(refunds, request.Number, request.Access, cancellationToken);
            return await refunds.CancelAsync(order, request.Lines, request.Confirm, refunds.CallerId, cancellationToken);
        });
}

/// <summary><c>POST /spark/commerce/checkout/refunds/claim</c>: a legal-guarantee claim (GA8).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class OpenGuaranteeClaim : IPostEndpoint<SparkGuaranteeClaimRequest>
{
    public static string Path => "/refunds/claim";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly RefundService refunds;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkGuaranteeClaimRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request?.Lines is null)
                return null;
            var order = await RefundEndpointSupport.BuyerOrderAsync(refunds, request.Number, request.Access, cancellationToken);
            return await refunds.OpenClaimAsync(order, request.Lines, request.Description, RefundEndpointSupport.ParseOptional<GuaranteeRemedy>(request.PreferredRemedy, "preferredRemedy"),
                ReturnSource.Online, refunds.CallerId, cancellationToken);
        });
}

/// <summary><c>POST /spark/commerce/checkout/refunds/reschedule</c>: keep or refund after a reschedule (D20).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class ChooseReschedule : IPostEndpoint<SparkRescheduleChoiceRequest>
{
    public static string Path => "/refunds/reschedule";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly RefundService refunds;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkRescheduleChoiceRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request?.WindowId is null)
                return null;
            var order = await RefundEndpointSupport.BuyerOrderAsync(refunds, request.Number, request.Access, cancellationToken);
            return await refunds.ChooseRescheduleAsync(order, request.WindowId, request.Choice, refunds.CallerId, cancellationToken);
        });
}

// ---- staff ----------------------------------------------------------------------------------------------------------

/// <summary><c>GET /spark/commerce/checkout/refunds/staff/order?orderId=</c>: the after-sale state for the seller's box office.</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class GetStaffAftercare : IGetEndpoint
{
    public static string Path => "/refunds/staff/order";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.RequireAuthorization();

    [Inject] private readonly RefundService refunds;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => CheckoutResults.RunAsync(addOn, httpContext, async () =>
            await refunds.ViewAsync(await refunds.StaffOrderAsync(httpContext.Request.Query["orderId"].ToString(), refunds.BackOfficeRole, httpContext.RequestAborted), httpContext.RequestAborted));
}

/// <summary><c>POST …/refunds/staff/withdrawal</c>: a withdrawal received by mail or phone (box office).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class RegisterStaffWithdrawal : IPostEndpoint<SparkStaffWithdrawalRequest>
{
    public static string Path => "/refunds/staff/withdrawal";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly RefundService refunds;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkStaffWithdrawalRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request?.OrderId is null)
                return null;
            var order = await refunds.StaffOrderAsync(request.OrderId, refunds.BackOfficeRole, cancellationToken);
            var source = RefundEndpointSupport.Parse<ReturnSource>(request.Source, "source");
            if (source is not (ReturnSource.Mail or ReturnSource.Phone))
                throw CheckoutException.Of(CheckoutRefusals.Conflict, subject: "source");
            return await refunds.WithdrawAsync(order, request.Lines, request.Note, request.Name, request.Email, source, request.ReceivedAt,
                RefundEndpointSupport.Actor(refunds), confirm: true, cancellationToken);
        });
}

/// <summary><c>POST …/refunds/staff/goods-back</c>: goods received or dispatch proven (box office).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class RegisterGoodsBack : IPostEndpoint<SparkGoodsBackRequest>
{
    public static string Path => "/refunds/staff/goods-back";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly RefundService refunds;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;
    [Inject] private readonly TimeProvider clock;

    public override Task<IResult> HandleAsync(SparkGoodsBackRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request?.OrderId is null || request.ReturnId is null)
                return null;
            var order = await refunds.StaffOrderAsync(request.OrderId, refunds.BackOfficeRole, cancellationToken);
            await refunds.RegisterGoodsBackAsync(new GoodsBackRegistration(order.Id!, request.ReturnId,
                RefundEndpointSupport.Parse<GoodsBackKind>(request.Kind, "kind"), request.At ?? clock.GetUtcNow(),
                RefundEndpointSupport.ParseOptional<ReturnDisposition>(request.Disposition, "disposition"),
                request.Deductions?.Select(d => new DiminishedValue(d.LineId, d.Amount, d.Reason)).ToList(),
                RefundEndpointSupport.Actor(refunds)), cancellationToken);
            return await refunds.ViewAsync(await refunds.StaffOrderAsync(order.Id!, refunds.BackOfficeRole, cancellationToken), cancellationToken);
        });
}

/// <summary><c>POST …/refunds/staff/refund</c>: a refund by return reason (finance).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class StaffRefund : IPostEndpoint<SparkStaffRefundRequest>
{
    public static string Path => "/refunds/staff/refund";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly RefundService refunds;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkStaffRefundRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request?.OrderId is null)
                return null;
            var order = await refunds.StaffOrderAsync(request.OrderId, refunds.FinanceRole, cancellationToken);
            return await refunds.StaffRefundAsync(order, RefundEndpointSupport.Parse<ReturnReasonCode>(request.Reason, "reason"), request.Lines, request.Amount, request.Note,
                RefundEndpointSupport.Actor(refunds), cancellationToken);
        });
}

/// <summary><c>POST …/refunds/staff/claim</c>: a guarantee claim's remedy (finance).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class ResolveGuaranteeClaim : IPostEndpoint<SparkClaimResolutionRequest>
{
    public static string Path => "/refunds/staff/claim";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly RefundService refunds;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkClaimResolutionRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request?.OrderId is null || request.ClaimId is null)
                return null;
            var order = await refunds.StaffOrderAsync(request.OrderId, refunds.FinanceRole, cancellationToken);
            return await refunds.ResolveClaimAsync(order, request.ClaimId, RefundEndpointSupport.Parse<GuaranteeRemedy>(request.Remedy, "remedy"),
                RefundEndpointSupport.ParseOptional<GuaranteeGround>(request.Ground, "ground"), request.Amount, request.Note, RefundEndpointSupport.Actor(refunds), cancellationToken);
        });
}

/// <summary><c>POST …/refunds/staff/retry</c>: retry a failed refund (finance, D20).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class RetryRefund : IPostEndpoint<SparkRefundActionRequest>
{
    public static string Path => "/refunds/staff/retry";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly RefundService refunds;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkRefundActionRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request?.OrderId is null || request.RefundId is null)
                return null;
            var order = await refunds.StaffOrderAsync(request.OrderId, refunds.FinanceRole, cancellationToken);
            return await refunds.RetryAsync(order.Id!, request.RefundId, cancellationToken);
        });
}

/// <summary><c>POST …/refunds/staff/settle</c>: the manual fallback (finance, audited, D20).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class SettleRefundManually : IPostEndpoint<SparkRefundActionRequest>
{
    public static string Path => "/refunds/staff/settle";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly RefundService refunds;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkRefundActionRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
        {
            if (request?.OrderId is null || request.RefundId is null)
                return null;
            var order = await refunds.StaffOrderAsync(request.OrderId, refunds.FinanceRole, cancellationToken);
            return await refunds.SettleManuallyAsync(order.Id!, request.RefundId, request.Note, RefundEndpointSupport.Actor(refunds), cancellationToken);
        });
}

/// <summary><c>POST …/refunds/staff/mass</c>: the event was cancelled (finance, D20).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class StartMassRefund : IPostEndpoint<SparkMassRefundRequest>
{
    public static string Path => "/refunds/staff/mass";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly MassRefunds mass;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkMassRefundRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request?.ItemIds is null ? null : await mass.StartAsync(request, cancellationToken));
}

/// <summary><c>GET …/refunds/staff/mass?id=</c>: progress.</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class GetMassRefund : IGetEndpoint
{
    public static string Path => "/refunds/staff/mass";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services) => builder.RequireAuthorization();

    [Inject] private readonly MassRefunds mass;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => CheckoutResults.RunAsync(addOn, httpContext, async () => await mass.GetAsync(httpContext.Request.Query["id"].ToString(), httpContext.RequestAborted));
}

/// <summary><c>POST …/refunds/staff/mass/control</c>: pause, resume, cancel, retryFailed.</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class ControlMassRefund : IPostEndpoint<SparkMassRefundControlRequest>
{
    public static string Path => "/refunds/staff/mass/control";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly MassRefunds mass;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkMassRefundControlRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request?.MassRefundId is null ? null : await mass.ControlAsync(request, cancellationToken));
}

/// <summary><c>POST …/refunds/staff/reschedule</c>: open a keep-or-refund window and mail the buyers (finance, D20).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class OpenRescheduleWindow : IPostEndpoint<SparkRescheduleWindowRequest>
{
    public static string Path => "/refunds/staff/reschedule";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly MassRefunds mass;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkRescheduleWindowRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request?.ItemIds is null ? null : await mass.RescheduleAsync(request, cancellationToken));
}
