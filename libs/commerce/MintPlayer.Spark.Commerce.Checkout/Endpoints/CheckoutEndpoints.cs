using Microsoft.AspNetCore.Antiforgery;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Commerce.Checkout.Contracts;
using MintPlayer.Spark.Commerce.Checkout.Models;
using MintPlayer.Spark.Commerce.Checkout.Services;
using MintPlayer.Spark.Endpoints;

namespace MintPlayer.Spark.Commerce.Checkout.Endpoints;

internal class CheckoutGroup : IEndpointGroup
{
    public static string Prefix => "/spark/commerce/checkout";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) { }
}

/// <summary>Maps checkout refusals: a <see cref="CheckoutRefusals"/> code as 409 with <see cref="SparkCheckoutRefusal"/>; "not yours" as the missing-row 404.</summary>
internal static class CheckoutResults
{
    public static async Task<IResult> RunAsync(ISparkAddOnEndpoints addOn, HttpContext http, Func<Task<object?>> action)
    {
        try
        {
            var result = await action();
            return result is null ? addOn.Refusal(http) : addOn.Envelope(result, StatusCodes.Status200OK);
        }
        catch (CheckoutException ex) when (ex.Code is CheckoutRefusals.Forbidden or CheckoutRefusals.OrderNotFound)
        {
            return addOn.Refusal(http);
        }
        catch (CheckoutException ex)
        {
            return addOn.Envelope(new SparkCheckoutRefusal(ex.Code, ex.LineId, ex.Subject), StatusCodes.Status409Conflict);
        }
    }

    public static SparkOrderView View(CheckoutOrder order, CheckoutMails mails) => new(
        order.Number, order.State.ToString(), order.Total, order.AmountRefunded,
        order.Lines.Select(l => new SparkOrderLine(l.LineId, l.Title, l.Quantity, l.Units, l.Total, l.Status.ToString(), l.IdCheckRequired)).ToList(),
        order.Extras.Select(e => new SparkCheckoutAmount(e.Code, e.Kind.ToString(), e.Amount, e.LabelKey)).ToList(),
        order.CreatedAt, order.ConfirmedAt, order.PaymentPendingUntil,
        order.State == OrderState.PaymentPending ? order.Payment?.CheckoutUrl : null);
}

/// <summary><c>GET /spark/commerce/checkout/basket</c>: the current buyer's basket (account, else visitor). Anonymous.</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class GetBasket : IGetEndpoint
{
    public static string Path => "/basket";

    [Inject] private readonly ISparkCheckout checkout;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => CheckoutResults.RunAsync(addOn, httpContext, async () => await checkout.GetBasketAsync(httpContext.RequestAborted));
}

/// <summary><c>POST /spark/commerce/checkout/basket/add</c>. A guest gets the strictly necessary visitor cookie here (X1).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class AddBasketLine : IPostEndpoint<SparkBasketAddRequest>
{
    public static string Path => "/basket/add";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkCheckout checkout;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkBasketAddRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await checkout.AddLineAsync(request, cancellationToken));
}

/// <summary><c>POST /spark/commerce/checkout/basket/quantity</c>: 0 removes the line (and its add-ons).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class SetBasketQuantity : IPostEndpoint<SparkBasketQuantityRequest>
{
    public static string Path => "/basket/quantity";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkCheckout checkout;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkBasketQuantityRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request?.LineId is null ? null : await checkout.SetQuantityAsync(request.LineId, request.Quantity, cancellationToken));
}

/// <summary><c>POST /spark/commerce/checkout/basket/choices</c>: donation (GC3), insurance (GC4), codes; a guest's e-mail.</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class SetBasketChoices : IPostEndpoint<SparkBasketChoicesRequest>
{
    public static string Path => "/basket/choices";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkCheckout checkout;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkBasketChoicesRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null
            : await checkout.SetChoicesAsync(request.Choices ?? new Dictionary<string, string?>(), request.Email, cancellationToken));
}

/// <summary><c>GET /spark/commerce/checkout/summary</c>: the last step (GA2).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class GetCheckoutSummary : IGetEndpoint
{
    public static string Path => "/summary";

    [Inject] private readonly ISparkCheckout checkout;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => CheckoutResults.RunAsync(addOn, httpContext, async () => await checkout.GetSummaryAsync(httpContext.RequestAborted));
}

/// <summary><c>POST /spark/commerce/checkout/place</c>: "Order with obligation to pay" (CRD Art. 8(2)).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class PlaceOrder : IPostEndpoint<SparkPlaceOrderRequest>
{
    public static string Path => "/place";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkCheckout checkout;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkPlaceOrderRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await checkout.PlaceOrderAsync(request, cancellationToken));
}

/// <summary><c>GET /spark/commerce/checkout/order?number=</c>: the buyer's own order (the provider's redirect lands on its page).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class GetOwnOrder : IGetEndpoint
{
    public static string Path => "/order";

    [Inject] private readonly ISparkCheckout checkout;
    [Inject] private readonly CheckoutMails mails;
    [Inject] private readonly ISparkAddOnEndpoints addOn;

    public Task<IResult> HandleAsync(HttpContext httpContext)
        => CheckoutResults.RunAsync(addOn, httpContext, async () =>
            await checkout.GetOwnOrderAsync(httpContext.Request.Query["number"].ToString(), httpContext.RequestAborted) is { } order
                ? CheckoutResults.View(order, mails) : null);
}

/// <summary><c>POST /spark/commerce/checkout/order/cancel-payment</c>: the buyer abandons the payment (D3).</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class CancelOrderPayment : IPostEndpoint<SparkOrderNumberRequest>
{
    public static string Path => "/order/cancel-payment";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly ISparkCheckout checkout;
    [Inject] private readonly CheckoutMails mails;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkOrderNumberRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () =>
            request?.Number is { } number && await checkout.CancelPaymentAsync(number, cancellationToken) is { } order ? CheckoutResults.View(order, mails) : null);
}

// ---- back office (GB7): signed in, the seller's box-office role (finance for mark-paid) ----------------------------

/// <summary><c>POST /spark/commerce/checkout/box-office/orders</c>.</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class CreateBackOfficeOrder : IPostEndpoint<SparkBackOfficeOrderRequest>
{
    public static string Path => "/box-office/orders";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly BackOfficeOrders backOffice;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkBackOfficeOrderRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request is null ? null : await backOffice.CreateAsync(request, cancellationToken));
}

/// <summary><c>POST /spark/commerce/checkout/box-office/mark-paid</c>: finance only, audited.</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class MarkOrderPaid : IPostEndpoint<SparkMarkPaidRequest>
{
    public static string Path => "/box-office/mark-paid";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly BackOfficeOrders backOffice;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkMarkPaidRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request?.OrderId is null ? null : await backOffice.MarkPaidAsync(request, cancellationToken));
}

/// <summary><c>POST /spark/commerce/checkout/box-office/resend</c>.</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class ResendOrderConfirmation : IPostEndpoint<SparkOrderIdRequest>
{
    public static string Path => "/box-office/resend";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly BackOfficeOrders backOffice;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkOrderIdRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request?.OrderId is null ? null : await backOffice.ResendAsync(request.OrderId, cancellationToken));
}

/// <summary><c>POST /spark/commerce/checkout/box-office/change-line</c>: seat change or upgrade.</summary>
[MemberOf<CheckoutGroup>]
internal sealed partial class ChangeOrderLine : IPostEndpoint<SparkLineChangeRequest>
{
    public static string Path => "/box-office/change-line";

    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.RequireAuthorization().WithMetadata(new RequireAntiforgeryTokenAttribute(true));

    [Inject] private readonly BackOfficeOrders backOffice;
    [Inject] private readonly ISparkAddOnEndpoints addOn;
    [Inject] private readonly IHttpContextAccessor httpContextAccessor;

    public override Task<IResult> HandleAsync(SparkLineChangeRequest request, CancellationToken cancellationToken)
        => CheckoutResults.RunAsync(addOn, httpContextAccessor.HttpContext!, async () => request?.OrderId is null ? null : await backOffice.ChangeLineAsync(request, cancellationToken));
}
