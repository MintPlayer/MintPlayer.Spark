using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MintPlayer.AspNetCore.Endpoints;
using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Commerce.Payments.Test;

internal static class TestPaymentEndpoints
{
    public const string PayPath = "/spark/commerce/payments/test/pay";

    /// <summary>The pages exist only outside Production, or when the host allows the test provider there (a demo).</summary>
    public static bool Enabled(IHostEnvironment environment, TestPaymentsOptions options)
        => !environment.IsProduction() || options.AllowInProduction;
}

internal class TestPaymentsGroup : IEndpointGroup
{
    public static string Prefix => "/spark/commerce/payments/test";

    static void IEndpointGroup.Configure(RouteGroupBuilder group, IServiceProvider services) { }
}

/// <summary><c>GET /spark/commerce/payments/test/pay?id=tst_…</c>: the test provider's payment page, one button per outcome.</summary>
[MemberOf<TestPaymentsGroup>]
internal sealed partial class TestPaymentPage : IGetEndpoint
{
    public static string Path => "/pay";

    [Inject] private readonly TestPaymentStore store;
    [Inject] private readonly IHostEnvironment environment;
    [Inject] private readonly IOptionsMonitor<TestPaymentsOptions> options;

    public Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (!TestPaymentEndpoints.Enabled(environment, options.CurrentValue)
            || !store.Payments.TryGetValue(httpContext.Request.Query["id"].ToString(), out var payment))
            return Task.FromResult(Results.NotFound());

        var html = HtmlEncoder.Default;
        var buttons = new StringBuilder();
        foreach (var (outcome, label) in new[]
        {
            ("Pay", "Pay"), ("Authorize", "Authorize only"), ("Pending", "Pending (delayed method)"), ("Fail", "Fail"),
            ("Cancel", "Cancel"), ("Expire", "Expire"), ("PayLate", "Pay, webhook late"), ("PayDuplicate", "Pay, webhook twice"),
        })
        {
            buttons.Append($"<button type=\"submit\" name=\"outcome\" value=\"{outcome}\">{html.Encode(label)}</button> ");
        }
        var page = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Test payment</title>
            <style>body{font-family:system-ui,sans-serif;max-width:40rem;margin:2rem auto;padding:0 1rem}button{margin:.25rem 0;padding:.5rem .75rem}</style>
            </head><body>
            <h1>Test payment provider</h1>
            <p>{{html.Encode(payment.Reference)}}: {{html.Encode(payment.Amount.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture))}} {{html.Encode(payment.Amount.Currency)}}</p>
            <p>Status: {{html.Encode(payment.Status.ToString())}}. No money moves: this provider only simulates.</p>
            <form method="post" action="simulate"><input type="hidden" name="id" value="{{html.Encode(payment.Id)}}">{{buttons}}</form>
            </body></html>
            """;
        return Task.FromResult(Results.Content(page, "text/html; charset=utf-8"));
    }
}

/// <summary><c>POST /spark/commerce/payments/test/simulate</c> (form: <c>id</c>, <c>outcome</c>): simulates, then returns the buyer.</summary>
[MemberOf<TestPaymentsGroup>]
internal sealed partial class TestPaymentSimulate : IPostEndpoint, IEndpointBase
{
    public static string Path => "/simulate";

    // The page is a plain HTML form of the simulated provider (no SPA, no antiforgery token), as a provider's page would be.
    // It moves no money and exists only outside Production unless AllowInProduction.
    static void IEndpointBase.Configure(RouteHandlerBuilder builder, IServiceProvider services)
        => builder.WithMetadata(new RequireAntiforgeryTokenAttribute(false));

    [Inject] private readonly TestPaymentStore store;
    [Inject] private readonly TestPaymentSimulator simulator;
    [Inject] private readonly IHostEnvironment environment;
    [Inject] private readonly IOptionsMonitor<TestPaymentsOptions> options;

    public async Task<IResult> HandleAsync(HttpContext httpContext)
    {
        if (!TestPaymentEndpoints.Enabled(environment, options.CurrentValue) || !httpContext.Request.HasFormContentType)
            return Results.NotFound();
        var form = await httpContext.Request.ReadFormAsync(httpContext.RequestAborted);
        if (!store.Payments.TryGetValue(form["id"].ToString(), out var payment))
            return Results.NotFound();

        var (outcome, delivery) = form["outcome"].ToString() switch
        {
            "Pay" => (TestPaymentOutcome.Pay, TestDelivery.Immediate),
            "Authorize" => (TestPaymentOutcome.Authorize, TestDelivery.Immediate),
            "Pending" => (TestPaymentOutcome.Pending, TestDelivery.Immediate),
            "Fail" => (TestPaymentOutcome.Fail, TestDelivery.Immediate),
            "Cancel" => (TestPaymentOutcome.Cancel, TestDelivery.Immediate),
            "Expire" => (TestPaymentOutcome.Expire, TestDelivery.Immediate),
            "PayLate" => (TestPaymentOutcome.Pay, TestDelivery.Late),
            "PayDuplicate" => (TestPaymentOutcome.Pay, TestDelivery.Duplicate),
            _ => ((TestPaymentOutcome?)null, TestDelivery.None),
        };
        if (outcome is null)
            return Results.BadRequest();
        await simulator.SimulateAsync(payment.Id, outcome.Value, delivery, httpContext.RequestAborted);
        // The redirect URL is the one Checkout gave when creating the payment (absolute, our own host).
        return Results.Redirect(payment.RedirectUrl);
    }
}
